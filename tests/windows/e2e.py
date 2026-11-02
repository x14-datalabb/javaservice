"""End-to-end test of x14javaservice.exe as the Windows service "Sorcerer".

Run as administrator on a Windows test machine (not app10/app11) with Python 3 and psutil:

    python e2e.py <x14javaservice.exe> <pki-dir from msi-agent/watchdog/tests/make-pki.sh>

Installs Sorcerer in C:\\sorcerer-test with fake_child.py as the child instead of java.exe,
runs the scenarios below against it and removes the service again. Health check every 10 s,
starting 10 s after each start of the child.
"""
import datetime
import json
import os
import shutil
import sys
import time

import psutil

from sorcerer_test import SVC, FakeDb, check, events, sh, summary, wait_event

EXE, PKI = sys.argv[1], sys.argv[2]
BASE = r"C:\sorcerer-test"
HERE = os.path.dirname(os.path.abspath(__file__))
ANTURA_DB, LIME_DB = 14390, 14391


def child():
    svc = [p for p in psutil.process_iter(["name", "cmdline"]) if p.info["name"] == "x14javaservice.exe"]
    if not svc:
        return None
    kids = [c for c in svc[0].children() if "fake_child.py" in " ".join(c.cmdline() or [])]
    return kids[0].pid if kids else None


def set_state(**connectors):
    state_path = os.path.join(BASE, "state.json")
    with open(state_path, encoding="utf-8") as f:
        state = json.load(f)
    for name, (connector, tasks) in connectors.items():
        state["connectors"][name] = {"connector": connector, "tasks": tasks}
    with open(state_path + ".tmp", "w", encoding="utf-8") as f:
        json.dump(state, f)
    os.replace(state_path + ".tmp", state_path)


def posts():
    try:
        with open(os.path.join(BASE, "posts.log"), encoding="utf-8") as f:
            return f.read().splitlines()
    except OSError:
        return []


def install():
    sh("sc.exe", "stop", SVC)
    time.sleep(3)
    sh("sc.exe", "delete", SVC)
    shutil.rmtree(BASE, ignore_errors=True)
    for d in ("bin", "config", "pki"):
        os.makedirs(os.path.join(BASE, d))
    shutil.copy(EXE, os.path.join(BASE, "bin", "x14javaservice.exe"))
    shutil.copy(os.path.join(HERE, "fake_child.py"), BASE)
    for f in os.listdir(PKI):
        shutil.copy(os.path.join(PKI, f), os.path.join(BASE, "pki"))
    cfg = os.path.join(BASE, "config")
    with open(os.path.join(cfg, "worker.properties"), "w") as f:
        f.write("bootstrap.servers=localhost:9093\nlisteners=HTTPS://127.0.0.1:0\n"
                "ssl.keystore.location=pki\\\\source_test_x14.p12\nssl.keystore.password=abc=def\n")
    with open(os.path.join(cfg, "antura.properties"), "w") as f:
        f.write(f"name=prod_antura\ndatabase.hostname=127.0.0.1\ndatabase.port={ANTURA_DB}\n")
    with open(os.path.join(cfg, "lime.properties"), "w") as f:
        f.write(f"name=prod_lime\ndatabase.hostname=127.0.0.1\ndatabase.port={LIME_DB}\n")
    with open(os.path.join(cfg, "connect.cfg"), "w") as f:
        f.write("config\\worker.properties\nconfig\\antura.properties\nconfig\\lime.properties\n")
    with open(os.path.join(BASE, "state.json"), "w") as f:
        json.dump({"connectors": {"prod_antura": {"connector": "RUNNING", "tasks": ["RUNNING"]},
                                  "prod_lime": {"connector": "RUNNING", "tasks": ["RUNNING"]}}}, f)
    r = sh("sc.exe", "create", SVC, "binPath=", os.path.join(BASE, "bin", "x14javaservice.exe"), "start=", "demand")
    assert r.returncode == 0, r.stdout + r.stderr
    env = [f"X14_CHILD_EXE={sys.executable}", f"X14_CHILD_ARGS=-u {BASE}\\fake_child.py", f"X14_CHILD_CWD={BASE}",
           "X14_HEALTH_INTERVAL_SECONDS=10", "X14_HEALTH_GRACE_SECONDS=10"]
    r = sh("reg.exe", "add", rf"HKLM\SYSTEM\CurrentControlSet\Services\{SVC}", "/v", "Environment", "/t", "REG_MULTI_SZ",
           "/d", "\\0".join(env), "/f")
    assert r.returncode == 0, r.stdout + r.stderr


def main():
    install()
    antura_db = FakeDb(ANTURA_DB)

    t = datetime.datetime.now()
    sh("sc.exe", "start", SVC)
    check("child started", wait_event(t, 1005, 30))
    found = wait_event(t, 1006, 30)
    check("REST address taken from the Jetty line", found, found[2] if found else "")
    first_child = child()

    q = sh("sc.exe", "qfailure", SVC).stdout
    check("recovery actions: restart after 1 min", "RESTART -- Delay = 60000" in q)
    check("recovery actions: then 5 min", "RESTART -- Delay = 300000" in q)
    check("recovery also on error exit (failureflag)", "TRUE" in sh("sc.exe", "qfailureflag", SVC).stdout)

    time.sleep(25)
    ev = events(t)
    check("all RUNNING: no restart, REST answers", not posts() and not any(e[0] in ("1004", "2001", "3002") for e in ev),
          str([e for e in ev if e[0] in ("1004", "2001", "3002")]))

    # A task FAILED while its database answers: restarted, then running again.
    t = datetime.datetime.now()
    set_state(prod_antura=("RUNNING", ["FAILED"]))
    e = wait_event(t, 2001, 40, "prod_antura")
    check("FAILED task restarted through the REST API", e and "/connectors/prod_antura/restart?includeTasks=true&onlyFailed=true" in posts(),
          e[2] if e else str(posts()))
    check("running again reported", wait_event(t, 1001, 40, "prod_antura"))

    # A connector FAILED while its database is down: left alone until it answers.
    t = datetime.datetime.now()
    set_state(prod_lime=("FAILED", ["FAILED"]))
    e = wait_event(t, 1002, 40, "prod_lime")
    check("waits while the database does not answer", e and not any("prod_lime" in p for p in posts()), e[2] if e else "")
    lime_db = FakeDb(LIME_DB)
    e = wait_event(t, 2001, 40, "prod_lime")
    check("restarted once the database answers", e, e[2] if e else "")
    lime_db.close()

    # The child exits by itself (ConnectStandalone after a config error): started again.
    t = datetime.datetime.now()
    open(os.path.join(BASE, "exit.flag"), "w").close()
    e = wait_event(t, 3005, 30)
    check("child exit logged as an error with its exit code", e and "code 3" in e[2], e[2] if e else "")
    check("child started again", wait_event(t, 1005, 40))
    found = wait_event(t, 1006, 40)
    second_child = child()
    check("new REST address found", found and second_child and second_child != first_child, found[2] if found else "")

    # The REST API stops answering: the child is restarted after 5 checks in a row.
    t = datetime.datetime.now()
    open(os.path.join(BASE, "hang.flag"), "w").close()
    e = wait_event(t, 2002, 240)
    os.remove(os.path.join(BASE, "hang.flag"))
    check("silent REST API restarts the child", e, e[2] if e else "")
    check("REST did not answer was reported first", any(x[0] == "1004" for x in events(t)))
    time.sleep(5)
    third_child = child()
    check("child replaced", third_child and third_child != second_child, f"{second_child} -> {third_child}")
    check("REST answers again", wait_event(t, 1006, 60))

    # A deliberate stop is not a crash.
    t = datetime.datetime.now()
    sh("sc.exe", "stop", SVC)
    time.sleep(15)
    check("service stopped", "STOPPED" in sh("sc.exe", "query", SVC).stdout)
    check("child gone after stop", child() is None and not any("fake_child.py" in " ".join(p.cmdline() or [])
                                                             for p in psutil.process_iter(["name"]) if p.info["name"] == "python.exe"))
    check("no child-exit error on a deliberate stop", not any(e[0] == "3005" for e in events(t)))

    antura_db.close()
    sh("sc.exe", "delete", SVC)
    return summary()


if __name__ == "__main__":
    sys.exit(main())
