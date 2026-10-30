using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace BatchRename.App.Models;

public enum ItemState
{
    Unchanged,
    Ok,
    Warning,
    Invalid,
    ConflictInBatch,
    ConflictOnDisk
}

/// <summary>规格书 §8.1。字段语义严格对齐，State/Message 由引擎产出。</summary>
public sealed class RenameItem : INotifyPropertyChanged
{
    public string FullPath { get; init; } = "";
    public string Directory { get; init; } = "";
    public string OriginalName { get; init; } = "";
    public string Stem { get; init; } = "";
    /// <summary>含前导点，可能为空串</summary>
    public string Ext { get; init; } = "";
    public bool IsDirectory { get; init; }
    public FileAttributes Attributes { get; init; }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    private string _newName = "";
    public string NewName
    {
        get => _newName;
        set
        {
            if (Set(ref _newName, value)) OnPropertyChanged(nameof(NewFullPath));
        }
    }

    public string NewFullPath => Path.Combine(Directory, NewName);

    private ItemState _state;
    public ItemState State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    private string? _message;
    public string? Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    private bool _caseOnly;
    public bool CaseOnly
    {
        get => _caseOnly;
        set => Set(ref _caseOnly, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
