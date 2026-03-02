namespace ArchiveAutomator.Models;

/// <summary>
/// Column-mapping parameters selected by the user in the UI.
/// Passed to <see cref="Services.ExcelParserService"/> for each parse operation.
/// </summary>
public class ColumnMappings
{
    public string JobNumberColumn { get; set; } = string.Empty;
    public string StatusColumn    { get; set; } = string.Empty;
    public string TriggerValue    { get; set; } = string.Empty;
}
// Note: The AppSettings class that previously lived here was unused dead code and has been removed.
// Runtime settings are persisted via Services.SettingsService / Services.PersistedSettings.
