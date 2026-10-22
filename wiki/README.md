# ReportMate for Windows wiki

ReportMate for Windows is the native WPF dashboard. It shows the fleet views the web app serves (Dashboard, Devices, Events and the module reports) and the report for the machine it runs on, read from that machine's local cache. It is a reader: it renders what the Windows runner collects and what the API serves, and changes nothing on the device.

## Read first

- [Project layout, embedding, building and links](../README.md)
- [Continuous integration](../.github/workflows/ci.yml)
- [Release workflow](../.github/workflows/release.yml)
- [Build and packaging script](../build.ps1)

## Architecture

The solution has two projects. [`src/ReportMate.UI.csproj`](../src/ReportMate.UI.csproj) is a WPF class library holding the dashboard itself: every page, view model and service. [`app/ReportMate.App.csproj`](../app/ReportMate.App.csproj) is the application, a window around the library's `DashboardView` plus what only an application owns: startup, the `reportmate://` handler, the manifest and the icon. Another WPF application can host `DashboardView` to embed the whole dashboard, as the README describes.

The module data models are owned by the runner, so they come from the [Windows client](https://github.com/reportmate/reportmate-client-win) as a pinned submodule in `client/` and are compiled in from `client/src/Models/Modules`. The dashboard deserialises the runner's JSON with the same classes that wrote it, so the two cannot drift.

The per-device page reads the runner's cache under `C:\ProgramData\ManagedReports\cache` through [`DeviceSnapshotStore`](../src/Services/DeviceSnapshotStore.cs), newest run first, and needs no network. The fleet pages read the API's `/api/v1` endpoints through [`FleetApiClient`](../src/Services/FleetApiClient.cs) with a read-scoped credential, never the runner's ingest key.

Settings come from [`ConfigManager`](../src/Services/ConfigManager.cs): the runner's registry configuration, then the application's own settings, then policy, so policy overrides everything. Pages live under [`src/Views/Fleet`](../src/Views/Fleet) and [`src/Views/Device`](../src/Views/Device), with shared views, the settings page and the root `DashboardView` under [`src/Views/Shared`](../src/Views/Shared). Light and dark palettes are in [`src/Themes`](../src/Themes).

[`DeepLink`](../src/Services/DeepLink.cs) parses `reportmate://` links, which mirror the web routes, and [`ProtocolHandler`](../src/Services/ProtocolHandler.cs) registers the scheme per user and hands a link to a running copy of the application.

## Development and verification

Use the current [README](../README.md) for setup. Initialise the client submodule, then run the build script on Windows:

```powershell
git submodule update --init --depth 1
.\build.ps1
```

[`build.ps1`](../build.ps1) publishes a self-contained single-file executable for x64 and ARM64, bundles the `reportmateutil` CLI from its own GitHub release, and packages an MSI per architecture with `cimipkg`. It then reads the expected payload list from [`postinstall.ps1`](../build/pkg/scripts/postinstall.ps1) back out of each finished MSI and fails if a file is missing. `-SkipMsi` builds the executables without packaging them.

The tests in [`tests/ReportMate.App.Tests`](../tests/ReportMate.App.Tests) use xUnit and cover the logic with no UI dependency, such as deep links, install status, failure reasons, device matching and the cache reader. Run them with:

```powershell
dotnet test tests\ReportMate.App.Tests\ReportMate.App.Tests.csproj
```

[CI](../.github/workflows/ci.yml) runs the tests and builds the unsigned MSIs on every push to `main` and every pull request. The [release workflow](../.github/workflows/release.yml) does the same on a version tag and publishes the result. Public builds are unsigned by design; signing happens downstream in a private deployment pipeline that repacks a release.

## Documentation ownership

Keep this wiki as the entry point and update the linked pages where the facts belong: the README for embedding, building and the link contract, and the client repository for the module data shapes. Examples use fictional data. Deployment inventories, credentials, signing material, provider mappings and organization-specific operations belong with their deployment owner.
