using ArchiveAutomator.Interfaces;
using System.IO;

namespace ArchiveAutomator.Providers;

public class LocalFileSystemProvider : IStorageProvider
{
    /// <summary>
    /// Lock check: attempt a temporary rename to verify no locked files exist.
    /// Returns false if the folder doesn't exist or an IOException occurs.
    /// </summary>
    public Task<bool> CanMoveAsync(string sourcePath, CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
            return Task.FromResult(false);

        string testPath = sourcePath.TrimEnd(Path.DirectorySeparatorChar) + "_archivetest_";
        try
        {
            Directory.Move(sourcePath, testPath);
            Directory.Move(testPath, sourcePath);
            return Task.FromResult(true);
        }
        catch (IOException)
        {
            // Locked files or in-use handles — rename back if partially moved
            if (Directory.Exists(testPath) && !Directory.Exists(sourcePath))
            {
                try { Directory.Move(testPath, sourcePath); } catch { /* best-effort */ }
            }
            return Task.FromResult(false);
        }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (IsCrossDrive(sourcePath, destinationPath))
        {
            // Cross-drive: copy then delete
            await CopyRecursiveAsync(sourcePath, destinationPath, ct);
            VerifyDestination(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }
        else
        {
            Directory.Move(sourcePath, destinationPath);
        }
    }

    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await CopyRecursiveAsync(sourcePath, destinationPath, ct);
    }

    public Task DeleteAsync(string sourcePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(sourcePath))
            Directory.Delete(sourcePath, recursive: true);
        return Task.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static bool IsCrossDrive(string source, string destination)
    {
        string? srcRoot = Path.GetPathRoot(source);
        string? dstRoot = Path.GetPathRoot(destination);
        return !string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyRecursiveAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            string dest = Path.Combine(destination, Path.GetFileName(file));
            await Task.Run(() => File.Copy(file, dest, overwrite: true), ct);
        }

        foreach (string subDir in Directory.GetDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            string destSub = Path.Combine(destination, Path.GetFileName(subDir));
            await CopyRecursiveAsync(subDir, destSub, ct);
        }
    }

    /// <summary>
    /// Basic sanity check: destination folder exists and has the same file count as the source.
    /// Throws if the check fails, so the caller can decide whether to delete the source.
    /// </summary>
    private static void VerifyDestination(string source, string destination)
    {
        if (!Directory.Exists(destination))
            throw new IOException($"Copy verification failed: destination '{destination}' does not exist.");

        int srcCount = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length;
        int dstCount = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length;

        if (srcCount != dstCount)
            throw new IOException(
                $"Copy verification failed: source has {srcCount} files, destination has {dstCount}.");
    }
}
