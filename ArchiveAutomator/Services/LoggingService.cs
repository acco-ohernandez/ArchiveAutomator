using ArchiveAutomator.Models;
using System.IO;
using System.Text;

namespace ArchiveAutomator.Services;

public class LoggingService
{
    private readonly string _logPath;
    // Instance-level lock: each LoggingService owns its own semaphore.
    // A static lock would serialize writes across all instances (wrong scope).
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Full path to this run's log file.</summary>
    public string LogFilePath => _logPath;

    /// <summary>
    /// Creates a new per-run log file named log_{runTimestamp}.csv in the given directory.
    /// </summary>
    /// <param name="logDirectory">Directory where log files are stored.</param>
    /// <param name="runTimestamp">Timestamp string (e.g. "20250301_143022") used in the filename.</param>
    public LoggingService(string logDirectory, string runTimestamp)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"log_{runTimestamp}.csv");
        EnsureHeader();
    }

    public async Task LogAsync(
        string jobId,
        StorageMode mode,
        OperationMode operation,
        string source,
        string destination,
        string result,
        string errorDetails = "")
    {
        string line = BuildLine(jobId, mode, operation, source, destination, result, errorDetails);

        await _lock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureHeader()
    {
        if (!File.Exists(_logPath))
            File.WriteAllText(_logPath, "Timestamp,JobID,Mode,Operation,Source,Destination,Result,ErrorDetails" + Environment.NewLine, Encoding.UTF8);
    }

    private static string BuildLine(
        string jobId, StorageMode mode, OperationMode operation,
        string source, string destination, string result, string errorDetails)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return string.Join(",",
            Escape(ts),
            Escape(jobId),
            Escape(mode.ToString()),
            Escape(operation.ToString()),
            Escape(source),
            Escape(destination),
            Escape(result),
            Escape(errorDetails));
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
