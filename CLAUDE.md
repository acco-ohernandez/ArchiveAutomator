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
- `Box.Sdk.Gen` 1.12.0 — Box API (NOT `Box.V2` — that is .NET Framework only)
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
Sessions/       session JSON output — at solution root
Logs/           per-run log_[timestamp].csv output — at solution root
```

## Architecture

### Key interfaces & models

**`IStorageProvider`** — `CanMoveAsync`, `MoveAsync`, `CopyAsync`, `DeleteAsync`.

**`JobItem`** — `JobNumber`, `FolderName`, `Status`, `State` (`Pending`/`InProgress`/`Success`/`Failed`), `ErrorDetails`.

**`SessionManifest`** — `RunId` (GUID), `StartedAt` (ISO8601), `Mode`, `Operation`, `List<JobItem>`. Written to `/Sessions/session_[RunId].json`; updated after every individual operation.

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
One file per run; named `log_yyyyMMdd_HHmmss.csv` in `/Logs/`.

### Settings persistence
`SettingsService` has two layers:
1. **Auto-save** (`settings.json`) — saves to `%AppData%\ArchiveAutomator\settings.json` on every property change. Loaded on startup **only when no resumable session was declined** (see Resume logic below).
2. **Named profiles** (`profiles\*.json`) — explicit Save/Load via the Settings Profile GroupBox. Stored in `%AppData%\ArchiveAutomator\profiles\{name}.json`. `ListProfiles()` scans the folder; `SanitizeName()` strips illegal filename chars.

### Resume dialog behaviour (fixed)
- **No session found** → auto-load last settings normally.
- **Session found, user clicks Yes** → auto-load settings (paths/columns), then overlay Mode/Operation/Jobs from the manifest on top.
- **Session found, user clicks No** → start completely blank — settings are NOT loaded. This prevents the old job paths from silently reappearing.

`PromptResumableSession()` returns `SessionCheckResult` (`None` / `Resumed` / `Declined`). The constructor calls `LoadSettings()` only for `None` or `Resumed`, then calls `ApplySession()` only for `Resumed`.

### UI layout (MainWindow.xaml)
Two-panel layout with a vertical `GridSplitter` between left and right.

**Outer `DockPanel`**: Menu (Top) → StatusBar (Bottom) → Action bar (Bottom) → main Grid fills rest.

**Left panel** (`ScrollViewer` → `StackPanel` of `Expander` sections, initial width 360 px, draggable):
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

**`MappingView.xaml`** was updated to a vertical 3-row `Grid` (Job Number col, Status col, Trigger value + Load Jobs) so it fits the narrow left panel.

**`CanStart` validation**:
- Move or Copy: `SourceFolderPath` AND `ArchiveFolderPath` must be non-empty.
- Delete: only `SourceFolderPath` required.
- `OnPropertyChanged(nameof(CanStart))` fired from `SourceFolderPath`, `ArchiveFolderPath`, and `OperationMode` setters.
- `OnPropertyChanged(nameof(IsDeleteMode))` fired from `OperationMode` setter, `ApplySession`, and `ClearAll`.

### Known namespace conflicts (UseWindowsForms=true)
| Ambiguous type | Fix |
|---|---|
| `Task` | `using Task = System.Threading.Tasks.Task;` in BoxApiProvider.cs |
| `Binding.DoNothing` | Use `System.Windows.Data.Binding.DoNothing` fully qualified in Converters.cs |
| `MessageBox` etc. | Aliases `MsgBox`, `MsgBoxButton`, `MsgBoxImage`, `MsgBoxResult` in MainViewModel.cs |
| `UserControl` (code-behind) | `System.Windows.Controls.UserControl` fully qualified in all 4 View files |
| `Application` (App.xaml.cs) | `System.Windows.Application` fully qualified |

### Box credentials
Set `BoxApiProvider.ClientId` and `BoxApiProvider.ClientSecret` static properties (done by ViewModel before calling `AuthenticateAsync`). OAuth2 browser flow: `BoxOAuth.GetAuthorizeUrl()` (no params); `GetTokensAuthorizationCodeGrantAsync(authCode)` (code only, no redirect param). Local `HttpListener` on `http://localhost:4545/` captures the redirect.
