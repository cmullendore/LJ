using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LJExport.Models;
using Serilog;

namespace LJExport.Services;

public sealed class JournalExportService(HttpClient httpClient)
{
    private const int MaxConcurrentWrites = 6;
    private static readonly Regex ImageRegex = new("<img\\b[^>]*?src=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly ILogger Logger = Log.ForContext<JournalExportService>();

    public async Task ExportAsync(IEnumerable<JournalEntry> entries, string directory, ExportFormat format, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var selectedEntries = entries.Where(entry => entry.IsSelected).ToList();
        Logger.Information("Starting {ExportFormat} journal export to {ExportDirectory} with {EntryCount} selected entries", format, directory, selectedEntries.Count);
        Directory.CreateDirectory(directory);
        Logger.Debug("Ensured journal export directory exists: {ExportDirectory}", directory);
        var completed = 0;
        progress?.Report(new OperationProgress("Exporting journal entries…", 0, selectedEntries.Count));

        await Parallel.ForEachAsync(selectedEntries, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentWrites, CancellationToken = cancellationToken }, async (entry, token) =>
        {
            var path = Path.Combine(directory, GetFileName(entry, format));
            Logger.Debug("Writing journal entry {ItemId} to {DestinationPath}", entry.ItemId, path);
            if (format == ExportFormat.Json)
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }), token);
            }
            else
            {
                var document = new XDocument(new XElement("journalEntry", new XElement("itemId", entry.ItemId), new XElement("eventTime", entry.EventTime), new XElement("subject", entry.Subject), new XElement("body", entry.Body), new XElement("poster", entry.Poster)));
                await File.WriteAllTextAsync(path, document.ToString(), token);
            }

            var current = Interlocked.Increment(ref completed);
            Logger.Information("Saved journal entry {ItemId} to {DestinationPath}; completed {CompletedCount} of {TotalCount}", entry.ItemId, path, current, selectedEntries.Count);
            progress?.Report(new OperationProgress("Exporting journal entries…", current, selectedEntries.Count));
        });

        Logger.Information("Journal export to {ExportDirectory} completed", directory);
    }

    public async Task ExportEmbeddedPhotosAsync(IEnumerable<JournalEntry> entries, string directory, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var photos = entries.Where(entry => entry.IsSelected).SelectMany(entry => ExtractImageUrls(entry.Body).Select((url, index) => (Entry: entry, Url: url, Index: index))).ToList();
        Logger.Information("Starting embedded journal photo export to {ExportDirectory} with {PhotoCount} photos", directory, photos.Count);
        var completed = 0;
        progress?.Report(new OperationProgress("Exporting embedded journal photos…", 0, photos.Count));

        await Parallel.ForEachAsync(photos, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentWrites, CancellationToken = cancellationToken }, async (photo, token) =>
        {
            var entryDirectory = Path.Combine(directory, "Photos", "Journal entries", GetEntryFolderName(photo.Entry));
            Directory.CreateDirectory(entryDirectory);
            Logger.Debug("Ensured journal photo export directory exists: {EntryDirectory}", entryDirectory);
            await DownloadImageAsync(photo.Url, entryDirectory, photo.Index, token);
            var current = Interlocked.Increment(ref completed);
            Logger.Debug("Exported embedded journal photo {PhotoUri}; completed {CompletedCount} of {TotalCount}", photo.Url, current, photos.Count);
            progress?.Report(new OperationProgress("Exporting embedded journal photos…", current, photos.Count));
        });

        Logger.Information("Embedded journal photo export to {ExportDirectory} completed", directory);
    }

    private async Task DownloadImageAsync(Uri url, string directory, int index, CancellationToken cancellationToken)
    {
        Logger.Debug("Downloading embedded journal photo {PhotoUri}", url);
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Logger.Debug("Embedded journal photo {PhotoUri} returned HTTP {StatusCode}", url, (int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{url} did not return an image.");
        }

        var extension = Path.GetExtension(url.LocalPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = mediaType.ToLowerInvariant() switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".img" };
        }

        var path = Path.Combine(directory, $"photo-{index + 1:D3}{extension}");
        Logger.Debug("Writing embedded journal photo {PhotoUri} to {DestinationPath}", url, path);
        await using var target = File.Create(path);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(target, cancellationToken);
        Logger.Information("Saved embedded journal photo to {DestinationPath}", path);
    }

    private static IReadOnlyList<Uri> ExtractImageUrls(string html) => ImageRegex.Matches(html).Select(match => Uri.TryCreate(System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value), UriKind.Absolute, out var url) ? url : null).Where(url => url is not null && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)).Select(url => url!).Distinct().ToList();

    private static string GetFileName(JournalEntry entry, ExportFormat format)
    {
        var date = entry.EventTime == DateTimeOffset.MinValue ? "unknown-date" : entry.EventTime.ToString("yyyy-MM-dd");
        var subject = string.Concat(entry.Subject.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        var description = string.IsNullOrWhiteSpace(subject) ? "untitled" : subject[..Math.Min(subject.Length, 80)];
        return $"{date}_{entry.ItemId}_{description}.{(format == ExportFormat.Json ? "json" : "xml")}";
    }

    private static string GetEntryFolderName(JournalEntry entry) => $"{(entry.EventTime == DateTimeOffset.MinValue ? "unknown-date" : entry.EventTime.ToString("yyyy-MM-dd"))}_{entry.ItemId}";
}