using ArchiveAutomator.Models;
using Newtonsoft.Json;
using System.IO;

namespace ArchiveAutomator.Services;

/// <summary>
/// Persists user settings.  BoxClientSecret is intentionally never persisted.
/// </summary>
public class PersistedSettings
{
    public string CsvFilePath { get; set; } = string.Empty;
    public string SourceFolderPath { get; set; } = string.Empty;
    public string ArchiveFolderPath { get; set; } = string.Empty;
    public StorageMode StorageMode { get; set; } = StorageMode.Local;
    public OperationMode OperationMode { get; set; } = OperationMode.Move;
    public string SelectedJobNumberColumn { get; set; } = string.Empty;
    public string SelectedStatusColumn { get; set; } = string.Empty;
    public string TriggerValue { get; set; } = "Closed";
    public string BoxClientId { get; set; } = string.Empty;
    // BoxClientSecret is intentionally NOT persisted for security
}

public class SettingsService
{
    // ── Last-used auto-save (settings.json) ───────────────────────────────

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArchiveAutomator",
        "settings.json");

    /// <summary>Loads the last auto-saved settings. Returns defaults if none exist.</summary>
    public PersistedSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonConvert.DeserializeObject<PersistedSettings>(json) ?? new PersistedSettings();
            }
        }
        catch { /* Return defaults on any read/parse error */ }
        return new PersistedSettings();
    }

    /// <summary>Auto-saves the current settings (called on every property change).</summary>
    public void Save(PersistedSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
        catch { /* Swallow save errors — settings are non-critical */ }
    }

    // ── Named profiles (%AppData%\ArchiveAutomator\profiles\) ─────────────

    private static string ProfilesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArchiveAutomator",
        "profiles");

    /// <summary>
    /// Saves the current settings as a named profile.
    /// The file is stored as <c>profiles\{name}.json</c>.
    /// </summary>
    public void SaveProfile(string profileName, PersistedSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            string path = Path.Combine(ProfilesDir, $"{SanitizeName(profileName)}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
        catch { /* non-critical */ }
    }

    /// <summary>
    /// Loads a named profile.  Returns <c>null</c> if the profile does not exist.
    /// </summary>
    public PersistedSettings? LoadProfile(string profileName)
    {
        try
        {
            string path = Path.Combine(ProfilesDir, $"{SanitizeName(profileName)}.json");
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<PersistedSettings>(File.ReadAllText(path));
        }
        catch { }
        return null;
    }

    /// <summary>Returns all saved profile names, sorted alphabetically.</summary>
    public List<string> ListProfiles()
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            return Directory.GetFiles(ProfilesDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Strips characters that are illegal in file names and trims whitespace.</summary>
    private static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length > 0 ? name : "Default";
    }
}
