## Windows service for java

`x14javaservice.exe` runs as the Windows service **Sorcerer** and supervises a child process:
Kafka Connect (`java.exe ... ConnectStandalone`) for the X14 CDC agent, installed by the MSI in
`msi-agent`. The child is configured in the service's registry `Environment`:

| Variable | |
|---|---|
| `X14_CHILD_EXE` | The child, e.g. `jdk\bin\java.exe`. Without it the service idles. |
| `X14_CHILD_ARGS` | Its arguments. The files listed in `config\connect.cfg` are appended. |
| `X14_CHILD_CWD` | Its working directory. |
| `X14_HEALTH` | `off` turns the health check off. Default on. |
| `X14_HEALTH_INTERVAL_SECONDS` | Seconds between health checks. Default 60. |
| `X14_HEALTH_GRACE_SECONDS` | Seconds after each start of the child before the first check. Default 300. |

### Supervision

Kafka Connect never restarts a failed task, and a source database that is still down when
Debezium retries kills the task for good. Before, the service also stayed `Running` when
`java.exe` itself had exited. Sorcerer now:

- **starts the child again when it exits by itself**, after 10 s, 30 s, 90 s, 270 s, then every
  5 minutes, back to 10 s once it has run for 10 minutes. A stop of the service is not a crash;
- **checks Kafka Connect's REST API every minute**: `GET /connectors?expand=status`;
- **restarts a FAILED connector or task** with
  `POST /connectors/{name}/restart?includeTasks=true&onlyFailed=true`, but only while its
  database (`database.hostname`/`database.port` in the source file) answers on TCP, and at most
  every 10 minutes per connector. It never gives up: a source restart resumes from the saved
  offset;
- **restarts the child when the REST API has not answered in 5 checks in a row**, at most every
  15 minutes;
- **sets its own recovery actions** in the service control manager, so Windows restarts
  Sorcerer if its own process dies: after 1 min, 1 min, then 5 min (`sc qfailure Sorcerer`).

This replaces the separate PowerShell watchdog in `msi-agent/watchdog`, with the same rules and
event ids.

The REST API listens on the worker file's `listeners`. The MSI template uses
`HTTPS://127.0.0.1:0`, a random port, so Sorcerer takes the port from the line Jetty logs on
the child's stdout (`Started https_127.0.0.10@...{SSL, (ssl, http/1.1)}{127.0.0.1:52011}`).
A fixed port in `listeners` is used when there is no such line. The listener requires a client
certificate, because it takes the worker's `ssl.*` settings: Sorcerer authenticates with the
worker's own keystore (`ssl.keystore.location`, PKCS12) and accepts the server only when it
presents that same certificate. No new access is opened.

### Events

Application log, source `Sorcerer`. Error 3001 is the one to alarm on.

| Id | Level | |
|---|---|---|
| 1001 | Information | A connector is running again |
| 1002 | Information | A connector is FAILED and waits for its database (every 30 min) |
| 1004 | Information / Warning | The REST API did not answer |
| 1005 | Information | The child started |
| 1006 | Information | The REST address was found in the child's output |
| 2001 | Warning | A FAILED connector was restarted |
| 2002 | Warning | The child is restarted because the REST API does not answer |
| 2003 | Warning | The REST address is unknown, only the child process is supervised (daily) |
| 3001 | Error | A connector has been FAILED for an hour (repeated hourly) |
| 3002 | Error | The health check itself failed |
| 3004 | Error | A restart request failed |
| 3005 | Error | The child exited by itself and is started again |

The child's stdout is not written to the event log.

### Build and test

`ConnectAgent.Core` holds the logic without Windows APIs; `ConnectAgent.Tests` runs on Linux and
Windows, including a real HTTPS server with client authentication and a test PKI shaped like
production (P-384, leaves with only `clientAuth`):

```bash
dotnet test ConnectAgent.Tests
cd ConnectAgent.Service && dotnet publish -c Release -r win-x64
```

`tests/windows/e2e.py` installs the published exe as the service Sorcerer on a Windows test
machine, with `fake_child.py` standing in for Kafka Connect, and checks child restarts,
connector restarts, waiting for the database, a silent REST API, recovery actions and a clean
stop (20 checks). Run it as administrator with Python 3 and psutil, and a PKI from
`msi-agent/watchdog/tests/make-pki.sh` with the keystore password `abc=def`:

```powershell
python tests\windows\e2e.py <path>\x14javaservice.exe <pki-dir>
```
