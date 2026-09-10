# ReportMate for Windows

The ReportMate dashboard as a native Windows application: the web app's fleet
views — Dashboard, Devices, Events and the nine module reports — plus the report
for the machine it is running on, built from that machine's local cache.

It is a reader. It renders what the ReportMate runner collects and what the API
serves, and changes nothing on the device.

## Layout

| Path | What it is |
|---|---|
| `src/` | The WPF application |
| `tests/` | Tests for the logic that has no UI dependency |
| `client/` | The ReportMate Windows client, as a submodule, for the module data shapes |

The module models are owned by the runner and read here, so they come from the
client repository as a pinned submodule rather than a copy. A copy would drift,
and this application exists to render exactly what the runner writes.

## Building

```powershell
git submodule update --init --depth 1
.\build.ps1
```

The result is a single self-contained executable. WPF cannot be trimmed, so it is
large; self-contained means it runs on an endpoint with no .NET installed.

Public builds are unsigned by design. Signing happens in the private deployment
pipeline, which fetches a release from here and repacks it.

## Fleet data and credentials

The per-device page reads `C:\ProgramData\ManagedReports\cache` and needs no
network. Every fleet page needs the API.

The runner's own API key is an **ingest** credential: the API refuses it for
reads. The application therefore uses a read-scoped key when one is configured
and the shared client passphrase otherwise, and says a read credential is needed
when it has neither, rather than sending one that is certain to be refused.

Settings are read from `HKLM\SOFTWARE\ReportMate` (the runner's own
configuration), then this application's own settings, then policy — so a policy
still overrides everything and an edit made here still overrides the runner.

## Links

`reportmate://` links open a view directly, and are the web routes with the
scheme swapped:

```
reportmate://dashboard
reportmate://device/<serial>?tab=installs
reportmate://applications/usage/<app>?days=30
```

A bare `reportmate://` link cannot fall back on a machine with no handler, so the
shareable form is the web handoff route, which tries the application and then
continues to the same page in a browser. The Mac application and the web app
share this contract; the link format is asserted by tests in all three so they
cannot drift apart.
