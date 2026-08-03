using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LJExport.Models;

namespace LJExport.Services;

public sealed class JournalExportService(HttpClient httpClient)
{
    private const int MaxConcurrentWrites = 6;
    private static readonly Regex ImageRegex = new("<img\\b[^>]*?src=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task ExportAsync(IEnumerable<JournalEntry> entries, string directory, ExportFormat format, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var selectedEntries = entries.Where(entry => entry.IsSelected).ToList();
        Directory.CreateDirectory(directory);
        var completed = 0;
        progress?.Report(new OperationProgress("Exporting journal entries…", 0, selectedEntries.Count));

        await Parallel.ForEachAsync(selectedEntries, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentWrites, CancellationToken = cancellationToken }, async (entry, token) =>
        {
            var path = Path.Combine(directory, GetFileName(entry, format));
            if (format == ExportFormat.Json)
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }), token);
            }
            else
            {
                var document = new XDocument(new XElement("journalEntry", new XElement("itemId", entry.ItemId), new XElement("eventTime", entry.EventTime), new XElement("subject", entry.Subject), new XElement("body", entry.Body), new XElement("poster", entry.Poster)));
                await File.WriteAllTextAsync(path, document.ToString(), token);
            }

            progress?.Report(new OperationProgress("Exporting journal entries…", Interlocked.Increment(ref completed), selectedEntries.Count));
        });
    }

    public async Task ExportEmbeddedPhotosAsync(IEnumerable<JournalEntry> entries, string directory, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var photos = entries.Where(entry => entry.IsSelected).SelectMany(entry => ExtractImageUrls(entry.Body).Select((url, index) => (Entry: entry, Url: url, Index: index))).ToList();
        var completed = 0;
        progress?.Report(new OperationProgress("Exporting embedded journal photos…", 0, photos.Count));

        await Parallel.ForEachAsync(photos, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentWrites, CancellationToken = cancellationToken }, async (photo, token) =>
        {
            var entryDirectory = Path.Combine(directory, "Photos", "Journal entries", GetEntryFolderName(photo.Entry));
            Directory.CreateDirectory(entryDirectory);
            await DownloadImageAsync(photo.Url, entryDirectory, photo.Index, token);
            progress?.Report(new OperationProgress("Exporting embedded journal photos…", Interlocked.Increment(ref completed), photos.Count));
        });
    }

    private async Task DownloadImageAsync(Uri url, string directory, int index, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        await using var target = File.Create(path);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(target, cancellationToken);
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