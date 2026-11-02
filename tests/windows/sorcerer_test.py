"""Helpers shared by the Windows end-to-end tests of Sorcerer (e2e.py, e2e_connect.py)."""
import socket
import subprocess
import threading
import time

SVC = "Sorcerer"
results = []


def sh(*args):
    return subprocess.run(list(args), capture_output=True, text=True)


def ps(script):
    return subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True).stdout


def check(name, ok, detail=""):
    results.append((name, bool(ok)))
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}", flush=True)


def summary():
    failed = [n for n, ok in results if not ok]
    print(f"\n{len(results) - len(failed)} of {len(results)} checks passed", flush=True)
    if failed:
        print("failed: " + "; ".join(failed), flush=True)
    return 1 if failed else 0


def events(since, provider="Sorcerer", log="Application", ids=None):
    """(id, level, message) of the events since a local time, oldest first."""
    id_filter = f"; Id={','.join(str(i) for i in ids)}" if ids else ""
    out = ps(f"Get-WinEvent -FilterHashtable @{{LogName='{log}'; ProviderName='{provider}'{id_filter}; "
             f"StartTime=[datetime]'{since:%Y-%m-%dT%H:%M:%S}'}} -ErrorAction SilentlyContinue | "
             "Sort-Object TimeCreated | ForEach-Object { '' + $_.Id + '|' + $_.LevelDisplayName + '|' + "
             "($_.Message -replace '\\r?\\n', ' ') }")
    return [tuple(line.split("|", 2)) for line in out.splitlines() if line.count("|") >= 2]


def wait_event(since, event_id, timeout, contains=""):
    end = time.time() + timeout
    while time.time() < end:
        for e in events(since):
            if e[0] == str(event_id) and contains in e[2]:
                return e
        time.sleep(2)
    return None


def wait_for(predicate, timeout, interval=1.0):
    """The first truthy value of predicate() within the timeout, else None."""
    end = time.time() + timeout
    while time.time() < end:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    return None


class FakeDb:
    """Something that accepts TCP on the database port, like SQL Server."""

    def __init__(self, port):
        self.sock = socket.socket()
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("127.0.0.1", port))
        self.sock.listen(16)
        threading.Thread(target=self.loop, daemon=True).start()

    def loop(self):
        while True:
            try:
                c, _ = self.sock.accept()
                c.close()
            except OSError:
                return

    def close(self):
        self.sock.close()
