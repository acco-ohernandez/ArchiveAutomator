# Archive Automator

A Windows desktop application that automates the movement, copying, or deletion of project
folders based on a job list spreadsheet. Instead of relocating hundreds of folders manually,
you provide a CSV or Excel file, map the relevant columns, and let the tool process every
matching job in a single unattended run.

---

## Features

| Feature | Detail |
|---|---|
| **Storage backends** | Local File System · Box.com (OAuth 2.0 API) |
| **Operations** | Move · Copy · Delete |
| **Job list formats** | CSV (.csv) · Excel (.xlsx, .xlsm) |
| **Session resume** | Saves progress after every job; interrupted runs can be resumed |
| **Named profiles** | Save and reload complete configurations instantly |
| **Cross-drive Move** | Detected automatically — recursive copy → verify → delete source |
| **Locked-file safety** | In-use folders are marked Failed and skipped; run continues |
| **Box retry logic** | Exponential back-off on HTTP 429 / 5xx rate-limit responses |
| **Audit log** | Per-run CSV log written after every individual operation |
| **Pause / Cancel** | Stop at any time; session manifest preserves completed work |

---

## System Requirements

- **OS**: Windows 10 or Windows 11 (64-bit)
- **Runtime**: .NET 8.0 Desktop Runtime (or SDK)
- **Permissions**: Standard user account — no administrator rights required
- **Box.com mode only**: A Box developer account and an OAuth 2.0 app (one-time setup)

---

## Installation

1. Ensure the [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
   is installed on your machine.
2. Extract the release ZIP to any folder (e.g. `C:\Tools\ArchiveAutomator\`).
3. Launch `ArchiveAutomator.exe`.

All data files are written to your user profile — the application folder itself is never
written to, making it safe to run from a read-only or shared location.

---

## Quick Start — Local File System

1. **Mode section** → select **Local File System** · choose operation (**Move** is the default)
2. **Job List File** → click **Browse…** and open your CSV or Excel spreadsheet
3. **Column Mapping** → pick the Job Number column, Status column, and enter the Trigger Value
   (e.g. `Closed`)
4. **Folder Paths** → set Source Folder (where project folders live) and Archive Folder
   (destination); Archive is hidden when Delete mode is selected
5. Click **Load Jobs** — review the list in the **Matched Jobs (Pre-flight)** panel
6. Click **▶ Start** — watch real-time progress in the **Execution Log**
7. When complete, you are offered the option to open the log file

---

## Quick Start — Box.com

Complete the [one-time Box.com setup](#boxcom-setup) first, then:

1. **Mode section** → select **Box.com (API)** · choose operation
2. Enter your **Client ID** and **Client Secret**, then click **Sign in with Box…**
3. Complete authentication in your browser (the `✓ Authenticated` badge appears)
4. **Job List File** → browse to your spreadsheet
5. **Column Mapping** → map columns and trigger value
6. **Folder Paths** → enter the Box **folder IDs** for Source and Archive
   (see [Finding Box Folder IDs](#finding-box-folder-ids))
7. Click **Load Jobs**, review, then **▶ Start**

---

## Job List File Format

### Supported Formats

| Extension | Notes |
|---|---|
| `.csv` | Comma-separated, UTF-8 or ANSI encoding |
| `.xlsx` | Standard Open XML workbook; first worksheet only |
| `.xlsm` | Macro-enabled workbook; macros are not executed |

### Required Columns

| Column | Purpose |
|---|---|
| **Job Number** | The value used as the folder name to look up in the Source Folder |
| **Status** | Rows are processed only when this matches the **Trigger Value** exactly (case-sensitive) |

The file may contain any number of additional columns — only the two mapped columns are used.

### Example Spreadsheet

```
JobNumber  | Status   | Client      | Manager | Notes
---------- | -------- | ----------- | ------- | -------
10001      | Closed   | Acme Corp   | Smith   | Phase 1
10002      | Active   | Beta LLC    | Jones   |         <- skipped
10003      | Closed   | Gamma Inc   | Davis   | Phase 2
10004      | On Hold  | Delta Co    | Lee     |         <- skipped
10005      | Closed   | Epsilon Ltd | Kim     | Final
```

With **Job Number** = `JobNumber`, **Status** = `Status`, **Trigger** = `Closed`:
Jobs **10001**, **10003**, and **10005** are queued for processing.

### Column Mapping Tips

- The trigger value is **case-sensitive** — `Closed` is not the same as `closed`
- Row 1 must contain column headers; data starts on row 2
- Leading/trailing spaces in the job number are preserved — ensure they match the folder name
- For Excel files, close the file in Excel before loading it in Archive Automator

---

## Operations

### Move

Moves the source subfolder to the archive parent folder.

- **Same drive**: Uses a fast directory rename (near-instant)
- **Cross-drive**: Performs a recursive copy, verifies the destination file count matches,
  then deletes the source

### Copy

Copies the source subfolder into the archive parent folder, leaving the original in place.

### Delete

Permanently deletes the source subfolder and all its contents. The Archive Folder path
field is hidden when this mode is selected.

> **Warning**: Delete is irreversible. Review the Matched Jobs list carefully before starting.

---

## Folder Paths

### Local File System

Enter full paths to the parent directories:

```
Source Folder:   C:\Projects\Active
Archive Folder:  D:\Archive\2024
```

Archive Automator looks for a subfolder whose name matches each job number inside the
Source Folder, then moves/copies it into the Archive Folder.

**Example**: Job `10001` moves `C:\Projects\Active\10001` to `D:\Archive\2024\10001`

### Finding Box Folder IDs

In Box.com mode, the **Source Folder** and **Archive Folder** fields must contain Box
**folder IDs**, not names or file paths. To find a folder's ID, open the folder on Box.com
and read the number after `/folder/` in the URL:

```
https://app.box.com/folder/368694270455   ->   Folder ID = 368694270455
```

Archive Automator automatically resolves each job's subfolder name to its Box ID before
every operation — you never need to look up individual job folder IDs.

---

## Box.com Setup

### Step 1 — Create a Box Developer App

1. Sign in at [developer.box.com](https://developer.box.com) with your Box account
2. Go to **My Apps** → **Create New App**
3. Choose **Custom App** → **User Authentication (OAuth 2.0)**
4. Name the app (e.g. `Archive Automator`) and click **Create App**

### Step 2 — Copy Your Credentials

On the app's **Configuration** tab, copy the **Client ID** and **Client Secret** from the
OAuth 2.0 Credentials section and paste them into Archive Automator's **Mode** section.

### Step 3 — Register Redirect URIs

On the same Configuration tab, scroll to **Redirect URIs** and add all five entries:

```
http://localhost:49200/callback
http://localhost:49201/callback
http://localhost:49202/callback
http://localhost:49203/callback
http://localhost:49204/callback
```

> Archive Automator automatically picks the first available port at runtime. Registering all
> five prevents failures if any port is already in use by another application.
> These ports are in the IANA dynamic/ephemeral range (49152–65535).

### Step 4 — Authenticate

1. Paste your **Client ID** into the Client ID field
2. Type your **Client Secret** into the Client Secret field
3. Click **Sign in with Box…** — your browser opens the Box authorization page
4. Log in and click **Grant Access**
5. The browser shows *Authentication complete.* — return to Archive Automator
6. The **✓ Authenticated** badge appears — you are ready to run

### Security Notes

- The Client Secret is **never saved to disk** — you must re-enter it each session
- Saving a Settings Profile stores the Client ID but intentionally omits the Secret
- OAuth tokens are held in memory only and discarded when the application closes
- Authentication must be completed within 2 minutes

---

## Settings & Profiles

### Auto-Save

All settings (file paths, column mappings, storage mode, operation, Box Client ID) are
saved automatically every time a field changes. They are restored the next time you launch
the application — no manual action required.

```
%AppData%\ArchiveAutomator\settings.json
```

> **Tip**: Use **Edit → Open Settings File…** to open `settings.json` directly in your
> default text editor (Notepad, VS Code, etc.).

### Named Profiles

Named profiles let you switch between complete configurations instantly.

| Action | How |
|---|---|
| **Save** | Type a name in the Profile name field, click **Save Settings** |
| **Load** | Select a name from the dropdown, click **Load Settings** |

Loading a profile also clears the current job list and log, giving you a clean slate.

```
%AppData%\ArchiveAutomator\profiles\{profile-name}.json
```

---

## Session Resume

If a run is interrupted (Pause, Cancel, app closed, system restart), Archive Automator saves
a session manifest after every completed job. On next launch it detects the incomplete
session and offers to resume.

| Choice | Result |
|---|---|
| **Yes** | Loads saved settings, then overlays the pending jobs from the session |
| **No** | Starts completely blank — settings are **not** loaded either |

Already-completed jobs are never retried.

```
%LocalAppData%\ArchiveAutomator\Sessions\session_{RunId}.json
```

---

## Log Files

Each run produces a separate CSV log file:

```
%LocalAppData%\ArchiveAutomator\Logs\log_yyyyMMdd_HHmmss.csv
```

### Log Columns

| Column | Description |
|---|---|
| `Timestamp` | ISO 8601 date-time when the operation completed |
| `JobID` | Job number from the spreadsheet |
| `Mode` | `Local` or `Box` |
| `Operation` | `Move`, `Copy`, or `Delete` |
| `Source` | Full source path or Box folder ID |
| `Destination` | Full destination path or Box folder ID (empty for Delete) |
| `Result` | `Success` or `Failed` |
| `ErrorDetails` | Exception message for failed jobs; empty for successes |

Access logs quickly via the menu:

- **File → Open Logs Folder** — opens the Logs directory in File Explorer
- **File → Open Last Log** — opens the most recent log file directly
- **File → Open Sessions Folder** — opens the Sessions directory in File Explorer

---

## Cleanup Settings

Each run creates a new log file and a new session file on disk. To prevent unbounded growth,
Archive Automator automatically removes the oldest files at startup.

The limits are configurable in the collapsed **Cleanup Settings** section in the left panel:

| Field | Default | Description |
|---|---|---|
| **Max log files** | 100 | Maximum `log_*.csv` files to keep |
| **Max session files** | 100 | Maximum `session_*.json` files to keep |

- Files beyond the limit are deleted at startup; the **newest** files are always kept
- Cleanup results appear in the **Execution Log** on startup (only printed when files are deleted)
- The minimum value for either field is 1
- Cleanup runs **after** settings are loaded, so changes take effect on the very next launch

---

## Troubleshooting

### Job marked Failed — "locked or in use"

Another process has a file inside the folder open (File Explorer preview pane, Word, Outlook,
etc.). Close the application holding the file and either re-run that job individually, or
resume the session — already-successful jobs are never reprocessed.

### Job marked Failed — "not found inside parent folder"

The subfolder name from the spreadsheet was not found in the Source Folder. Common causes:

- The folder was already moved in a previous run
- The job number in the spreadsheet has extra spaces or different casing compared to the folder name
- The wrong Source Folder path or Box folder ID is configured
- (Box) The subfolder is under a different parent folder than the one specified

### Box: "redirect_uri_missing" shown in browser

The five redirect URIs have not been added to your Box app. Go to
developer.box.com → My Apps → your app → Configuration → Redirect URIs, add all five
`http://localhost:492xx/callback` entries, and save the Configuration page.

### Box: "No local port is available for the Box sign-in callback"

All five OAuth callback ports (49200–49204) are already registered with Windows http.sys by
other processes. Close any local development servers (Node.js, IIS Express, React dev server)
and try again.

### Box: Authentication timed out

The sign-in flow must be completed within 2 minutes. If the browser did not open
automatically, check your default browser setting in Windows and click **Sign in with Box…** again.

### No columns appear in the mapping dropdowns

- Verify the file is a valid CSV or Excel file
- Ensure row 1 contains column headers, not data
- For Excel: the file must not be open in Excel, and must not be password-protected

### Cross-drive Move is slow

When source and destination are on different drives, Archive Automator performs a full
recursive copy, verifies the destination file count, then deletes the source. This is expected
behaviour — large folders take time proportional to their total size.

---

## Data Locations Summary

| Data | Location |
|---|---|
| Auto-saved settings | `%AppData%\ArchiveAutomator\settings.json` |
| Named profiles | `%AppData%\ArchiveAutomator\profiles\` |
| Session manifests | `%LocalAppData%\ArchiveAutomator\Sessions\` |
| Audit logs | `%LocalAppData%\ArchiveAutomator\Logs\` |

All locations are inside your user profile — no administrator rights are ever required, and
the application folder is never written to.

---

## Building from Source

```bash
# Prerequisites: .NET 8.0 SDK, Windows

dotnet build ArchiveAutomator.sln          # Debug build
dotnet run --project ArchiveAutomator/ArchiveAutomator.csproj
dotnet build ArchiveAutomator.sln -c Release
```

### Key NuGet Packages

| Package | Version | Purpose |
|---|---|---|
| `Box.Sdk.Gen` | 1.12.0 | Box API — use this, **not** `Box.V2` (.NET Framework only) |
| `ClosedXML` | 0.105.0 | XLSX / XLSM parsing |
| `CsvHelper` | 33.1.0 | CSV parsing |
| `Newtonsoft.Json` | 13.0.4 | Session manifest and settings serialization |

---

*By O.R. Hernandez*
