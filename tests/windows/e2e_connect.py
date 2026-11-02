"""End-to-end test of x14javaservice.exe as the Windows service "Sorcerer" with a real Kafka
Connect, on a machine without internet.

Run as administrator on a Windows test machine (not app10/app11) with Python 3 and psutil:

    python e2e_connect.py <x14javaservice.exe> <pki-dir> <jdk-dir> <kafka-dir>

<pki-dir> comes from msi-agent/watchdog/tests/make-pki.sh with the keystore password abc=def
(P-384, leaves with only clientAuth, PKCS12 without chain, as in production). <jdk-dir> and
<kafka-dir> are Temurin JDK 17 and Kafka 3.9.1 as msi-agent/tools/download.pl fetches them
(fetch_connect.py).

Lays Sorcerer out in C:\\sorcerer-test like the MSI: bin\\x14javaservice.exe, jdk, kafka and
config, with java.exe ConnectStandalone as the child. The REST listener is HTTPS://127.0.0.1:0
with the worker's ssl.* and ssl.client.auth=required. Two connectors (connect/FlakySource.java)
fail once on command, standing in for Debezium. A single-node Kafka broker runs from <jdk-dir>,
outside the install directory, on 127.0.0.1:19092.

For the whole run a firewall rule blocks outbound connections to anything but loopback and
private addresses, like app10/app11, which reach their databases and brokers but not the
internet. Blocked connections are audited (Security 5157), so the test can tell whether Sorcerer
tried to go further than 127.0.0.1. Health check every 10 s, starting 60 s after each start of
the child. Takes about 20 minutes.
"""
import base64
import datetime
import hashlib
import http.client
import json
import os
import re
import shutil
import socket
import ssl
import subprocess
import sys
import time
import uuid
import winreg

import psutil

from sorcerer_test import SVC, FakeDb, check, events, ps, sh, summary, wait_event, wait_for

EXE, PKI, JDK, KAFKA = (os.path.abspath(a) for a in sys.argv[1:5])
BASE = r"C:\sorcerer-test"
BROKER = r"C:\sorcerer-test-broker"
HERE = os.path.dirname(os.path.abspath(__file__))
ANTURA_DB, LIME_DB = 14390, 14391
BROKER_PORT = 19092
FW_RULE = "sorcerer-e2e-offline"
WFP_AUDIT = "{0CCE9226-69AE-11D9-BED3-505054503030}"  # Filtering Platform Connection
FLAGS = os.path.join(BASE, "flags")
PLUGIN_JAR = os.path.join(BASE, "plugins", "flaky", "flaky.jar")
# Everything but 127/8, 10/8, 172.16/12, 192.168/16 and multicast, and global IPv6.
INTERNET = ["0.0.0.0-9.255.255.255", "11.0.0.0-126.255.255.255", "128.0.0.0-172.15.255.255",
            "172.32.0.0-192.167.255.255", "192.169.0.0-223.255.255.255", "2000::/3"]


def worker_der():
    with open(os.path.join(PKI, "source_test_x14.crt"), encoding="ascii") as f:
        return ssl.PEM_cert_to_DER_cert(f.read())


def rest(port, path="/connectors?expand=status", client_cert=True, timeout=10):
    """(status, parsed body, server certificate DER) of one request to Connect's REST API."""
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    if client_cert:
        ctx.load_cert_chain(os.path.join(PKI, "source_test_x14.crt"), os.path.join(PKI, "source_test_x14.key"))
    conn = http.client.HTTPSConnection("127.0.0.1", port, context=ctx, timeout=timeout)
    try:
        conn.request("GET", path)
        response = conn.getresponse()
        body = response.read()
        der = conn.sock.getpeercert(binary_form=True)
        return response.status, json.loads(body) if body else None, der
    finally:
        conn.close()


def states(port):
    """{connector: (state, [task states])}, or None when the REST API does not answer."""
    try:
        code, body, _ = rest(port)
    except (OSError, ssl.SSLError, http.client.HTTPException, ValueError):
        return None
    if code != 200:
        return None
    return {name: (c["status"]["connector"]["state"], [t["state"] for t in c["status"]["tasks"]])
            for name, c in body.items()}


def all_running(port):
    s = states(port)
    return s if s and len(s) == 2 and all(c == "RUNNING" and t == ["RUNNING"] for c, t in s.values()) else None


def task_state(port, name):
    s = states(port)
    return s[name][1][0] if s and name in s and s[name][1] else None


def fail_once(name):
    open(os.path.join(FLAGS, name + ".fail"), "w").close()


def rest_port(since, timeout=120):
    """The port in the newest 1006 event since a time."""
    def find():
        found = [e for e in events(since, ids=[1006])]
        m = re.search(r"https://127\.0\.0\.1:(\d+)", found[-1][2]) if found else None
        return int(m.group(1)) if m else None
    return wait_for(find, timeout, 2)


def under_base(p):
    try:
        return p.exe().lower().startswith(BASE.lower() + "\\")
    except (psutil.Error, OSError):
        return False


def our_javas():
    """java.exe processes whose executable lies in the install directory."""
    return [p for p in psutil.process_iter(["name"]) if (p.info["name"] or "").lower() == "java.exe" and under_base(p)]


def one_java(other_than=None):
    javas = our_javas()
    return javas[0] if len(javas) == 1 and javas[0].pid != other_than else None


def service_process():
    return next((p for p in psutil.process_iter(["name"]) if p.info["name"] == "x14javaservice.exe"), None)


def reachable(host, port, timeout=10):
    try:
        socket.create_connection((host, port), timeout=timeout).close()
        return True
    except OSError:
        return False


def write(path, text):
    with open(path, "w", encoding="ascii") as f:
        f.write(text)


def props_path(path):
    return path.replace("\\", "\\\\")


def remove_service():
    sh("sc.exe", "stop", SVC)
    wait_for(lambda: "STOPPED" in sh("sc.exe", "query", SVC).stdout or "1060" in sh("sc.exe", "query", SVC).stdout, 30)
    sh("sc.exe", "delete", SVC)
    for p in our_javas():
        p.kill()


def install():
    remove_service()
    shutil.rmtree(BASE, ignore_errors=True)
    for d in ("bin", "config", "pki", "logs", "flags", "build", r"plugins\flaky"):
        os.makedirs(os.path.join(BASE, d))
    shutil.copy(EXE, os.path.join(BASE, "bin", "x14javaservice.exe"))
    shutil.copytree(JDK, os.path.join(BASE, "jdk"))
    shutil.copytree(KAFKA, os.path.join(BASE, "kafka"))
    for f in os.listdir(PKI):
        shutil.copy(os.path.join(PKI, f), os.path.join(BASE, "pki"))

    jdk_bin = os.path.join(BASE, "jdk", "bin")
    # The listener checks client certificates against the worker's truststore, as in production.
    for alias, crt in (("mainca", "ca.crt"), ("interca", "inter.crt")):
        r = sh(os.path.join(jdk_bin, "keytool.exe"), "-importcert", "-noprompt", "-storetype", "PKCS12",
               "-keystore", os.path.join(BASE, "pki", "truststore.p12"), "-storepass", "abc=def",
               "-alias", alias, "-file", os.path.join(BASE, "pki", crt))
        assert r.returncode == 0, r.stdout + r.stderr
    r = sh(os.path.join(jdk_bin, "javac.exe"), "-cp", os.path.join(BASE, "kafka", "libs", "*"),
           "-d", os.path.join(BASE, "build"), os.path.join(HERE, "connect", "FlakySource.java"))
    assert r.returncode == 0, r.stdout + r.stderr
    r = sh(os.path.join(jdk_bin, "jar.exe"), "--create", "--file", PLUGIN_JAR, "-C", os.path.join(BASE, "build"), ".")
    assert r.returncode == 0, r.stdout + r.stderr

    cfg = os.path.join(BASE, "config")
    write(os.path.join(cfg, "worker.properties"),
          f"bootstrap.servers=127.0.0.1:{BROKER_PORT}\n"
          "offset.storage.file.filename=config\\\\connect.offsets\n"
          f"plugin.path={props_path(os.path.join(BASE, 'plugins'))}\n"
          "plugin.discovery=only_scan\n"
          "listeners=HTTPS://127.0.0.1:0\n"
          "key.converter=org.apache.kafka.connect.json.JsonConverter\n"
          "value.converter=org.apache.kafka.connect.json.JsonConverter\n"
          "ssl.client.auth=required\n"
          "ssl.keystore.type=PKCS12\n"
          "ssl.keystore.location=pki\\\\source_test_x14.p12\n"
          "ssl.keystore.password=abc=def\n"
          "ssl.key.password=abc=def\n"
          "ssl.truststore.type=PKCS12\n"
          "ssl.truststore.location=pki\\\\truststore.p12\n"
          "ssl.truststore.password=abc=def\n"
          "ssl.endpoint.identification.algorithm=\n")
    for name, port in (("prod_antura", ANTURA_DB), ("prod_lime", LIME_DB)):
        write(os.path.join(cfg, name + ".properties"),
              f"name={name}\nconnector.class=e2e.FlakySource\ntasks.max=1\n"
              f"flag.dir={props_path(FLAGS)}\ndatabase.hostname=127.0.0.1\ndatabase.port={port}\n")
    write(os.path.join(cfg, "connect.cfg"),
          "config\\worker.properties\nconfig\\prod_antura.properties\nconfig\\prod_lime.properties\n")

    r = sh("sc.exe", "create", SVC, "binPath=", os.path.join(BASE, "bin", "x14javaservice.exe"), "start=", "demand")
    assert r.returncode == 0, r.stdout + r.stderr
    # Shaped like the MSI's X14_CHILD_ARGS with the production heap settings in front.
    args = (r'-Xmx512m -XX:+ExitOnOutOfMemoryError -Xlog:gc:file=logs\gc.log:time,uptime:filecount=5,filesize=20m '
            rf'-Dkafka.logs.dir="{BASE}\logs" -cp "{BASE}\kafka\libs\*" '
            rf'"-Dlog4j.configuration=file:{BASE}\kafka\config\connect-log4j.properties" '
            'org.apache.kafka.connect.cli.ConnectStandalone')
    env = [rf"X14_CHILD_EXE={BASE}\jdk\bin\java.exe", f"X14_CHILD_ARGS={args}", f"X14_CHILD_CWD={BASE}",
           "X14_HEALTH_INTERVAL_SECONDS=10", "X14_HEALTH_GRACE_SECONDS=60"]
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, rf"SYSTEM\CurrentControlSet\Services\{SVC}", 0, winreg.KEY_SET_VALUE) as k:
        winreg.SetValueEx(k, "Environment", 0, winreg.REG_MULTI_SZ, env)


def start_broker():
    """A single-node KRaft broker on 127.0.0.1, run from <jdk-dir> and <kafka-dir>, not the install dir."""
    shutil.rmtree(BROKER, ignore_errors=True)
    os.makedirs(os.path.join(BROKER, "logs"))
    props = os.path.join(BROKER, "server.properties")
    write(props, "process.roles=broker,controller\nnode.id=1\n"
                 "controller.quorum.voters=1@127.0.0.1:19094\n"
                 f"listeners=PLAINTEXT://127.0.0.1:{BROKER_PORT},CONTROLLER://127.0.0.1:19094\n"
                 f"advertised.listeners=PLAINTEXT://127.0.0.1:{BROKER_PORT}\n"
                 "controller.listener.names=CONTROLLER\n"
                 "listener.security.protocol.map=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT\n"
                 f"log.dirs={BROKER.replace(os.sep, '/')}/data\n"
                 "offsets.topic.replication.factor=1\ntransaction.state.log.replication.factor=1\n"
                 "transaction.state.log.min.isr=1\ngroup.initial.rebalance.delay.ms=0\n")
    java = [os.path.join(JDK, "bin", "java.exe"), "-Xmx512m", "-cp", os.path.join(KAFKA, "libs", "*"),
            "-Dlog4j.configuration=file:" + os.path.join(KAFKA, "config", "log4j.properties"),
            "-Dkafka.logs.dir=" + os.path.join(BROKER, "logs")]
    cluster = base64.urlsafe_b64encode(uuid.uuid4().bytes).rstrip(b"=").decode()
    r = sh(*java, "kafka.tools.StorageTool", "format", "-t", cluster, "-c", props)
    assert r.returncode == 0, r.stdout + r.stderr
    out = open(os.path.join(BROKER, "broker.out"), "w")
    broker = subprocess.Popen(java + ["kafka.Kafka", props], stdout=out, stderr=subprocess.STDOUT,
                              creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    return broker if wait_for(lambda: reachable("127.0.0.1", BROKER_PORT, 2), 90, 2) else None


def go_offline():
    remove_offline()
    ps(f"New-NetFirewallRule -DisplayName '{FW_RULE}' -Direction Outbound -Action Block -Profile Any "
       f"-RemoteAddress {','.join(INTERNET)} | Out-Null")
    sh("auditpol.exe", "/set", f"/subcategory:{WFP_AUDIT}", "/failure:enable")
    sh("ipconfig.exe", "/flushdns")


def remove_offline():
    ps(f"Remove-NetFirewallRule -DisplayName '{FW_RULE}' -ErrorAction SilentlyContinue")
    sh("auditpol.exe", "/set", f"/subcategory:{WFP_AUDIT}", "/failure:disable")


def blocked_outbound(since):
    """(application, destination, port) of every outbound connection the firewall blocked since a time."""
    out = ps("Get-WinEvent -FilterHashtable @{LogName='Security'; Id=5157; "
             f"StartTime=[datetime]'{since:%Y-%m-%dT%H:%M:%S}'" + "} -ErrorAction SilentlyContinue | "
             "Where-Object { $_.Properties[2].Value -eq '%%14593' } | "
             "ForEach-Object { '' + $_.Properties[1].Value + '|' + $_.Properties[5].Value + '|' + $_.Properties[6].Value }")
    return [tuple(line.split("|")) for line in out.splitlines() if line.count("|") == 2]


def start_orphan():
    """A java.exe from the install directory that no Sorcerer owns, like the ones left on app10."""
    return subprocess.Popen([os.path.join(BASE, "jdk", "bin", "java.exe"), "-cp", PLUGIN_JAR, "e2e.Sleep"],
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP)


def scenarios(t0, broker):
    exe_hash = hashlib.sha256(open(EXE, "rb").read()).hexdigest().upper()
    print(f"x14javaservice.exe SHA-256 {exe_hash}", flush=True)

    # Offline like app10: nothing outside the local network answers, and the attempt is audited.
    check("internet blocked: 1.1.1.1:443 does not answer", not reachable("1.1.1.1", 443))
    check("internet blocked: ctldl.windowsupdate.com:80 does not answer", not reachable("ctldl.windowsupdate.com", 80))
    check("Kafka broker on 127.0.0.1, outside the install directory", broker and broker.poll() is None)

    # A java.exe left behind by an earlier Sorcerer is killed at start; others are left alone.
    orphan = start_orphan()
    time.sleep(2)
    t = datetime.datetime.now()
    sh("sc.exe", "start", SVC)
    e = wait_event(t, 2004, 60, f"PID {orphan.pid}")
    check("left-over java.exe in the install directory killed at start (2004)",
          e and wait_for(lambda: orphan.poll() is not None, 15), e[2] if e else f"orphan PID {orphan.pid}")
    check("java.exe outside the install directory left alone (the broker)", broker.poll() is None)

    check("child started (1005)", wait_event(t, 1005, 60))
    port = rest_port(t)
    check("REST address taken from Jetty's Started line (1006)", port, f"port {port}")
    first = wait_for(one_java, 30)
    check("one java.exe ConnectStandalone", first, f"PID {first.pid if first else None}")

    running = wait_for(lambda: all_running(port), 120, 2)
    check("both connectors RUNNING in Connect", running, str(running))
    code, _, der = rest(port)
    check("REST listener presents the worker's own certificate", code == 200 and der == worker_der())
    try:
        refused = rest(port, client_cert=False)[0] != 200
    except (OSError, ssl.SSLError, http.client.HTTPException):
        refused = True
    check("REST listener refuses a client without a certificate", refused)

    # Health checks through real Jetty from a machine without internet: no timeouts.
    time.sleep(max(0, 105 - (datetime.datetime.now() - t).total_seconds()))
    bad = [x for x in events(t) if x[0] in ("1004", "2002", "3002")]
    check("health checks answer offline: no 1004, 2002 or 3002", not bad, str(bad))

    # A task FAILED while its database answers: restarted, then running again.
    t = datetime.datetime.now()
    db_antura = FakeDb(ANTURA_DB)
    fail_once("prod_antura")
    check("prod_antura task FAILED in Connect", wait_for(lambda: task_state(port, "prod_antura") == "FAILED", 30, 0.2))
    e = wait_event(t, 2001, 60, "prod_antura")
    check("FAILED task restarted through the REST API (2001)", e, e[2] if e else "")
    check("prod_antura task RUNNING again in Connect", wait_for(lambda: task_state(port, "prod_antura") == "RUNNING", 30))
    check("running again reported (1001)", wait_event(t, 1001, 60, "prod_antura"))

    # A task FAILED while its database is down: left alone until the database answers.
    t = datetime.datetime.now()
    fail_once("prod_lime")
    check("prod_lime task FAILED in Connect", wait_for(lambda: task_state(port, "prod_lime") == "FAILED", 30, 0.5))
    e = wait_event(t, 1002, 60, "prod_lime")
    check("waits while the database does not answer (1002)", e and task_state(port, "prod_lime") == "FAILED", e[2] if e else "")
    db_lime = FakeDb(LIME_DB)
    e = wait_event(t, 2001, 60, "prod_lime")
    check("restarted once the database answers (2001)", e, e[2] if e else "")
    check("prod_lime task RUNNING again in Connect", wait_for(lambda: task_state(port, "prod_lime") == "RUNNING", 30))

    # java.exe exits by itself: started again.
    t = datetime.datetime.now()
    old = one_java()
    old.kill()
    e = wait_event(t, 3005, 30)
    check("child exit logged as an error with its exit code (3005)", e and "exited with code" in e[2], e[2] if e else "")
    check("child started again (1005)", wait_event(t, 1005, 60))
    port = rest_port(t)
    new = wait_for(lambda: one_java(other_than=old.pid), 30)
    check("a new java.exe with a new REST address", new and port, f"PID {old.pid} -> {new.pid if new else None}, port {port}")
    check("connectors RUNNING in the new JVM", wait_for(lambda: all_running(port), 120, 2))

    # The REST API stops answering (JVM suspended): the child is restarted after 5 checks in a row.
    t = datetime.datetime.now()
    hung = one_java()
    hung.suspend()
    e = wait_event(t, 2002, 300)
    check("silent REST API restarts the child (2002)", e, e[2] if e else "")
    check("REST did not answer was reported first (1004)", any(x[0] == "1004" for x in events(t)))
    replaced = wait_for(lambda: one_java(other_than=hung.pid), 60)
    check("suspended java.exe replaced by one new java.exe", replaced and not psutil.pid_exists(hung.pid),
          f"PID {hung.pid} -> {replaced.pid if replaced else None}")
    port = rest_port(t)
    check("connectors RUNNING after the restart", port and wait_for(lambda: all_running(port), 120, 2))

    # Sorcerer is killed: java.exe dies with it, Windows restarts Sorcerer, which starts one JVM.
    t = datetime.datetime.now()
    svc, before = service_process(), our_javas()
    sh("taskkill.exe", "/F", "/PID", str(svc.pid))
    gone = wait_for(lambda: not our_javas(), 15, 0.5)
    check("killing Sorcerer kills its java.exe (Job object)", gone, f"Sorcerer PID {svc.pid}, java {[p.pid for p in before]}")
    check("the broker's java.exe survives", broker.poll() is None)
    e = wait_event(t, 1005, 150)
    restarted = service_process()
    check("Windows restarts Sorcerer and it starts a child (1005)", e and restarted and restarted.pid != svc.pid)
    time.sleep(20)
    javas = our_javas()
    check("exactly one java.exe after the restart", len(javas) == 1, str([p.pid for p in javas]))
    port = rest_port(t)
    check("connectors RUNNING after Sorcerer's restart", port and wait_for(lambda: all_running(port), 120, 2))

    # A deliberate stop is not a crash.
    t = datetime.datetime.now()
    sh("sc.exe", "stop", SVC)
    check("service stopped", wait_for(lambda: "STOPPED" in sh("sc.exe", "query", SVC).stdout, 60))
    check("no java.exe in the install directory after the stop", wait_for(lambda: not our_javas(), 15))
    check("no child-exit error on a deliberate stop", not any(x[0] == "3005" for x in events(t)))

    crashes = [x for x in events(t0, ".NET Runtime", ids=[1026]) + events(t0, "Application Error", ids=[1000])
               if "x14javaservice" in x[2]]
    check("x14javaservice.exe never crashed (.NET Runtime 1026, Application Error 1000)", not crashes, str(crashes)[:500])

    blocked = blocked_outbound(t0)
    ours = sorted({b for b in blocked if "x14javaservice" in b[0].lower()})
    check("the firewall audit sees blocked connections (this test's own to 1.1.1.1)",
          any("python" in b[0].lower() and b[1] == "1.1.1.1" for b in blocked))
    check("Sorcerer tried no connection beyond the local network", not ours, str(ours[:10]))
    others = {}
    for app, dest, dport in blocked:
        others.setdefault(os.path.basename(app), set()).add(f"{dest}:{dport}")
    for app, dests in sorted(others.items()):
        print(f"      blocked outbound from {app}: {', '.join(sorted(dests)[:8])}{' ...' if len(dests) > 8 else ''}")
    db_antura.close()
    db_lime.close()


def main():
    t0 = datetime.datetime.now()
    broker = None
    try:
        install()
        go_offline()
        broker = start_broker()
        scenarios(t0, broker)
    finally:
        remove_service()
        if broker:
            broker.kill()
        remove_offline()
    return summary()


if __name__ == "__main__":
    sys.exit(main())
