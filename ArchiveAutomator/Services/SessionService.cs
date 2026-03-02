using ArchiveAutomator.Models;
using Newtonsoft.Json;
using System.IO;

namespace ArchiveAutomator.Services;

public class SessionService
{
    private readonly string _sessionDirectory;

    public SessionService(string sessionDirectory)
    {
        _sessionDirectory = sessionDirectory;
        Directory.CreateDirectory(_sessionDirectory);
    }

    /// <summary>Persists the manifest to disk (overwrites if already saved this run).</summary>
    public async Task SaveAsync(SessionManifest manifest)
    {
        string path = GetPath(manifest);
        string json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Returns the most recent incomplete session manifest (any job still Pending or InProgress),
    /// or <c>null</c> if no resumable session exists.
    /// Files are ordered by last-write time (newest first) — GUID-based names do NOT sort
    /// chronologically, so string ordering would silently pick the wrong session.
    /// </summary>
    public SessionManifest? FindResumable()
    {
        var files = Directory.GetFiles(_sessionDirectory, "session_*.json")
                             .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (string file in files)
        {
            try
            {
                string json = File.ReadAllText(file);
                var manifest = JsonConvert.DeserializeObject<SessionManifest>(json);
                if (manifest is null) continue;

                bool hasIncomplete = manifest.Jobs.Any(j =>
                    j.State == JobState.Pending || j.State == JobState.InProgress);

                if (hasIncomplete)
                    return manifest;
            }
            catch
            {
                // Corrupt or unreadable file — skip silently
            }
        }

        return null;
    }

    /// <summary>Returns all session files ordered newest-first (by write time).</summary>
    public IEnumerable<string> ListSessionFiles() =>
        Directory.GetFiles(_sessionDirectory, "session_*.json")
                 .OrderByDescending(File.GetLastWriteTimeUtc);

    private string GetPath(SessionManifest manifest) =>
        Path.Combine(_sessionDirectory, $"session_{manifest.RunId}.json");
}
