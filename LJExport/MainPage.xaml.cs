using System.Collections.ObjectModel;
using LJExport.Models;
using LJExport.Services;
using Serilog;

namespace LJExport;

public partial class MainPage : ContentPage
{
    private static readonly ILogger Logger = Log.ForContext<MainPage>();
    private readonly LiveJournalClient liveJournalClient;
    private readonly JournalExportService exportService;
    private readonly LiveJournalPhotosClient liveJournalPhotosClient;
    private readonly ObservableCollection<JournalEntry> entries = [];
    private readonly ObservableCollection<LiveJournalPhotosAlbum> albums = [];
    private LiveJournalSession? session;
    private bool updatingSelection;

    public MainPage(LiveJournalClient liveJournalClient, JournalExportService exportService, LiveJournalPhotosClient liveJournalPhotosClient)
    {
        InitializeComponent();
        this.liveJournalClient = liveJournalClient;
        this.exportService = exportService;
        this.liveJournalPhotosClient = liveJournalPhotosClient;
        EntriesView.ItemsSource = entries;
        AlbumsView.ItemsSource = albums;
    }

    private async void OnScanClicked(object? sender, EventArgs e)
    {
        if (!HasCredentials())
        {
            Logger.Warning("Journal scan was requested without complete credentials");
            await DisplayAlertAsync("Credentials required", "Enter your LiveJournal username and password.", "OK");
            return;
        }

        var username = UsernameEntry.Text!.Trim();
        Logger.Information("Journal scan requested for {Username}", username);
        await RunOperationAsync("Journal scan", async (progress, token) =>
        {
            var scannedEntries = await liveJournalClient.GetAllEntriesAsync(username, PasswordEntry.Text!, progress, token);
            entries.Clear();
            foreach (var entry in scannedEntries)
            {
                entries.Add(entry);
            }

            SelectAllCheckBox.IsChecked = false;
            StatusLabel.Text = $"Found {entries.Count} journal entries.";
            UpdateExportButton();
            UpdatePhotoExportButton();
        });
    }

    private async void OnScanPhotosClicked(object? sender, EventArgs e)
    {
        if (!HasCredentials())
        {
            Logger.Warning("LiveJournal Photos scan was requested without complete credentials");
            await DisplayAlertAsync("Credentials required", "Enter your LiveJournal username and password.", "OK");
            return;
        }

        var username = UsernameEntry.Text!.Trim();
        Logger.Information("LiveJournal Photos scan requested for {Username}", username);
        await RunOperationAsync("LiveJournal Photos scan", async (progress, token) =>
        {
            session = await liveJournalClient.AuthenticateAsync(username, PasswordEntry.Text!, token);
            var scannedAlbums = await liveJournalPhotosClient.GetAlbumsAsync(session, progress, token);
            albums.Clear();
            foreach (var album in scannedAlbums)
            {
                albums.Add(album);
            }

            SelectAllAlbumsCheckBox.IsChecked = false;
            StatusLabel.Text = $"Found {albums.Count} LiveJournal Photos albums.";
            UpdatePhotoExportButton();
        });
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DirectoryEntry.Text))
        {
            Logger.Warning("Journal export was requested without an export directory");
            await DisplayAlertAsync("Export directory required", "Choose a directory for the exported files.", "OK");
            return;
        }

        if (!entries.Any(entry => entry.IsSelected))
        {
            Logger.Warning("Journal export was requested with no selected entries");
            await DisplayAlertAsync("No entries selected", "Select at least one entry to export.", "OK");
            return;
        }

        Logger.Information("Journal export requested to {ExportDirectory}", DirectoryEntry.Text.Trim());
        await RunOperationAsync("Journal export", async (progress, token) =>
        {
            await exportService.ExportAsync(entries, DirectoryEntry.Text.Trim(), JsonRadioButton.IsChecked ? ExportFormat.Json : ExportFormat.Xml, progress, token);
            StatusLabel.Text = "Export complete.";
        });
    }

    private async void OnExportPhotosClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DirectoryEntry.Text))
        {
            Logger.Warning("Photo export was requested without an export directory");
            await DisplayAlertAsync("Export directory required", "Choose a directory for the exported photos.", "OK");
            return;
        }

        if (!albums.Any(album => album.IsSelected))
        {
            Logger.Warning("LiveJournal Photos export was requested with no selected albums");
            await DisplayAlertAsync("No albums selected", "Select at least one LiveJournal Photos album to export.", "OK");
            return;
        }

        Logger.Information("LiveJournal Photos export requested to {ExportDirectory}", DirectoryEntry.Text.Trim());
        await RunOperationAsync("LiveJournal Photos export", async (progress, token) =>
        {
            if (session is null)
            {
                session = await liveJournalClient.AuthenticateAsync(UsernameEntry.Text!.Trim(), PasswordEntry.Text!, token);
            }

            await liveJournalPhotosClient.ExportAlbumsAsync(albums, DirectoryEntry.Text.Trim(), session, progress, token);
            StatusLabel.Text = "LiveJournal Photos export complete.";
        });
    }

    private async void OnBrowseClicked(object? sender, EventArgs e)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var window = (Microsoft.Maui.MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            DirectoryEntry.Text = folder.Path;
            Logger.Information("Export directory selected: {ExportDirectory}", folder.Path);
        }
#else
        DirectoryEntry.Text = await DisplayPromptAsync("Export directory", "Enter a writable directory path.", initialValue: DirectoryEntry.Text);
#endif
    }

    private void OnSelectAllCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (updatingSelection) return;
        updatingSelection = true;
        foreach (var entry in entries) entry.IsSelected = e.Value;
        updatingSelection = false;
        UpdateExportButton();
        UpdatePhotoExportButton();
    }

    private void OnEntryCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (updatingSelection) return;
        updatingSelection = true;
        SelectAllCheckBox.IsChecked = entries.Count > 0 && entries.All(entry => entry.IsSelected);
        updatingSelection = false;
        UpdateExportButton();
        UpdatePhotoExportButton();
    }

    private void OnSelectAllAlbumsCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (updatingSelection) return;
        updatingSelection = true;
        foreach (var album in albums) album.IsSelected = e.Value;
        updatingSelection = false;
        UpdatePhotoExportButton();
    }

    private void OnAlbumCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (updatingSelection) return;
        updatingSelection = true;
        SelectAllAlbumsCheckBox.IsChecked = albums.Count > 0 && albums.All(album => album.IsSelected);
        updatingSelection = false;
        UpdatePhotoExportButton();
    }

    private async Task RunOperationAsync(string operationName, Func<IProgress<OperationProgress>, CancellationToken, Task> operation)
    {
        Logger.Information("{OperationName} started", operationName);
        SetOperationControls(false);
        var progress = new Progress<OperationProgress>(UpdateProgress);
        try
        {
            await operation(progress, CancellationToken.None);
            Logger.Information("{OperationName} completed", operationName);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "{OperationName} failed", operationName);
            StatusLabel.Text = "Operation failed.";
            await DisplayAlertAsync("LiveJournal Export", exception.Message, "OK");
        }
        finally
        {
            SetOperationControls(true);
            UpdateExportButton();
            UpdatePhotoExportButton();
        }
    }

    private void UpdateProgress(OperationProgress progress)
    {
        StatusLabel.Text = progress.Total == 0 ? progress.Message : $"{progress.Message} {progress.Completed} of {progress.Total}";
        ActivityProgressBar.Progress = progress.Percentage;
    }

    private void SetOperationControls(bool isEnabled)
    {
        ScanButton.IsEnabled = isEnabled;
        ScanPhotosButton.IsEnabled = isEnabled;
        BrowseButton.IsEnabled = isEnabled;
        UsernameEntry.IsEnabled = isEnabled;
        PasswordEntry.IsEnabled = isEnabled;
        DirectoryEntry.IsEnabled = isEnabled;
        SelectAllCheckBox.IsEnabled = isEnabled;
        SelectAllAlbumsCheckBox.IsEnabled = isEnabled;
        JsonRadioButton.IsEnabled = isEnabled;
        XmlRadioButton.IsEnabled = isEnabled;
        EntriesView.IsEnabled = isEnabled;
        AlbumsView.IsEnabled = isEnabled;
    }

    private bool HasCredentials() => !string.IsNullOrWhiteSpace(UsernameEntry.Text) && !string.IsNullOrWhiteSpace(PasswordEntry.Text);

    private void UpdateExportButton() => ExportButton.IsEnabled = ScanButton.IsEnabled && entries.Any(entry => entry.IsSelected);

    private void UpdatePhotoExportButton() => ExportPhotosButton.IsEnabled = ScanButton.IsEnabled && albums.Any(album => album.IsSelected);
}