# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build from solution root
dotnet build ArchiveAutomator.sln

# Run the WPF app (must be on Windows)
dotnet run --project ArchiveAutomator/ArchiveAutomator.csproj

# Build release
dotnet build ArchiveAutomator.sln -c Release
```

No tests yet. Targets `net8.0-windows` (`UseWPF=true`, `UseWindowsForms=true`). Windows required.

## Project State

**All phases fully implemented. Builds clean (0 errors, 0 warnings).**

### NuGet Packages (installed)
- `Box.Sdk.Gen` 1.12.0 — Box API (**NOT** `Box.V2` — that is .NET Framework only)
- `ClosedXML` 0.105.0 — XLSX parsing
- `CsvHelper` 33.1.0 — CSV parsing
- `Microsoft.Identity.Client` 4.82.1
- `Newtonsoft.Json` 13.0.4 — session manifest + settings serialization

## Folder Structure

```
ArchiveAutomator/
  Converters/   Converters.cs (4 converters)
  Interfaces/   IStorageProvider.cs
  Models/       AppSettings.cs, JobItem.cs, SessionManifest.cs
  Providers/    BoxApiProvider.cs, LocalFileSystemProvider.cs
  Services/     ExcelParserService.cs, LoggingService.cs,
                OrchestratorService.cs, SessionService.cs, SettingsService.cs
  ViewModels/   MainViewModel.cs, RelayCommand.cs, ViewModelBase.cs
  Views/        ExecutionView, MappingView, PathsView, PreFlightView (.xaml + .xaml.cs)
                HelpWindow.xaml + HelpWindow.xaml.cs   ← tabbed user guide
README.md       Comprehensive user documentation
```

Runtime data (all in user profile — never in the application folder, no elevation needed):
```
%LocalAppData%\ArchiveAutomator\Logs\       per-run log_[timestamp].csv files
%LocalAppData%\ArchiveAutomator\Sessions\   session_[RunId].json manifests
%AppData%\ArchiveAutomator\settings.json    auto-saved settings
%AppData%\ArchiveAutomator\profiles\        named profile JSON files
```

## Architecture

### Key interfaces & models

**`IStorageProvider`** — `CanMoveAsync`, `MoveAsync`, `CopyAsync`, `DeleteAsync`.

**`JobItem`** — `JobNumber`, `FolderName`, `Status`, `State` (`Pending`/`InProgress`/`Success`/`Failed`), `ErrorDetails`. Implements `INotifyPropertyChanged` so DataGrid cells update live without manual collection refresh.

**`SessionManifest`** — `RunId` (GUID), `StartedAt` (ISO8601), `Mode`, `Operation`, `List<JobItem>`. Written to `%LocalAppData%\ArchiveAutomator\Sessions\session_[RunId].json`; updated after every individual operation.

**`PersistedSettings`** (in `SettingsService.cs`) — `CsvFilePath`, `SourceFolderPath`, `ArchiveFolderPath`, `StorageMode`, `OperationMode`, `SelectedJobNumberColumn`, `SelectedStatusColumn`, `TriggerValue`, `BoxClientId`. Saved to `%AppData%\ArchiveAutomator\settings.json`. **`BoxClientSecret` is intentionally NOT persisted.**

### Critical design rules

**Local lock check** — Before every Move/Copy, check `Directory.Exists(source)` first (throws `DirectoryNotFoundException` with "not found" message if missing). Then attempt `Directory.Move` to `_archivetest_` suffix. `IOException` → mark job `Failed` with "locked or in use" message.

**Cross-drive detection** — Compare `Path.GetPathRoot(source)` vs `Path.GetPathRoot(destination)`. If different: recursive copy → `VerifyDestination` (file count check) → `Directory.Delete(source, recursive: true)`.

**Box provider** — Always operate on **Folder IDs**, never search by folder name in the operation loop. Move = `UpdateFolderByIdAsync` with new `parent.id`. Retry with exponential backoff for Box's rate limit (HTTP 429/5xx). Request types are in `Box.Sdk.Gen.Managers` namespace (not `Box.Sdk.Gen.Schemas`).

**OrchestratorService** — Per-job try-catch. Updates session manifest JSON **after every individual operation** (not batched). Appends to the run log immediately. Reports two progress messages per job: one when starting (verb+paths) and one on completion (✓/✗).

**Resume logic** — On startup, scan for newest incomplete `session_[RunId].json`. If found, prompt user; reload and filter to `Pending`/`InProgress` items only.

**Threading** — All I/O is `async/await`. UI updates via `IProgress<OrchestratorProgress>`. Pause/Cancel use `CancellationTokenSource`.

### Logging schema (`log_[timestamp].csv`)
```
Timestamp,JobID,Mode,Operation,Source,Destination,Result,ErrorDetails
```
One file per run; named `log_yyyyMMdd_HHmmss.csv` in `%LocalAppData%\ArchiveAutomator\Logs\`.

### Settings persistence
`SettingsService` has two layers:
1. **Auto-save** (`settings.json`) — saves to `%AppData%\ArchiveAutomator\settings.json` on every property change. Loaded on startup **only when no resumable session was declined** (see Resume logic below).
2. **Named profiles** (`profiles\*.json`) — explicit Save/Load via the Settings Profile section. Stored in `%AppData%\ArchiveAutomator\profiles\{name}.json`. `ListProfiles()` scans the folder; `SanitizeName()` strips illegal filename chars.

### Resume dialog behaviour
- **No session found** → auto-load last settings normally.
- **Session found, user clicks Yes** → auto-load settings (paths/columns), then overlay Mode/Operation/Jobs from the manifest on top.
- **Session found, user clicks No** → start completely blank — settings are NOT loaded. This prevents the old job paths from silently reappearing.

`PromptResumableSession()` returns `SessionCheckResult` (`None` / `Resumed` / `Declined`). The constructor calls `LoadSettings()` only for `None` or `Resumed`, then calls `ApplySession()` only for `Resumed`.

### UI layout (MainWindow.xaml)
Two-panel layout with a vertical `GridSplitter` between left and right.

**Outer `DockPanel`**: Menu (Top) → StatusBar (Bottom) → Action bar (Bottom) → main Grid fills rest.

**Menu bar** — two top-level items:
- `_File`: New Run · Open Logs Folder · Open Last Log · **Open Sessions Folder**
- `_Help`: **User Guide…** (opens HelpWindow non-blocking) · **About Archive Automator** (MessageBox)

**Left panel** (`ScrollViewer` → `StackPanel` of `Expander` sections, initial width 420 px, draggable):
- Mode — Storage radio buttons + Operation radio buttons + Box credentials (conditional)
- Settings Profile — profile name ComboBox + Save/Load buttons
- Job List File — path TextBox + Browse button
- Column Mapping (`MappingView`, vertical 3-row layout)
- Folder Paths (`PathsView`; Archive row hidden when Delete mode)

**Vertical `GridSplitter`** (6 px) between columns — drag to resize left/right panels.

**Right panel** (`Grid`, fills `*`) — two sub-rows separated by a horizontal `GridSplitter`:
- Row 0 (200 px min): Matched Jobs (Pre-flight) — `PreFlightView` inside styled `Border`
- Row 1 (6 px): Horizontal `GridSplitter` — drag to resize jobs vs. log area
- Row 2 (`*`): Execution Log — `ExecutionView` inside styled `Border`

**Collapsible sections** use the `SectionExpander` style defined in `App.xaml`:
- Custom `ControlTemplate` for `Expander`: gradient header, rotating ▶/▼ chevron, hover/press highlights, card border (`CornerRadius="4"`), `IsExpanded` defaults to `True`.
- Right-panel headers use matching `Border` + `LinearGradientBrush` (non-collapsible, always visible).

**`MappingView.xaml`** — vertical 3-row `Grid` (Job Number col, Status col, Trigger value + Load Jobs) so it fits the narrow left panel.

**`HelpWindow.xaml`** — `ShowInTaskbar="False"`, `WindowStartupLocation="CenterScreen"`, 860×680. Contains a `TabControl` with six tabs: Overview, Quick Start, Job List File, Box.com Setup, Settings & Profiles, Troubleshooting. Local `Window.Resources` define styles: H1, H2, Body, Bullet, Note, CodeBlock (Border), Code (TextBlock), Rule (Separator), StepBadge (Border). Bottom bar has author credit left and a `CloseButton_Click` button (`IsDefault="True"`) right. Code-behind is a trivial `Window` subclass with `InitializeComponent()` + `CloseButton_Click` → `Close()`.

**`CanStart` validation**:
- Move or Copy: `SourceFolderPath` AND `ArchiveFolderPath` must be non-empty.
- Delete: only `SourceFolderPath` required.
- `OnPropertyChanged(nameof(CanStart))` fired from `SourceFolderPath`, `ArchiveFolderPath`, and `OperationMode` setters.
- `OnPropertyChanged(nameof(IsDeleteMode))` fired from `OperationMode` setter, `ApplySession`, and `ClearAll`.

### Commands in MainViewModel
| Command | Enabled when | Description |
|---|---|---|
| `BrowseCsvCommand` | always | Opens file dialog for job list |
| `BrowseSourceCommand` | always | Opens folder browser for source path |
| `BrowseArchiveCommand` | always | Opens folder browser for archive path |
| `ParseCommand` | CsvFilePath non-empty | Reads spreadsheet, populates Jobs |
| `StartCommand` | CanStart | Starts the orchestration run async |
| `PauseCommand` | IsRunning | Cancels CTS after current job |
| `CancelCommand` | IsRunning | Cancels CTS immediately |
| `AuthenticateBoxCommand` | CanAuthenticate | Runs Box OAuth browser flow |
| `OpenLogsFolderCommand` | _logsDirectory exists | Opens Logs folder in Explorer |
| `OpenLastLogCommand` | _lastLogPath exists | Opens last log CSV |
| `OpenSessionsFolderCommand` | _sessionsDirectory exists | Opens Sessions folder in Explorer |
| `ClearAllCommand` | !IsRunning | Resets all fields + auto-save |
| `SaveProfileCommand` | ProfileName non-empty | Saves named profile |
| `LoadProfileCommand` | ProfileName non-empty | Loads named profile |
| `ShowHelpCommand` | always | Opens HelpWindow (non-blocking `.Show()`) |
| `ShowAboutCommand` | always | Shows About MessageBox |

### Known namespace conflicts (UseWindowsForms=true)
| Ambiguous type | Fix |
|---|---|
| `Task` | `using Task = System.Threading.Tasks.Task;` in BoxApiProvider.cs |
| `Binding.DoNothing` | Use `System.Windows.Data.Binding.DoNothing` fully qualified in Converters.cs |
| `MessageBox` etc. | Aliases `MsgBox`, `MsgBoxButton`, `MsgBoxImage`, `MsgBoxResult` in MainViewModel.cs |
| `UserControl` (code-behind) | `System.Windows.Controls.UserControl` fully qualified in all 4 View files |
| `Application` (App.xaml.cs) | `System.Windows.Application` fully qualified |

### Box credentials & OAuth flow
Set `BoxApiProvider.ClientId` and `BoxApiProvider.ClientSecret` static properties (done by ViewModel before calling `AuthenticateAsync`).

OAuth2 browser flow step-by-step:
1. `FindFreeOAuthPort()` probes `OAuthPorts = { 49200, 49201, 49202, 49203, 49204 }` in order. Creates a short-lived `HttpListener` probe per port: `probe.Start()` succeeds → that port is free, return `(port, "http://localhost:{port}/callback")`. `HttpListenerException` → port has an existing http.sys URL ACL, try next.
2. `oauth.GetAuthorizeUrl(new GetAuthorizeUrlOptions { RedirectUri = callbackUri })` — the `RedirectUri` parameter is **required** when Box has multiple URIs registered; omitting it causes `redirect_uri_missing` in the browser.
3. `Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true })` opens the URL in the default browser.
4. `CaptureAuthCodeAsync(port, ct)` starts `HttpListener` on the chosen port, awaits `listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(2), ct)`. Catches `OperationCanceledException` **and** `TimeoutException` (WaitAsync throws `TimeoutException` on 2-min expiry, not `OperationCanceledException`). Returns null on either → `AuthenticateAsync` returns `false`.
5. `oauth.GetTokensAuthorizationCodeGrantAsync(authCode)` exchanges the auth code for tokens. No redirect_uri parameter needed here.
6. `_client = new BoxClient(auth: oauth)` — stored as a **static field** so all BoxApiProvider instances share the one authenticated client.

**All five redirect URIs must be registered in Box Developer Console → App → Configuration → Redirect URIs:**
```
http://localhost:49200/callback
http://localhost:49201/callback
http://localhost:49202/callback
http://localhost:49203/callback
http://localhost:49204/callback
```

### Box.Sdk.Gen API notes
- Request types in `Box.Sdk.Gen.Managers` namespace (NOT `Box.Sdk.Gen.Schemas`)
- `BoxApiException.ResponseInfo.StatusCode` (int) — not `BoxApiException.StatusCode`
- `GetAuthorizeUrlOptions` takes a `RedirectUri` string property
- `GetTokensAuthorizationCodeGrantAsync(authCode)` — just the code string, no redirect param
- Retry helper: `IsRateLimitOrTransient` → `code == 429 || code is >= 500 and <= 503`
- `MaxRetries = 4`, `BaseDelayMs = 500` → delays 500 ms, 1 s, 2 s, 4 s

### Data directory setup (InitServices in MainViewModel)
```csharp
string baseDir     = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ArchiveAutomator");
_logsDirectory     = Path.Combine(baseDir, "Logs");
_sessionsDirectory = Path.Combine(baseDir, "Sessions");
Directory.CreateDirectory(_logsDirectory);
Directory.CreateDirectory(_sessionsDirectory);
_sessionService = new SessionService(_sessionsDirectory);
```
Uses `%LocalAppData%` so the app works without elevation regardless of install location.
The old approach (navigating 4 levels up from `BaseDirectory`) only worked inside the VS solution tree.
