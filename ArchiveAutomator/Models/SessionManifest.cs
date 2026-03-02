namespace ArchiveAutomator.Models;

public enum StorageMode { Local, Box }
public enum OperationMode { Move, Copy, Delete }

public class SessionManifest
{
    public string RunId { get; set; } = Guid.NewGuid().ToString();
    public string StartedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public StorageMode Mode { get; set; }
    public OperationMode Operation { get; set; }
    public List<JobItem> Jobs { get; set; } = new();
}
