using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using LJExport.Models;
using Serilog;

namespace LJExport.Services;

public sealed class ScrapbookClient(HttpClient httpClient)
{
    private const int MaxConcurrentRequests = 6;
    private static readonly ILogger Logger = Log.ForContext<ScrapbookClient>();
    private static readonly Regex AnchorRegex = new("<a\\b[^>]*?href=[\"'](?<url>[^\"']+)[\"'][^>]*>(?<content>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new("<img\\b[^>]*?src=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public async Task<IReadOnlyList<ScrapbookAlbum>> GetAlbumsAsync(
        LiveJournalSession session,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Logger.Information("Starting ScrapBook album scan for {Username}", session.Username);
        progress?.Report(new OperationProgress("Scanning ScrapBook albums…", 0, 0));
        var root = new Uri($"https://pics.livejournal.com/{Uri.EscapeDataString(session.Username)}/");
        var rootHtml = await GetPageAsync(root, session, cancellationToken);
        var albumLinks = ExtractAlbumLinks(rootHtml, root);
        Logger.Debug("ScrapBook root scan for {Username} found {AlbumLinkCount} album links", session.Username, albumLinks.Count);

        if (albumLinks.Count == 0)
        {
            Logger.Information("No ScrapBook album links found for {Username}; scanning the root as a single album", session.Username);
            albumLinks.Add(("ScrapBook", root));
        }

        var albums = new ConcurrentBag<ScrapbookAlbum>();
        var completed = 0;
        progress?.Report(new OperationProgress("Scanning ScrapBook albums…", 0, albumLinks.Count));

        await Parallel.ForEachAsync(albumLinks, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentRequests,
            CancellationToken = cancellationToken
        }, async (album, token) =>
        {
            Logger.Debug("Scanning ScrapBook album {AlbumName} at {AlbumUrl}", album.Name, album.Url);
            var html = await GetPageAsync(album.Url, session, token);
            var photos = ExtractPhotoLinks(html, album.Url);
            Logger.Debug("ScrapBook album {AlbumName} contains {PhotoCount} photos", album.Name, photos.Count);
            albums.Add(new ScrapbookAlbum
            {
                Name = album.Name,
                Url = album.Url,
                Photos = photos
            });
            progress?.Report(new OperationProgress("Scanning ScrapBook albums…", Interlocked.Increment(ref completed), albumLinks.Count));
        });

        var result = albums.OrderBy(album => album.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Logger.Information("ScrapBook album scan for {Username} completed with {AlbumCount} albums", session.Username, result.Count);
        return result;
    }

    public async Task ExportAlbumsAsync(
        IEnumerable<ScrapbookAlbum> albums,
        string exportDirectory,
        LiveJournalSession session,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selectedPhotos = albums
            .Where(album => album.IsSelected)
            .SelectMany(album => album.Photos.Select(photo => (Album: album, Photo: photo)))
            .ToList();
        Logger.Information("Starting ScrapBook photo export to {ExportDirectory} with {PhotoCount} selected photos", exportDirectory, selectedPhotos.Count);
        var completed = 0;
        progress?.Report(new OperationProgress("Exporting ScrapBook photos…", 0, selectedPhotos.Count));

        await Parallel.ForEachAsync(selectedPhotos, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentRequests,
            CancellationToken = cancellationToken
        }, async (item, token) =>
        {
            var albumDirectory = Path.Combine(exportDirectory, "Photos", "ScrapBook", SafeFileName(item.Album.Name));
            Directory.CreateDirectory(albumDirectory);
            Logger.Debug("Ensured ScrapBook export directory exists: {AlbumDirectory}", albumDirectory);
            await DownloadPhotoAsync(item.Photo, albumDirectory, session, token);
            var current = Interlocked.Increment(ref completed);
            Logger.Debug("Exported ScrapBook photo {PhotoUri}; completed {CompletedCount} of {TotalCount}", item.Photo.OriginalUri, current, selectedPhotos.Count);
            progress?.Report(new OperationProgress("Exporting ScrapBook photos…", current, selectedPhotos.Count));
        });

        Logger.Information("ScrapBook photo export to {ExportDirectory} completed", exportDirectory);
    }

    private async Task<string> GetPageAsync(Uri url, LiveJournalSession session, CancellationToken cancellationToken)
    {
        Logger.Debug("Requesting ScrapBook page {PageUrl}", url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddSessionCookie(request, session);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        Logger.Debug("ScrapBook page {PageUrl} returned HTTP {StatusCode}", url, (int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        Logger.Debug("ScrapBook page {PageUrl} returned {ContentLength} characters", url, html.Length);
        return html;
    }

    private async Task DownloadPhotoAsync(ScrapbookPhoto photo, string directory, LiveJournalSession session, CancellationToken cancellationToken)
    {
        Logger.Debug("Downloading ScrapBook photo {PhotoUri}", photo.OriginalUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, photo.OriginalUri);
        AddSessionCookie(request, session);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Logger.Debug("ScrapBook photo {PhotoUri} returned HTTP {StatusCode}", photo.OriginalUri, (int)response.StatusCode);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{photo.OriginalUri} did not return an image.");
        }

        var extension = Path.GetExtension(photo.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = mediaType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".img"
            };
        }

        var fileName = SafeFileName(Path.GetFileNameWithoutExtension(photo.FileName));
        var path = Path.Combine(directory, $"{fileName}{extension}");
        Logger.Debug("Writing ScrapBook photo {PhotoUri} to {DestinationPath}", photo.OriginalUri, path);
        await using var target = File.Create(path);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(target, cancellationToken);
        Logger.Information("Saved ScrapBook photo to {DestinationPath}", path);
    }

    private static List<(string Name, Uri Url)> ExtractAlbumLinks(string html, Uri baseUri)
    {
        return AnchorRegex.Matches(html)
            .Select(match => (Url: ToUri(match.Groups["url"].Value, baseUri), Name: DecodeText(match.Groups["content"].Value)))
            .Where(link => link.Url is not null && link.Url.AbsolutePath.Contains("album", StringComparison.OrdinalIgnoreCase))
            .Select(link => (string.IsNullOrWhiteSpace(link.Name) ? "Untitled album" : link.Name, link.Url!))
            .DistinctBy(link => link.Item2)
            .ToList();
    }

    private static IReadOnlyList<ScrapbookPhoto> ExtractPhotoLinks(string html, Uri baseUri)
    {
        var linkedImages = AnchorRegex.Matches(html)
            .Select(match => (Url: ToUri(match.Groups["url"].Value, baseUri), ContainsImage: ImageRegex.IsMatch(match.Groups["content"].Value)))
            .Where(link => link.ContainsImage && link.Url is not null)
            .Select(link => link.Url!);
        var displayedImages = ImageRegex.Matches(html)
            .Select(match => ToUri(match.Groups["url"].Value, baseUri))
            .Where(url => url is not null)
            .Select(url => url!);

        return linkedImages.Concat(displayedImages)
            .Where(IsLikelyPhoto)
            .Distinct()
            .Select(url => new ScrapbookPhoto(url, Path.GetFileName(url.LocalPath)))
            .ToList();
    }

    private static Uri? ToUri(string value, Uri baseUri) => Uri.TryCreate(baseUri, WebUtility.HtmlDecode(value), out var uri) ? uri : null;

    private static void AddSessionCookie(HttpRequestMessage request, LiveJournalSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"ljsession={session.SessionId}");
        }
    }

    private static bool IsLikelyPhoto(Uri uri) =>
        uri.Host.EndsWith("livejournal.com", StringComparison.OrdinalIgnoreCase) &&
        !uri.AbsolutePath.Contains("/userpic", StringComparison.OrdinalIgnoreCase) &&
        !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    private static string DecodeText(string html) => WebUtility.HtmlDecode(TagRegex.Replace(html, string.Empty)).Trim();

    private static string SafeFileName(string value)
    {
        var name = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(name) ? "untitled" : name[..Math.Min(name.Length, 80)];
    }
}
