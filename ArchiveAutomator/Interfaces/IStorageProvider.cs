namespace ArchiveAutomator.Interfaces;

public interface IStorageProvider
{
    /// <summary>Checks whether the folder can be moved (no locked files).</summary>
    Task<bool> CanMoveAsync(string sourcePath, CancellationToken ct = default);

    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default);
    Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default);
    Task DeleteAsync(string sourcePath, CancellationToken ct = default);
}
