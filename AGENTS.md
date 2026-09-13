# AGENTS.md

## Overview
- .NET 10 / WinUI 3 (Windows App SDK 2.4) desktop toolbox for Power BI Desktop authors. Opens on a Home launcher; tools plug in via `IToolPage` + `ToolRegistry`.
- Solution is `SingularPowerTools.slnx` (XML solution format, not `.sln`).
- `src/SingularTools.Core` — `net10.0`, cross-platform, no WinUI. PBIR/PBIP parsing, page model, edit history, Power BI window detection.
- `src/SingularTools.App` — `net10.0-windows10.0.26100.0`, unpackaged WinUI 3 exe. **Root namespace is `SingularTools_App` (underscore), not the folder name.**
- `tests/SingularTools.Tests` — xUnit, references Core only. There is no CI; verification is local.

## Commands
- Build app: `dotnet build src/SingularTools.App/SingularTools.App.csproj`
- Run app (unpackaged, launches GUI): `dotnet run --project src/SingularTools.App/SingularTools.App.csproj`
- Test all: `dotnet test SingularPowerTools.slnx`
- Single test: `dotnet test tests/SingularTools.Tests/SingularTools.Tests.csproj --filter FullyQualifiedName~NaturalSort`
- No lint/format/codegen config; the compiler is the only check.

## Test fixture gotcha (read before touching tests or the demo report)
- `dotnet test` is currently **red: 8/13 fail**. `Demo PBI Report.Report/definition/pages/pages.json` has 2 pages, but `ReportManagerTests` asserts 3 (`P1`, `P2`, `Page 3`) and a `clusteredBarChart` on `P1`. Tests are coupled to the checked-in demo report; update both together.
- Mutation tests copy the demo report to a temp dir first, but read-only tests read the repo fixture directly, so editing `Demo PBI Report.Report` breaks them.

## Architecture
- Add a tool: implement `src/SingularTools.App/Shell/IToolPage.cs` and append a descriptor in `Shell/ToolRegistry.cs`; `MainWindow` builds navigation automatically. One folder per tool under `Tools/`.
- `ReportManager` is the whole PBIR layer. A folder is a valid report when `definition/pages/pages.json` exists. Page IDs are 20-char lowercase hex; `DuplicatePage` generates random 10-byte IDs and deep-copies the page folder.
- Writes are atomic: `SaveChanges` writes `pages.json.tmp` then `File.Move(..., overwrite: true)`. Call `SaveChanges()` after mutating `Pages`, and `Reload()` to re-read from disk.
- `ReportEditHistory` is snapshot-based (full copies under `%LOCALAPPDATA%\SingularPowerTools\history\<guid>`), not command-based: `Reset` baseline, `Commit` after each edit, then feed the dir from `Undo()`/`Redo()` to `RestoreFromSnapshot`.
- `PowerBiDetector` identifies Power BI by process name `PBIDesktop` (window title alone is unreliable). `PowerBiNavigator` drives the open report's page tabs via UI Automation (App csproj adds `Microsoft.WindowsDesktop.App` framework reference).
- `ReportManager.DiscoverReportFolder`/`FindReportByName` hardcode `e:\Test_Projects\Singular Tools for Power BI` fallbacks — a dev-machine quirk, not portable.
- App logs to `%LOCALAPPDATA%\SingularPowerTools\app.log` via `App.Log`; check it for launch/XAML failures since failures in a GUI are otherwise silent.
- App is unpackaged WinUI (`WindowsPackageType=None`, self-contained Windows App SDK, custom `Program.Main` with `DISABLE_XAML_GENERATED_MAIN`). Do not remove the `CopyXamlResourcesToPublish` / `CopyAssetsToPublish` targets — `dotnet publish` omits `.xbf`/`.pri`/Assets for unpackaged WinUI, and an installed copy then silently runs stale UI without icons.

## Distribution
- `distribution/register-external-tool.ps1` publishes Release to `%LOCALAPPDATA%\SingularPowerTools\`, regenerates `distribution/SingularPowerTools.pbitool.json` (absolute exe path + base64 icon), then self-elevates to copy it into Power BI's `External Tools` Common Files directory.
- `distribution/register-as-admin.cmd` only copies the already-present JSON (no publish); requires admin.
