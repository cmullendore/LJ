using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LJExport.Models;

public sealed class JournalEntry : INotifyPropertyChanged
{
    private bool isSelected;

    public long ItemId { get; init; }

    public DateTimeOffset EventTime { get; init; }

    public string Subject { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    public string Poster { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record OperationProgress(string Message, int Completed, int Total)
{
    public double Percentage => Total == 0 ? 0 : (double)Completed / Total;
}

public enum ExportFormat
{
    Json,
    Xml
}
