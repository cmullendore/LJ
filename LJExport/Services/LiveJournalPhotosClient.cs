using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LJExport.Models;
using Serilog;

namespace LJExport.Services;

public sealed class LiveJournalPhotosClient(HttpClient httpClient)
{
    private const string ApiEndpoint = "https://www.livejournal.com/__api/";
    private const int MaxConcurrentRequests = 6;
    private const int PageSize = 100;
    private static readonly ILogger Logger = Log.ForContext<LiveJournalPhotosClient>();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex AuthTokenRegex = new(@"""auth_token""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    public async Task<IReadOnlyList<LiveJournalPhotosAlbum>> GetAlbumsAsync(
        LiveJournalSession session,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Logger.Information("Starting LiveJournal Photos album scan for {Username}", session.Username);
        progress?.Report(new OperationProgress("Scanning LiveJournal Photos albums\u2026", 0, 0));

        var authToken = await GetAuthTokenAsync(session, cancellationToken);
        Logger.Debug("Obtained auth_token for {Username}", session.Username);

        var albumDtos = await CallApiAsync<GetAlbumsResult>(
            "photo.get_albums",
            new { user = session.Username, shouldViewAll = 1 },
            authToken,
            session,
            cancellationToken);

        var rawAlbums = albumDtos?.Albums ?? [];
        Logger.Debug("LiveJournal Photos API returned {AlbumCount} albums for {Username}", rawAlbums.Count, session.Username);

        if (rawAlbums.Count == 0)
        {
            Logger.Information("No LiveJournal Photos albums found for {Username}", session.Username);
            return [];
        }

        var albums = new ConcurrentBag<LiveJournalPhotosAlbum>();
        var completed = 0;
        progress?.Report(new OperationProgress("Scanning LiveJournal Photos albums\u2026", 0, rawAlbums.Count));

        await Parallel.ForEachAsync(rawAlbums, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentRequests,
            CancellationToken = cancellationToken
        }, async (albumDto, token) =>
        {
            var photos = await GetAlbumPhotosAsync(albumDto.Id, authToken, session, token);
            var albumUrl = new Uri($"https://{session.Username}.livejournal.com/photo/album/{albumDto.Id}/");
            albums.Add(new LiveJournalPhotosAlbum
            {
                Name = albumDto.Name,
                Url = albumUrl,
                Photos = photos
            });
            Logger.Debug("Scanned album {AlbumName} ({AlbumId}): {PhotoCount} photos", albumDto.Name, albumDto.Id, photos.Count);
            progress?.Report(new OperationProgress("Scanning LiveJournal Photos albums\u2026", Interlocked.Increment(ref completed), rawAlbums.Count));
        });

        var result = albums.OrderBy(album => album.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Logger.Information("LiveJournal Photos scan for {Username} completed: {AlbumCount} albums", session.Username, result.Count);
        return result;
    }

    public async Task ExportAlbumsAsync(
        IEnumerable<LiveJournalPhotosAlbum> albums,
        string exportDirectory,
        LiveJournalSession session,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selectedPhotos = albums
            .Where(album => album.IsSelected)
            .SelectMany(album => album.Photos.Select(photo => (Album: album, Photo: photo)))
            .ToList();
        Logger.Information("Starting LiveJournal Photos export to {ExportDirectory} with {PhotoCount} selected photos", exportDirectory, selectedPhotos.Count);
        var completed = 0;
        progress?.Report(new OperationProgress("Exporting LiveJournal Photos\u2026", 0, selectedPhotos.Count));

        await Parallel.ForEachAsync(selectedPhotos, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentRequests,
            CancellationToken = cancellationToken
        }, async (item, token) =>
        {
            var albumDirectory = Path.Combine(exportDirectory, "Photos", SafeFileName(item.Album.Name));
            Directory.CreateDirectory(albumDirectory);
            await DownloadPhotoAsync(item.Photo, albumDirectory, session, token);
            var current = Interlocked.Increment(ref completed);
            progress?.Report(new OperationProgress("Exporting LiveJournal Photos\u2026", current, selectedPhotos.Count));
        });

        Logger.Information("LiveJournal Photos export to {ExportDirectory} completed", exportDirectory);
    }

    private async Task<string> GetAuthTokenAsync(LiveJournalSession session, CancellationToken cancellationToken)
    {
        var pageUrl = new Uri($"https://{session.Username}.livejournal.com/photo/");
        Logger.Debug("Fetching auth_token from {PageUrl}", pageUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        AddSessionCookie(request, session);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var match = AuthTokenRegex.Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("Could not find auth_token in the LiveJournal Photos page. Ensure you are logged in.");
        }

        return match.Groups[1].Value;
    }

    private async Task<IReadOnlyList<LiveJournalPhoto>> GetAlbumPhotosAsync(
        long albumId,
        string authToken,
        LiveJournalSession session,
        CancellationToken cancellationToken)
    {
        var photos = new List<LiveJournalPhoto>();
        var offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await CallApiAsync<GetRecordsResult>(
                "photo.get_records",
                new { user = session.Username, albumId, offset, limit = PageSize, shouldViewAll = 1 },
                authToken,
                session,
                cancellationToken);

            var records = result?.Records ?? [];
            foreach (var record in records)
            {
                if (Uri.TryCreate(record.Url, UriKind.Absolute, out var uri))
                {
                    photos.Add(new LiveJournalPhoto(uri, record.Name));
                }
            }

            if (records.Count < PageSize)
            {
                break;
            }

            offset += records.Count;
        }

        return photos;
    }

    private async Task<T?> CallApiAsync<T>(
        string method,
        object parameters,
        string authToken,
        LiveJournalSession session,
        CancellationToken cancellationToken)
    {
        // LJ API requires a JSON array of batched calls; auth_token is merged into params
        var paramsWithAuth = MergeAuthToken(parameters, authToken);
        var requestItem = new
        {
            jsonrpc = "2.0",
            method,
            @params = paramsWithAuth,
            id = Environment.TickCount64
        };

        var body = JsonSerializer.Serialize(new[] { requestItem });
        Logger.Debug("Calling LiveJournal API method {Method}", method);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain")
        };
        AddSessionCookie(request, session);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        Logger.Debug("LiveJournal API method {Method} returned HTTP {StatusCode}", method, (int)response.StatusCode);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // Response is a JSON array; pick the first element matching our request
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var element = root.ValueKind == JsonValueKind.Array ? root[0] : root;

        if (element.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
        {
            var code = errorEl.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : 0;
            var message = errorEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown error";
            Logger.Warning("LiveJournal API method {Method} returned error {Code}: {Message}", method, code, message);
            throw new InvalidOperationException($"LiveJournal API error {code}: {message}");
        }

        if (!element.TryGetProperty("result", out var resultEl))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(resultEl.GetRawText(), JsonOptions);
    }

    private static Dictionary<string, object?> MergeAuthToken(object parameters, string authToken)
    {
        var json = JsonSerializer.Serialize(parameters);
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
        dict["auth_token"] = authToken;
        return dict;
    }

    private async Task DownloadPhotoAsync(LiveJournalPhoto photo, string directory, LiveJournalSession session, CancellationToken cancellationToken)
    {
        Logger.Debug("Downloading photo {PhotoUri}", photo.OriginalUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, photo.OriginalUri);
        AddSessionCookie(request, session);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Logger.Debug("Photo {PhotoUri} returned HTTP {StatusCode}", photo.OriginalUri, (int)response.StatusCode);
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

        // Use the unique filename from the URI to avoid collisions when multiple photos share the same FileName
        // (e.g. several photos named "no title" downloaded in parallel into the same directory).
        var uniqueBaseName = SafeFileName(Path.GetFileNameWithoutExtension(photo.OriginalUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(uniqueBaseName))
        {
            uniqueBaseName = SafeFileName(Path.GetFileNameWithoutExtension(photo.FileName));
        }
        var path = Path.Combine(directory, $"{uniqueBaseName}{extension}");
        Logger.Debug("Writing photo {PhotoUri} to {DestinationPath}", photo.OriginalUri, path);
        await using var target = File.Create(path);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(target, cancellationToken);
        Logger.Information("Saved photo to {DestinationPath}", path);
    }

    private static void AddSessionCookie(HttpRequestMessage request, LiveJournalSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"ljsession={session.SessionId}");
        }
    }

    private static string SafeFileName(string value)
    {
        var name = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(name) ? "untitled" : name[..Math.Min(name.Length, 80)];
    }

    private sealed class GetAlbumsResult
    {
        public IReadOnlyList<AlbumDto> Albums { get; init; } = [];
    }

    private sealed class AlbumDto
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class GetRecordsResult
    {
        public IReadOnlyList<RecordDto> Records { get; init; } = [];
    }

    private sealed class RecordDto
    {
        public long Id { get; init; }
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;
    }
}
