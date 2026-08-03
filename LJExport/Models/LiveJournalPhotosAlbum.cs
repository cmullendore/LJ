using System.ComponentModel;

namespace LJExport.Models;

public sealed class LiveJournalPhotosAlbum : INotifyPropertyChanged
{
    private bool isSelected;

    public string Name { get; init; } = string.Empty;

    public Uri Url { get; init; } = null!;

    public IReadOnlyList<LiveJournalPhoto> Photos { get; init; } = [];

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

public sealed record LiveJournalPhoto(Uri OriginalUri, string FileName);
