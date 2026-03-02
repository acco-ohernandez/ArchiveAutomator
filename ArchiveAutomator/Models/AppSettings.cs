namespace ArchiveAutomator.Models;

public class ColumnMappings
{
    public string JobNumberColumn { get; set; } = string.Empty;
    public string StatusColumn { get; set; } = string.Empty;
    public string TriggerValue { get; set; } = string.Empty;
}

public class AppSettings
{
    public string SourcePath { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public StorageMode Mode { get; set; } = StorageMode.Local;
    public OperationMode Operation { get; set; } = OperationMode.Move;
    public ColumnMappings ColumnMappings { get; set; } = new();
}
