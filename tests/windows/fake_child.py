"""Stands in for Kafka Connect (java.exe ConnectStandalone) under Sorcerer in the Windows test.

Started by the service with the connect.cfg paths as extra arguments, which it ignores. Like
Connect it serves the REST API over HTTPS on a random port with the worker's certificate,
requires a client certificate from the same CA and logs Jetty's "Started https_..." line with
the bound port on stdout.

Controlled through files next to it:
  state.json  connectors and task states returned by GET /connectors?expand=status
  posts.log   every POST .../restart is appended here, and sets the connector RUNNING
  exit.flag   exit with code 3, like ConnectStandalone after a config error
  hang.flag   accept connections but never answer
"""
import datetime
import json
import os
import ssl
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BASE = os.path.dirname(os.path.abspath(__file__))
STATE = os.path.join(BASE, "state.json")
POSTS = os.path.join(BASE, "posts.log")
PKI = os.path.join(BASE, "pki")
lock = threading.Lock()


def load():
    with lock, open(STATE, encoding="utf-8-sig") as f:
        return json.load(f)


def save(state):
    with lock, open(STATE + ".tmp", "w", encoding="utf-8") as f:
        json.dump(state, f)
    os.replace(STATE + ".tmp", STATE)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if os.path.exists(os.path.join(BASE, "hang.flag")):
            time.sleep(3600)
        if self.path != "/connectors?expand=status":
            return self.send(404, {"message": "not found"})
        out = {}
        for name, c in load()["connectors"].items():
            connector = {"state": c["connector"], "worker_id": "127.0.0.1:0"}
            if c["connector"] == "FAILED":
                connector["trace"] = "x\nCaused by: java.net.SocketTimeoutException: Connect timed out"
            tasks = []
            for i, state in enumerate(c["tasks"]):
                task = {"id": i, "state": state, "worker_id": "127.0.0.1:0"}
                if state == "FAILED":
                    task["trace"] = "x\nCaused by: java.net.SocketTimeoutException: Connect timed out"
                tasks.append(task)
            out[name] = {"status": {"name": name, "connector": connector, "tasks": tasks, "type": "source"}}
        self.send(200, out)

    def do_POST(self):
        with open(POSTS, "a", encoding="utf-8") as f:
            f.write(self.path + "\n")
        name = urllib.parse.unquote(self.path.split("/")[2].split("?")[0])
        state = load()
        if name not in state["connectors"]:
            return self.send(404, {"message": "unknown connector"})
        c = state["connectors"][name]
        c["connector"] = "RUNNING"
        c["tasks"] = ["RUNNING"] * len(c["tasks"])
        save(state)
        self.send(202, {"name": name})


context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(os.path.join(PKI, "source_test_x14.crt"), os.path.join(PKI, "source_test_x14.key"))
context.load_verify_locations(os.path.join(PKI, "chain.pem"))
context.verify_mode = ssl.CERT_REQUIRED
server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
server.socket = context.wrap_socket(server.socket, server_side=True)
port = server.server_address[1]
now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S,%f")[:-3]
print(f"[{now}] INFO Kafka Connect worker initialization took 1234ms (org.apache.kafka.connect.cli.AbstractConnectCli:151)", flush=True)
print(f"[{now}] INFO Started https_127.0.0.10@1a2b3c4d{{SSL, (ssl, http/1.1)}}{{127.0.0.1:{port}}} "
      f"(org.eclipse.jetty.server.AbstractConnector:333)", flush=True)
threading.Thread(target=server.serve_forever, daemon=True).start()

while True:
    flag = os.path.join(BASE, "exit.flag")
    if os.path.exists(flag):
        os.remove(flag)
        print("[x] ERROR Stopping after connector error (org.apache.kafka.connect.cli.ConnectStandalone:142)", flush=True)
        os._exit(3)
    time.sleep(0.5)
