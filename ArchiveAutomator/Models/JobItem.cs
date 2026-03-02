using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArchiveAutomator.Models;

public enum JobState { Pending, InProgress, Success, Failed }

/// <summary>
/// Represents a single archive job row.
/// Implements INotifyPropertyChanged so the DataGrid updates cells live
/// as State and ErrorDetails change during a run — without requiring a scroll.
/// </summary>
public class JobItem : INotifyPropertyChanged
{
    // ── Static / identity fields (never change after parsing) ──────────────

    private string _jobNumber   = string.Empty;
    private string _folderName  = string.Empty;
    private string _status      = string.Empty;

    public string JobNumber
    {
        get => _jobNumber;
        set => Set(ref _jobNumber, value);
    }

    public string FolderName
    {
        get => _folderName;
        set => Set(ref _folderName, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    // ── Mutable runtime fields (updated live during execution) ─────────────

    private JobState _state = JobState.Pending;
    public JobState State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    private string _errorDetails = string.Empty;
    public string ErrorDetails
    {
        get => _errorDetails;
        set => Set(ref _errorDetails, value);
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
