using ArchiveAutomator.Interfaces;
using ArchiveAutomator.Models;
using System.IO;

namespace ArchiveAutomator.Services;

public class OrchestratorProgress
{
    public int Completed { get; init; }
    public int Total { get; init; }
    public JobItem Job { get; init; } = null!;
    public string Message { get; init; } = string.Empty;
}

public class OrchestratorService
{
    private readonly IStorageProvider _provider;
    private readonly SessionService _sessionService;
    private readonly LoggingService _loggingService;

    public OrchestratorService(
        IStorageProvider provider,
        SessionService sessionService,
        LoggingService loggingService)
    {
        _provider = provider;
        _sessionService = sessionService;
        _loggingService = loggingService;
    }

    /// <summary>
    /// Runs the archive operation for all Pending/InProgress jobs in the manifest.
    /// Reports progress after every job. Saves the manifest after every job.
    /// </summary>
    public async Task RunAsync(
        SessionManifest manifest,
        string sourceFolderRoot,
        string archiveFolderRoot,
        IProgress<OrchestratorProgress>? progress = null,
        CancellationToken ct = default)
    {
        var pending = manifest.Jobs
            .Where(j => j.State == JobState.Pending || j.State == JobState.InProgress)
            .ToList();

        int total = pending.Count;
        int completed = 0;

        foreach (var job in pending)
        {
            ct.ThrowIfCancellationRequested();

            job.State = JobState.InProgress;
            await _sessionService.SaveAsync(manifest);

            string source      = BuildPath(sourceFolderRoot,  job);
            string destination = BuildPath(archiveFolderRoot, job);

            // Report "starting" message so the user can see what's happening in real time
            progress?.Report(new OrchestratorProgress
            {
                Completed = completed,
                Total = total,
                Job = job,
                Message = BuildStartMessage(job, manifest.Operation, source, destination)
            });

            try
            {
                await ProcessJobAsync(job, manifest.Operation, source, destination, manifest.Mode, ct);
                job.State = JobState.Success;
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Pending; // Allow resume
                await _sessionService.SaveAsync(manifest);
                throw;
            }
            catch (Exception ex)
            {
                job.State = JobState.Failed;
                job.ErrorDetails = ex.Message;

                await _loggingService.LogAsync(
                    job.JobNumber, manifest.Mode, manifest.Operation,
                    source, destination, "Failed", ex.Message);
            }

            completed++;
            await _sessionService.SaveAsync(manifest);

            progress?.Report(new OrchestratorProgress
            {
                Completed = completed,
                Total = total,
                Job = job,
                Message = BuildCompleteMessage(job, manifest.Operation, destination)
            });
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task ProcessJobAsync(
        JobItem job,
        OperationMode operation,
        string source,
        string destination,
        StorageMode mode,
        CancellationToken ct)
    {
        // Lock check only applies to Local mode Move/Copy
        if (mode == StorageMode.Local && operation != OperationMode.Delete)
        {
            // Distinguish "folder missing" from "folder locked" so the log message is actionable.
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"Source folder not found: '{source}'");

            bool canMove = await _provider.CanMoveAsync(source, ct);
            if (!canMove)
                throw new IOException($"Folder '{source}' is locked or in use by another process.");
        }

        switch (operation)
        {
            case OperationMode.Move:
                await _provider.MoveAsync(source, destination, ct);
                break;

            case OperationMode.Copy:
                await _provider.CopyAsync(source, destination, ct);
                break;

            case OperationMode.Delete:
                await _provider.DeleteAsync(source, ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        // Log success
        await _loggingService.LogAsync(
            job.JobNumber,
            mode,
            operation,
            source,
            operation == OperationMode.Delete ? string.Empty : destination,
            "Success");
    }

    /// <summary>
    /// Resolves a full path (or Box folder ID) from a root and a job's folder name.
    /// When <paramref name="root"/> is empty the folder name is used as-is (Box ID mode).
    /// </summary>
    private static string BuildPath(string root, JobItem job) =>
        string.IsNullOrEmpty(root)
            ? job.FolderName
            : Path.Combine(root, job.FolderName);

    // ── Progress message helpers ───────────────────────────────────────────

    private static string BuildStartMessage(JobItem job, OperationMode operation, string source, string destination)
    {
        string verb = operation switch
        {
            OperationMode.Move   => "Moving",
            OperationMode.Copy   => "Copying",
            OperationMode.Delete => "Deleting",
            _                    => operation.ToString()
        };

        return operation == OperationMode.Delete
            ? $"  [{verb}] {job.JobNumber}  ← {source}"
            : $"  [{verb}] {job.JobNumber}  {source}  →  {destination}";
    }

    private static string BuildCompleteMessage(JobItem job, OperationMode operation, string destination)
    {
        if (job.State == JobState.Failed)
            return $"  [✗ Failed]  {job.JobNumber}  — {job.ErrorDetails}";

        string verb = operation switch
        {
            OperationMode.Move   => "Moved",
            OperationMode.Copy   => "Copied",
            OperationMode.Delete => "Deleted",
            _                    => operation.ToString()
        };

        return operation == OperationMode.Delete
            ? $"  [✓ {verb}]  {job.JobNumber}"
            : $"  [✓ {verb}]  {job.JobNumber}  →  {destination}";
    }
}
