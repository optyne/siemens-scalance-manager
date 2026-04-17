using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Scalance.App.Services;

/// <summary>
/// Shared, singleton in-memory log of recent device operations.  Bound directly from
/// MainWindow's status strip so every VM gets to announce successes/failures without
/// owning its own UI surface.  VMs still update their local StatusMessage; this is the
/// history.
/// </summary>
public sealed partial class OperationLog : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    public ObservableCollection<OperationLogEntry> Entries { get; } = new();

    [ObservableProperty] private string? latestLine;

    public OperationLog()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Info(string message) => Append(OperationLogLevel.Info, message);
    public void Warn(string message) => Append(OperationLogLevel.Warn, message);
    public void Error(string message) => Append(OperationLogLevel.Error, message);

    private void Append(OperationLogLevel level, string message)
    {
        void Do()
        {
            var entry = new OperationLogEntry(DateTimeOffset.Now, level, message);
            Entries.Insert(0, entry);
            while (Entries.Count > 200) Entries.RemoveAt(Entries.Count - 1);
            LatestLine = $"[{entry.Timestamp:HH:mm:ss}] {message}";
        }
        if (_dispatcher.CheckAccess()) Do();
        else _dispatcher.Invoke(Do);
    }
}

public enum OperationLogLevel { Info, Warn, Error }

public sealed record OperationLogEntry(DateTimeOffset Timestamp, OperationLogLevel Level, string Message)
{
    public string TimestampDisplay => Timestamp.LocalDateTime.ToString("HH:mm:ss");
    public string LevelDisplay => Level.ToString();
}
