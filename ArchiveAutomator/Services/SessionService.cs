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
    /// Returns the most recent incomplete session manifest (any job not in Success state),
    /// or null if no resumable session exists.
    /// </summary>
    public SessionManifest? FindResumable()
    {
        var files = Directory.GetFiles(_sessionDirectory, "session_*.json")
                             .OrderByDescending(f => f);

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
                // Corrupt file — skip it
            }
        }

        return null;
    }

    /// <summary>Returns all session files ordered newest-first.</summary>
    public IEnumerable<string> ListSessionFiles() =>
        Directory.GetFiles(_sessionDirectory, "session_*.json")
                 .OrderByDescending(f => f);

    private string GetPath(SessionManifest manifest) =>
        Path.Combine(_sessionDirectory, $"session_{manifest.RunId}.json");
}
