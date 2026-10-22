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

The fleet pages sign in with Entra by default, and nothing secret is stored for
it. The app mints a token with `az account get-access-token --resource <audience>`
from the operator's own `az login`, keeps it in memory only, and sends it as
`Authorization: Bearer`. The audience is one of these:

- `OidcAudience` in the registry (any layer) or `REPORTMATE_OIDC_AUDIENCE`: an
  `api://` URI or an application ID.
- The audience the API advertises at `/api/v1/auth/config`. It is asked for only when
  the API address was set by an administrator (the runner's `HKLM\SOFTWARE\ReportMate`,
  policy, or `REPORTMATE_URL`), only over https, and the answer is used only when it
  is an `api://` URI. An address typed into Settings never discovers an audience.

When Entra is not available, because the API has no OIDC, az is not installed or
not signed in, or the API refuses the token, the app falls back to a read-scoped key
(`ReadApiKey`, `REPORTMATE_API_KEY`) and then the shared client passphrase.

The runner's own API key is an **ingest** credential: the API refuses it for
reads, so it is never sent. With no way to read, the app says which setting is
missing rather than sending a credential that is certain to be refused.

Settings are read from `HKLM\SOFTWARE\ReportMate` (the runner's own
configuration), then this application's own settings, then the environment, then
policy, so a policy still overrides everything and an edit made here still overrides
the runner.

## Dashboard

The dashboard follows the layout and cards shared with the web and Mac apps:

| Content width | Layout |
|---|---|
| under 860 | one column, insight cards two across at the bottom |
| 860 to 1,419 | status and activity 30/70, insight cards two across below |
| 1,420 to 1,939 | three columns 1/1/2, cards 1 to 4 beside status, cards 5 to 12 four across below |
| 1,940 and over | four columns 1/1/1/2, the cards dealt alternately into two insight columns |

The twelve insight cards, in order: System Health, Security Posture, Management,
Hardware, Install Health, Check-in Gaps, OS Currency, Software Risk, Certificates,
Identity, App Usage, and Network & Peripherals. Each read is shared, so one
`/security` read feeds Security Posture, Software Risk and Certificates. Reads run two
at a time with one retry, and refresh every five minutes. The managed-items read is
last and optional: if it fails, Install Health and Software Risk show their counts
without the item lists.

Every number, bar, legend row, chip and list item opens the report that owns it
with that filter applied, and each card header opens its report unfiltered. The
links are the web routes, so a click opens what the same link opens on the web or
the Mac. A device row opens the device list narrowed to that serial, because the
device page shows this machine only. A filtered report names its filters next to
its count, with a button to show the whole report.

These are the link parameters the reports read. Multiple values are comma-separated:

| Report | Parameters |
|---|---|
| `devices` | `status`, `search`, `platform` |
| `system` | `osVersion`, `uptime` (`lt1d`, `1-3d`, `3-7d`, `1-2w`, `2-4w`, `30d+`), `pending` (`up-to-date`, `1-5`, `6-10`, `10+`), `deferred=1`, `edition`, `os` (`unsupported`, `windows10`), `patch` (`behind`, `latest`) |
| `security` | `encryption=off`, `firewall=off`, `antivirus=off`, `sip=off`, `secureBoot=off`, `threats=1`, `certs=expired\|expiring`, `cve=critical\|exploited`, `certSearch=<text>`, `certStatus=expiring\|expired` |
| `management` | `provider`, `enrolled=0\|1`, `enrollmentType` |
| `hardware` | `arch` (`Apple Silicon`, `Intel`, `ARM64`, `x64`), `storage=low`, `memory=le8`, `type=laptop\|desktop`, `processor` |
| `installs` | `filter=errors\|warnings`, `item=<name>`, `stale=1` |
| `events/failures` | `hours`, `reason`, `outcome` |
| `identity` | `admins=extra`, `join=entra\|domain\|hybrid\|workgroup`, `usage=shared\|assigned` |
| `applications` | `apps`, `publishers`, `versions`; `applications/usage/<app>?days=30` for one app |
| `network` | `connection=wired\|wifi`, `signal=weak` (under 25%) |
| `peripherals` | `kind=printers\|displays`, `room=<room>` |

## Links

`reportmate://` links open a view directly, and are the web routes with the
scheme swapped:

```
reportmate://dashboard
reportmate://device/<serial>?tab=installs
reportmate://applications/usage/<app>?days=30
reportmate://management?provider=Unmanaged
```

A bare `reportmate://` link cannot fall back on a machine with no handler, so the
shareable form is the web handoff route, which tries the application and then
continues to the same page in a browser. The Mac application and the web app
share this contract; the link format is asserted by tests in all three so they
cannot drift apart.
