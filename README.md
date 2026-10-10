# ReportMate for Windows

The ReportMate dashboard as a native Windows application: the web app's fleet
views — Dashboard, Devices, Events and the nine module reports — plus the report
for the machine it is running on, built from that machine's local cache.

It is a reader. It renders what the ReportMate runner collects and what the API
serves, and changes nothing on the device.

## Layout

| Path | What it is |
|---|---|
| `app/` | The ReportMate app: startup, the window, the `reportmate://` handler, the icon |
| `src/` | The dashboard itself (`ReportMate.UI`): every page, view model and service |
| `tests/` | Tests for the logic that has no UI dependency |
| `client/` | The ReportMate Windows client, as a submodule, for the module data shapes |

The module models are owned by the runner and read here, so they come from the
client repository as a pinned submodule rather than a copy. A copy would drift,
and this application exists to render exactly what the runner writes.

## Embedding the dashboard

The dashboard lives in `src/ReportMate.UI.csproj`, a WPF class library, and the app
is a window around its root view. Another WPF app can host the same view to embed
the whole dashboard:

```csharp
var dashboard = new ReportMate.App.Views.Shared.DashboardView(scopedResources: true);
```

With `scopedResources: true` the dashboard keeps its palette and styles on the view
and the pages it shows, so the host's resources, including ones under the same keys,
are left alone. The host must load ModernWpf's `ThemeResources` and
`XamlControlsResources` in its own application resources; the dashboard follows the
theme ModernWpf reports.

Two hooks let the host supply what it already has. Both are optional:

- `ConfigManager.HostDefaults` fills connection settings, such as the API address,
  beneath the device's registry configuration, so the device, the app's settings and
  policy all still take precedence. Call `ConfigManager.Instance.ReloadSettings()`
  after setting it.
- `FleetApiClient.BearerTokenProvider` returns a bearer token for the fleet reads,
  for a host that has already signed its user in.

`DashboardView.OpenDeepLink` opens the view a `reportmate://` link names. The host
decides whether to register the protocol; the library never does.

A host whose window already has a search field, or that names the view itself,
passes `DashboardChrome.HostProvided`. The header then draws one row of the platform
toggle, the section tabs and Settings, with no search field and no ReportMate mark,
and the Devices list drops its own filter box, so the window has one search field:

```csharp
var dashboard = new ReportMate.App.Views.Shared.DashboardView(scopedResources: true, DashboardChrome.HostProvided);
```

The host feeds its field into the Devices list. Any text brings the list forward,
filtered by it; an empty string clears the filter:

```csharp
dashboard.DeviceSearch = query;
```

On Return, the host opens the device that best matches, which returns false when
nothing does:

```csharp
await dashboard.OpenBestDeviceMatchAsync();
```

`DashboardChrome(ShowsSearchField, ShowsBrand)` sets the two independently, and
`DashboardView.Chrome` changes them after the view is built. In every mode the
reports sit in the header as their own tabs when the width its other controls leave
can hold them, and fold into one Reports tab when it cannot.

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
