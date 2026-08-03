using System.ComponentModel;

namespace LJExport.Models;

public sealed class ScrapbookAlbum : INotifyPropertyChanged
{
    private bool isSelected;

    public string Name { get; init; } = string.Empty;

    public Uri Url { get; init; } = null!;

    public IReadOnlyList<ScrapbookPhoto> Photos { get; init; } = [];

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

public sealed record ScrapbookPhoto(Uri OriginalUri, string FileName);
