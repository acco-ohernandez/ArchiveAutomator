using ArchiveAutomator.Models;
using ArchiveAutomator.Providers;
using ArchiveAutomator.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using MsgBox = System.Windows.MessageBox;
using MsgBoxResult = System.Windows.MessageBoxResult;
using MsgBoxButton = System.Windows.MessageBoxButton;
using MsgBoxImage = System.Windows.MessageBoxImage;

namespace ArchiveAutomator.ViewModels;

public class MainViewModel : ViewModelBase
{
    // ── Services ───────────────────────────────────────────────────────────
    private readonly ExcelParserService _parser       = new();
    private readonly SettingsService    _settingsService = new();
    private SessionService?          _sessionService;
    private OrchestratorService?     _orchestrator;
    private CancellationTokenSource? _cts;

    private string _logsDirectory     = string.Empty;
    private string _sessionsDirectory = string.Empty;
    private string _lastLogPath       = string.Empty;

    // Committed cleanup limits — what RunStartupCleanup() uses and what is written to settings.json.
    // Only updated when the user clicks Apply or Reset; never touched by ClearAll.
    private int _maxLogFiles     = 100;
    private int _maxSessionFiles = 100;

    // Draft cleanup limits — bound to the Cleanup Settings TextBoxes.
    // Typing changes these without saving; Apply commits them; Reset snaps both to 100 and saves.
    private int _maxLogFilesDraft     = 100;
    private int _maxSessionFilesDraft = 100;

    // Path to the auto-saved settings file — used by OpenSettingsFileCommand.
    private static readonly string _settingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArchiveAutomator",
        "settings.json");

    // StringBuilder backing for LogText — avoids O(n²) string allocation on every AppendLog call.
    private readonly StringBuilder _logBuffer = new();

    // Used internally to signal startup behaviour
    private enum SessionCheckResult { None, Resumed, Declined }

    // ── Bound Properties ───────────────────────────────────────────────────

    private StorageMode _storageMode = StorageMode.Local;
    public StorageMode StorageMode
    {
        get => _storageMode;
        set
        {
            Set(ref _storageMode, value);
            OnPropertyChanged(nameof(IsBoxMode));
            OnPropertyChanged(nameof(CanAuthenticate));
            SaveSettings();
        }
    }

    private OperationMode _operationMode = OperationMode.Move;
    public OperationMode OperationMode
    {
        get => _operationMode;
        set
        {
            Set(ref _operationMode, value);
            OnPropertyChanged(nameof(IsDeleteMode));
            OnPropertyChanged(nameof(CanStart));
            SaveSettings();
        }
    }

    private string _csvFilePath = string.Empty;
    public string CsvFilePath
    {
        get => _csvFilePath;
        set { Set(ref _csvFilePath, value); LoadHeaders(); SaveSettings(); }
    }

    private string _sourceFolderPath = string.Empty;
    public string SourceFolderPath
    {
        get => _sourceFolderPath;
        set { Set(ref _sourceFolderPath, value); OnPropertyChanged(nameof(CanStart)); SaveSettings(); }
    }

    private string _archiveFolderPath = string.Empty;
    public string ArchiveFolderPath
    {
        get => _archiveFolderPath;
        set { Set(ref _archiveFolderPath, value); OnPropertyChanged(nameof(CanStart)); SaveSettings(); }
    }

    private string _selectedJobNumberColumn = string.Empty;
    public string SelectedJobNumberColumn
    {
        get => _selectedJobNumberColumn;
        set { Set(ref _selectedJobNumberColumn, value); SaveSettings(); }
    }

    private string _selectedStatusColumn = string.Empty;
    public string SelectedStatusColumn
    {
        get => _selectedStatusColumn;
        set { Set(ref _selectedStatusColumn, value); SaveSettings(); }
    }

    private string _triggerValue = "Closed";
    public string TriggerValue
    {
        get => _triggerValue;
        set { Set(ref _triggerValue, value); SaveSettings(); }
    }

    private string _logText = string.Empty;
    public string LogText { get => _logText; set => Set(ref _logText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => Set(ref _progressValue, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { Set(ref _isRunning, value); OnPropertyChanged(nameof(CanStart)); } }

    private bool _isPaused;
    public bool IsPaused { get => _isPaused; set => Set(ref _isPaused, value); }

    private bool _isBoxAuthenticated;
    public bool IsBoxAuthenticated { get => _isBoxAuthenticated; set => Set(ref _isBoxAuthenticated, value); }

    private string _boxClientId = string.Empty;
    public string BoxClientId
    {
        get => _boxClientId;
        set { Set(ref _boxClientId, value); OnPropertyChanged(nameof(CanAuthenticate)); SaveSettings(); }
    }

    // Backing field set from code-behind via SetBoxClientSecret() — PasswordBox can't be data-bound.
    private string _boxClientSecret = string.Empty;
    public void SetBoxClientSecret(string secret)
    {
        _boxClientSecret = secret;
        OnPropertyChanged(nameof(CanAuthenticate));
    }

    public bool CanAuthenticate =>
        StorageMode == StorageMode.Box &&
        !string.IsNullOrWhiteSpace(_boxClientId) &&
        !string.IsNullOrWhiteSpace(_boxClientSecret);

    public bool IsBoxMode    => StorageMode == StorageMode.Box;
    public bool CanStart =>
        !IsRunning &&
        Jobs.Any(j => j.State == JobState.Pending) &&
        !string.IsNullOrWhiteSpace(_sourceFolderPath) &&
        (_operationMode == OperationMode.Delete || !string.IsNullOrWhiteSpace(_archiveFolderPath));
    public bool IsDeleteMode => OperationMode == OperationMode.Delete;

    // Draft properties — bound to the TextBoxes.  Typing updates these; Apply/Reset commit them.
    public int MaxLogFilesDraft
    {
        get => _maxLogFilesDraft;
        set
        {
            if (Set(ref _maxLogFilesDraft, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(IsCleanupDirty));
                ApplyCleanupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int MaxSessionFilesDraft
    {
        get => _maxSessionFilesDraft;
        set
        {
            if (Set(ref _maxSessionFilesDraft, Math.Max(1, value)))
            {
                OnPropertyChanged(nameof(IsCleanupDirty));
                ApplyCleanupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when draft values differ from committed values — drives Apply button.</summary>
    public bool IsCleanupDirty =>
        _maxLogFilesDraft     != _maxLogFiles ||
        _maxSessionFilesDraft != _maxSessionFiles;

    // ── Settings profile properties ────────────────────────────────────────

    private string _profileName = string.Empty;
    public string ProfileName
    {
        get => _profileName;
        set => Set(ref _profileName, value);
    }

    public ObservableCollection<string> AvailableProfiles { get; } = new();

    // ── Collections ────────────────────────────────────────────────────────

    public ObservableCollection<string> AvailableColumns { get; } = new();
    public ObservableCollection<JobItem> Jobs { get; } = new();

    // ── Commands ───────────────────────────────────────────────────────────

    public RelayCommand BrowseCsvCommand { get; }
    public RelayCommand BrowseSourceCommand { get; }
    public RelayCommand BrowseArchiveCommand { get; }
    public RelayCommand ParseCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AuthenticateBoxCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenLastLogCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand LoadProfileCommand { get; }
    public RelayCommand ShowHelpCommand { get; }
    public RelayCommand ShowAboutCommand { get; }
    public RelayCommand OpenSessionsFolderCommand { get; }
    public RelayCommand OpenSettingsFileCommand { get; }
    public RelayCommand ApplyCleanupCommand { get; }
    public RelayCommand ResetCleanupCommand { get; }

    public MainViewModel()
    {
        BrowseCsvCommand     = new RelayCommand(BrowseCsv);
        BrowseSourceCommand  = new RelayCommand(BrowseSource);
        BrowseArchiveCommand = new RelayCommand(BrowseArchive);
        ParseCommand         = new RelayCommand(ParseFile, () => !string.IsNullOrEmpty(CsvFilePath));
        StartCommand         = new RelayCommand(async () => await StartAsync(), () => CanStart);
        PauseCommand         = new RelayCommand(TogglePause, () => IsRunning);
        CancelCommand        = new RelayCommand(Cancel, () => IsRunning);
        AuthenticateBoxCommand = new RelayCommand(async () => await AuthenticateBoxAsync(), () => CanAuthenticate);
        OpenLogsFolderCommand  = new RelayCommand(OpenLogsFolder,
            () => !string.IsNullOrEmpty(_logsDirectory) && Directory.Exists(_logsDirectory));
        OpenLastLogCommand     = new RelayCommand(OpenLastLog,
            () => !string.IsNullOrEmpty(_lastLogPath) && File.Exists(_lastLogPath));
        ClearAllCommand        = new RelayCommand(ClearAll, () => !IsRunning);
        SaveProfileCommand     = new RelayCommand(ExecuteSaveProfile,
            () => !string.IsNullOrWhiteSpace(_profileName));
        LoadProfileCommand     = new RelayCommand(ExecuteLoadProfile,
            () => !string.IsNullOrWhiteSpace(_profileName));
        ShowHelpCommand        = new RelayCommand(ShowHelp);
        ShowAboutCommand       = new RelayCommand(ShowAbout);
        OpenSessionsFolderCommand = new RelayCommand(OpenSessionsFolder,
            () => !string.IsNullOrEmpty(_sessionsDirectory) && Directory.Exists(_sessionsDirectory));
        OpenSettingsFileCommand   = new RelayCommand(OpenSettingsFile,
            () => File.Exists(_settingsFilePath));
        ApplyCleanupCommand = new RelayCommand(ApplyCleanup, () => IsCleanupDirty);
        ResetCleanupCommand = new RelayCommand(ResetCleanup);

        InitServices();
        RefreshProfiles();

        // ── Startup order (important) ──────────────────────────────────────
        // 1. Ask about any resumable session FIRST.
        // 2. If user said Yes  → load last auto-saved settings (paths/columns),
        //    then overlay the session's Mode/Operation/Jobs on top.
        // 3. If user said No   → leave everything blank (fresh start).
        // 4. If no session     → load last auto-saved settings normally.
        var sessionResult = PromptResumableSession(out var sessionManifest);

        if (sessionResult != SessionCheckResult.Declined)
            LoadSettings();   // applies saved paths, columns, mode, etc.

        if (sessionResult == SessionCheckResult.Resumed && sessionManifest != null)
            ApplySession(sessionManifest);  // overlays Mode/Operation/Jobs from manifest

        // Cleanup runs LAST so it always uses the values loaded from settings above.
        RunStartupCleanup();
    }

    // ── Initialization ─────────────────────────────────────────────────────

    private void InitServices()
    {
        // Use %LocalAppData%\ArchiveAutomator\ so the app works without elevation
        // regardless of where the executable lives (zip extract, Program Files, etc.).
        // The old approach navigated up 4 levels from BaseDirectory, which only worked
        // inside the VS solution folder structure.
        string baseDir     = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArchiveAutomator");
        _logsDirectory     = Path.Combine(baseDir, "Logs");
        string sessionsDir = Path.Combine(baseDir, "Sessions");
        _sessionsDirectory = sessionsDir;

        // Pre-create both directories so they exist before any run starts.
        // Directory.CreateDirectory is a no-op if they already exist.
        Directory.CreateDirectory(_logsDirectory);
        Directory.CreateDirectory(sessionsDir);

        _sessionService = new SessionService(sessionsDir);
    }

    /// <summary>
    /// Checks for a resumable session and prompts the user.
    /// Returns the result so the constructor can decide whether to load saved settings.
    /// </summary>
    private SessionCheckResult PromptResumableSession(out SessionManifest? manifest)
    {
        manifest = _sessionService!.FindResumable();
        if (manifest is null) return SessionCheckResult.None;

        int pending = manifest.Jobs.Count(j =>
            j.State == JobState.Pending || j.State == JobState.InProgress);

        var answer = MsgBox.Show(
            $"A previous session was found with {pending} pending item(s).\nResume it?",
            "Resume Session",
            MsgBoxButton.YesNo,
            MsgBoxImage.Question);

        if (answer != MsgBoxResult.Yes)
        {
            manifest = null;                   // discard — caller will not load settings either
            return SessionCheckResult.Declined; // start completely fresh
        }

        return SessionCheckResult.Resumed;
    }

    /// <summary>Overlays Mode, Operation, and pending Jobs from a resumed manifest.</summary>
    private void ApplySession(SessionManifest manifest)
    {
        // Override mode directly in backing fields so SaveSettings isn't triggered redundantly
        _storageMode  = manifest.Mode;
        _operationMode = manifest.Operation;
        OnPropertyChanged(nameof(StorageMode));
        OnPropertyChanged(nameof(OperationMode));
        OnPropertyChanged(nameof(IsBoxMode));
        OnPropertyChanged(nameof(IsDeleteMode));
        OnPropertyChanged(nameof(CanAuthenticate));

        Jobs.Clear();
        foreach (var job in manifest.Jobs.Where(j =>
            j.State == JobState.Pending || j.State == JobState.InProgress))
            Jobs.Add(job);

        int count = Jobs.Count;
        AppendLog($"Resumed session {manifest.RunId} — {count} item(s) pending.");
        OnPropertyChanged(nameof(CanStart));
    }

    // ── Settings (auto-save) ───────────────────────────────────────────────

    /// <summary>
    /// Loads the auto-saved settings and applies them to all input fields.
    /// Also used after loading a named profile (via <see cref="ApplySettings"/>).
    /// </summary>
    private void LoadSettings() => ApplySettings(_settingsService.Load());

    /// <summary>
    /// Applies a <see cref="PersistedSettings"/> snapshot to all input fields,
    /// populates column headers, then fires property-change notifications.
    /// Backing fields are set directly to avoid re-triggering SaveSettings.
    /// </summary>
    private void ApplySettings(PersistedSettings s)
    {
        _csvFilePath      = s.CsvFilePath;
        _sourceFolderPath = s.SourceFolderPath;
        _archiveFolderPath= s.ArchiveFolderPath;
        _storageMode      = s.StorageMode;
        _operationMode    = s.OperationMode;
        _triggerValue     = s.TriggerValue;
        _boxClientId      = s.BoxClientId;
        _maxLogFiles          = Math.Max(1, s.MaxLogFiles);
        _maxSessionFiles      = Math.Max(1, s.MaxSessionFiles);
        // Sync draft to committed so the TextBoxes reflect the loaded values
        // and the Apply button starts disabled (nothing has changed yet).
        _maxLogFilesDraft     = _maxLogFiles;
        _maxSessionFilesDraft = _maxSessionFiles;

        // Populate AvailableColumns BEFORE notifying column selections so that
        // the ComboBox items exist when the binding resolves.
        if (!string.IsNullOrEmpty(_csvFilePath) && File.Exists(_csvFilePath))
            LoadHeaders();

        _selectedJobNumberColumn = s.SelectedJobNumberColumn;
        _selectedStatusColumn    = s.SelectedStatusColumn;

        // Notify all UI bindings
        OnPropertyChanged(nameof(CsvFilePath));
        OnPropertyChanged(nameof(SourceFolderPath));
        OnPropertyChanged(nameof(ArchiveFolderPath));
        OnPropertyChanged(nameof(StorageMode));
        OnPropertyChanged(nameof(OperationMode));
        OnPropertyChanged(nameof(SelectedJobNumberColumn));
        OnPropertyChanged(nameof(SelectedStatusColumn));
        OnPropertyChanged(nameof(TriggerValue));
        OnPropertyChanged(nameof(BoxClientId));
        OnPropertyChanged(nameof(IsBoxMode));
        OnPropertyChanged(nameof(CanAuthenticate));
        OnPropertyChanged(nameof(MaxLogFilesDraft));
        OnPropertyChanged(nameof(MaxSessionFilesDraft));
        OnPropertyChanged(nameof(IsCleanupDirty));
        ApplyCleanupCommand?.RaiseCanExecuteChanged();
    }

    private void SaveSettings()
    {
        _settingsService.Save(new PersistedSettings
        {
            CsvFilePath             = _csvFilePath,
            SourceFolderPath        = _sourceFolderPath,
            ArchiveFolderPath       = _archiveFolderPath,
            StorageMode             = _storageMode,
            OperationMode           = _operationMode,
            SelectedJobNumberColumn = _selectedJobNumberColumn,
            SelectedStatusColumn    = _selectedStatusColumn,
            TriggerValue            = _triggerValue,
            BoxClientId             = _boxClientId,
            MaxLogFiles             = _maxLogFiles,
            MaxSessionFiles         = _maxSessionFiles
        });
    }

    // ── Named profiles ─────────────────────────────────────────────────────

    private void RefreshProfiles()
    {
        var profiles = _settingsService.ListProfiles();
        AvailableProfiles.Clear();
        foreach (var p in profiles)
            AvailableProfiles.Add(p);
    }

    private void ExecuteSaveProfile()
    {
        if (string.IsNullOrWhiteSpace(_profileName)) return;

        _settingsService.SaveProfile(_profileName, new PersistedSettings
        {
            CsvFilePath             = _csvFilePath,
            SourceFolderPath        = _sourceFolderPath,
            ArchiveFolderPath       = _archiveFolderPath,
            StorageMode             = _storageMode,
            OperationMode           = _operationMode,
            SelectedJobNumberColumn = _selectedJobNumberColumn,
            SelectedStatusColumn    = _selectedStatusColumn,
            TriggerValue            = _triggerValue,
            BoxClientId             = _boxClientId,
            MaxLogFiles             = _maxLogFiles,
            MaxSessionFiles         = _maxSessionFiles
        });

        RefreshProfiles();
        AppendLog($"Settings saved as profile \"{_profileName}\".");
    }

    private void ExecuteLoadProfile()
    {
        if (string.IsNullOrWhiteSpace(_profileName)) return;

        var s = _settingsService.LoadProfile(_profileName);
        if (s is null)
        {
            MsgBox.Show($"Profile \"{_profileName}\" was not found.",
                "Load Profile", MsgBoxButton.OK, MsgBoxImage.Warning);
            return;
        }

        ApplySettings(s);

        // Clear runtime state so the user starts fresh with the loaded settings
        Jobs.Clear();
        _logBuffer.Clear();
        LogText       = string.Empty;
        ProgressValue = 0;
        OnPropertyChanged(nameof(CanStart));

        AppendLog($"Profile \"{_profileName}\" loaded.");
    }

    // ── Browse Handlers ────────────────────────────────────────────────────

    private void BrowseCsv()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Spreadsheet files|*.csv;*.xlsx;*.xlsm|All files|*.*",
            Title  = "Select job list file"
        };
        if (dlg.ShowDialog() == true)
            CsvFilePath = dlg.FileName;
    }

    private void BrowseSource()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Select source folder" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SourceFolderPath = dlg.SelectedPath;
    }

    private void BrowseArchive()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Select archive/destination folder" };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ArchiveFolderPath = dlg.SelectedPath;
    }

    // ── Column Header Loading ──────────────────────────────────────────────

    private void LoadHeaders()
    {
        if (string.IsNullOrEmpty(CsvFilePath) || !File.Exists(CsvFilePath)) return;
        try
        {
            var headers = _parser.ReadHeaders(CsvFilePath);
            AvailableColumns.Clear();
            foreach (var h in headers)
                AvailableColumns.Add(h);
        }
        catch (Exception ex)
        {
            AppendLog($"Error reading headers: {ex.Message}");
        }
    }

    // ── Parse ──────────────────────────────────────────────────────────────

    private void ParseFile()
    {
        if (string.IsNullOrEmpty(SelectedJobNumberColumn) || string.IsNullOrEmpty(SelectedStatusColumn))
        {
            MsgBox.Show("Please select both the Job Number column and Status column.", "Mapping Required",
                MsgBoxButton.OK, MsgBoxImage.Warning);
            return;
        }

        // Clear previous results so the user starts with a clean slate each load
        Jobs.Clear();
        _logBuffer.Clear();
        LogText       = string.Empty;
        ProgressValue = 0;
        OnPropertyChanged(nameof(CanStart));

        try
        {
            var mapping = new ColumnMappings
            {
                JobNumberColumn = SelectedJobNumberColumn,
                StatusColumn    = SelectedStatusColumn,
                TriggerValue    = TriggerValue
            };

            var items = _parser.Parse(CsvFilePath, mapping);
            foreach (var item in items)
                Jobs.Add(item);

            AppendLog($"Parsed {items.Count} matching job(s) from \"{Path.GetFileName(CsvFilePath)}\".");
            OnPropertyChanged(nameof(CanStart));
        }
        catch (Exception ex)
        {
            AppendLog($"Parse error: {ex.Message}");
        }
    }

    // ── Run ────────────────────────────────────────────────────────────────

    private async Task StartAsync()
    {
        if (StorageMode == StorageMode.Box && !IsBoxAuthenticated)
        {
            MsgBox.Show("Please authenticate with Box before starting.", "Not Authenticated",
                MsgBoxButton.OK, MsgBoxImage.Warning);
            return;
        }

        // Set UI state before the try so the finally always has a clean IsRunning=false to reset to.
        IsRunning     = true;
        IsPaused      = false;
        ProgressValue = 0;
        _cts          = new CancellationTokenSource();

        try
        {
            // Everything from LoggingService creation onward is inside the try/catch so that
            // any setup failure (bad path, disk full, etc.) is caught and shown in the log
            // rather than escaping as an unhandled exception and terminating the app.
            string runTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var loggingService  = new LoggingService(_logsDirectory, runTimestamp);
            _lastLogPath        = loggingService.LogFilePath;

            var manifest = new SessionManifest
            {
                Mode      = StorageMode,
                Operation = OperationMode,
                Jobs      = Jobs.ToList()
            };

            var provider = StorageMode == StorageMode.Local
                ? (ArchiveAutomator.Interfaces.IStorageProvider)new LocalFileSystemProvider()
                : new BoxApiProvider();

            _orchestrator = new OrchestratorService(provider, _sessionService!, loggingService);

            int pendingCount = Jobs.Count(j => j.State == JobState.Pending);
            AppendLog($"Starting {OperationMode} run — {pendingCount} job(s) pending.  Log: {Path.GetFileName(_lastLogPath)}");

            var progress = new Progress<OrchestratorProgress>(p =>
            {
                ProgressValue = (double)p.Completed / p.Total * 100;
                AppendLog(p.Message);
                // No manual Jobs notification needed — JobItem now implements
                // INotifyPropertyChanged, so the DataGrid updates cells automatically
                // as State and ErrorDetails change on each job object.
            });

            await _orchestrator.RunAsync(manifest, SourceFolderPath, ArchiveFolderPath, progress, _cts.Token);
            AppendLog("Run complete.");
            OfferToOpenLog();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Run cancelled. Progress saved — you can resume later.");
        }
        catch (Exception ex)
        {
            AppendLog($"Unexpected error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void TogglePause()
    {
        if (!IsPaused)
        {
            _cts?.Cancel();
            IsPaused = true;
            AppendLog("Pausing after current job…");
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        AppendLog("Cancelling…");
    }

    // ── Box Auth ───────────────────────────────────────────────────────────

    private async Task AuthenticateBoxAsync()
    {
        try
        {
            BoxApiProvider.ClientId     = _boxClientId.Trim();
            BoxApiProvider.ClientSecret = _boxClientSecret;

            var boxProvider = new BoxApiProvider();
            bool ok = await boxProvider.AuthenticateAsync();
            IsBoxAuthenticated = ok;
            AppendLog(ok ? "Box authentication successful." : "Box authentication failed or was cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"Box auth error: {ex.Message}");
        }
    }

    // ── Clear All ──────────────────────────────────────────────────────────

    private void ClearAll()
    {
        var result = MsgBox.Show(
            "Clear all fields and start fresh?\n\nThis resets the form and clears saved settings. No files will be deleted.",
            "New Run — Clear All Fields",
            MsgBoxButton.YesNo,
            MsgBoxImage.Question);

        if (result != MsgBoxResult.Yes) return;

        // Clear runtime state
        Jobs.Clear();
        AvailableColumns.Clear();
        _logBuffer.Clear();
        LogText            = string.Empty;
        ProgressValue      = 0;
        IsBoxAuthenticated = false;
        _boxClientSecret   = string.Empty;
        _lastLogPath       = string.Empty;

        // Reset all input fields to defaults (set backing fields directly, then notify)
        _csvFilePath             = string.Empty;
        _sourceFolderPath        = string.Empty;
        _archiveFolderPath       = string.Empty;
        _storageMode             = StorageMode.Local;
        _operationMode           = OperationMode.Move;
        _selectedJobNumberColumn = string.Empty;
        _selectedStatusColumn    = string.Empty;
        _triggerValue            = "Closed";
        _boxClientId             = string.Empty;
        _profileName             = string.Empty;
        // Cleanup limits are intentionally NOT reset here — the user sets those
        // manually via Apply/Reset and they persist independently of New Run.

        OnPropertyChanged(nameof(CsvFilePath));
        OnPropertyChanged(nameof(SourceFolderPath));
        OnPropertyChanged(nameof(ArchiveFolderPath));
        OnPropertyChanged(nameof(StorageMode));
        OnPropertyChanged(nameof(OperationMode));
        OnPropertyChanged(nameof(SelectedJobNumberColumn));
        OnPropertyChanged(nameof(SelectedStatusColumn));
        OnPropertyChanged(nameof(TriggerValue));
        OnPropertyChanged(nameof(BoxClientId));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(IsBoxMode));
        OnPropertyChanged(nameof(IsDeleteMode));
        OnPropertyChanged(nameof(CanAuthenticate));
        OnPropertyChanged(nameof(CanStart));

        // Wipe the last-used auto-save so next launch also starts fresh
        SaveSettings();
    }

    // ── Cleanup Settings Apply / Reset ─────────────────────────────────────

    /// <summary>
    /// Commits draft cleanup limits to the settled values and saves to disk.
    /// Called by ApplyCleanupCommand (only enabled when IsCleanupDirty).
    /// </summary>
    private void ApplyCleanup()
    {
        _maxLogFiles     = _maxLogFilesDraft;
        _maxSessionFiles = _maxSessionFilesDraft;
        SaveSettings();
        OnPropertyChanged(nameof(IsCleanupDirty));
        ApplyCleanupCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Resets both draft and committed cleanup limits to 100 and saves to disk immediately.
    /// No separate Apply click is needed — the Apply button will become disabled
    /// because draft == committed == 100 after this call.
    /// </summary>
    private void ResetCleanup()
    {
        _maxLogFilesDraft     = 100;
        _maxSessionFilesDraft = 100;
        _maxLogFiles          = 100;
        _maxSessionFiles      = 100;
        OnPropertyChanged(nameof(MaxLogFilesDraft));
        OnPropertyChanged(nameof(MaxSessionFilesDraft));
        OnPropertyChanged(nameof(IsCleanupDirty));
        ApplyCleanupCommand.RaiseCanExecuteChanged();
        SaveSettings();
    }

    // ── Log file helpers ───────────────────────────────────────────────────

    private void OfferToOpenLog()
    {
        if (!File.Exists(_lastLogPath)) return;

        var result = MsgBox.Show(
            $"Run complete. Would you like to open the log file?\n\n{Path.GetFileName(_lastLogPath)}",
            "Run Complete",
            MsgBoxButton.YesNo,
            MsgBoxImage.Question);

        if (result == MsgBoxResult.Yes)
            OpenLastLog();
    }

    private void OpenLogsFolder()
    {
        if (Directory.Exists(_logsDirectory))
            Process.Start(new ProcessStartInfo(_logsDirectory) { UseShellExecute = true });
    }

    private void OpenLastLog()
    {
        if (File.Exists(_lastLogPath))
            Process.Start(new ProcessStartInfo(_lastLogPath) { UseShellExecute = true });
    }

    // ── Startup cleanup ────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the oldest log and session files beyond the configured limits.
    /// Called once at startup, after settings are loaded, so the configured
    /// limits are always respected.  Results are reported in the Execution Log.
    /// </summary>
    private void RunStartupCleanup()
    {
        int deletedLogs     = DeleteOldestFiles(_logsDirectory,     "log_*.csv",      _maxLogFiles);
        int deletedSessions = DeleteOldestFiles(_sessionsDirectory, "session_*.json", _maxSessionFiles);

        if (deletedLogs > 0)
            AppendLog($"Startup cleanup: removed {deletedLogs} old log file(s) — kept newest {_maxLogFiles}.");
        if (deletedSessions > 0)
            AppendLog($"Startup cleanup: removed {deletedSessions} old session file(s) — kept newest {_maxSessionFiles}.");
    }

    /// <summary>
    /// Keeps the <paramref name="maxKeep"/> newest files matching <paramref name="pattern"/>
    /// in <paramref name="directory"/> and deletes the rest.
    /// Returns the number of files actually deleted.
    /// </summary>
    private static int DeleteOldestFiles(string directory, string pattern, int maxKeep)
    {
        if (!Directory.Exists(directory)) return 0;

        var files = Directory.GetFiles(directory, pattern)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        if (files.Count <= maxKeep) return 0;

        int deleted = 0;
        foreach (var file in files.Skip(maxKeep))
        {
            try   { file.Delete(); deleted++; }
            catch { /* skip any file that is locked or inaccessible */ }
        }
        return deleted;
    }

    // ── Help / About ───────────────────────────────────────────────────────

    private static void ShowHelp()
    {
        var win = new ArchiveAutomator.Views.HelpWindow();
        win.Show();
    }

    private static void ShowAbout() =>
        MsgBox.Show(
            "Archive Automator\n" +
            "Version 1.0\n\n" +
            "Automates the movement, copying, or deletion of project\n" +
            "folders based on a job list spreadsheet (CSV / XLSX).\n\n" +
            "Supports Local File System and Box.com (API).\n\n" +
            "By O.R. Hernandez",
            "About Archive Automator",
            MsgBoxButton.OK,
            MsgBoxImage.Information);

    private void OpenSessionsFolder()
    {
        if (Directory.Exists(_sessionsDirectory))
            Process.Start(new ProcessStartInfo(_sessionsDirectory) { UseShellExecute = true });
    }

    private static void OpenSettingsFile()
    {
        if (File.Exists(_settingsFilePath))
            Process.Start(new ProcessStartInfo(_settingsFilePath) { UseShellExecute = true });
    }

    // ── Logging ────────────────────────────────────────────────────────────

    private void AppendLog(string message)
    {
        _logBuffer.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ")
                  .AppendLine(message);
        LogText = _logBuffer.ToString();
    }
}
