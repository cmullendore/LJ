using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using LJExport.Models;
using Serilog;

namespace LJExport.Services;

public sealed class LiveJournalClient(HttpClient httpClient)
{
    private const string Endpoint = "https://www.livejournal.com/interface/flat";
    private const int MaxConcurrentRequests = 6;
    private static readonly ILogger Logger = Log.ForContext<LiveJournalClient>();

    public async Task<IReadOnlyList<JournalEntry>> GetAllEntriesAsync(
        string username,
        string password,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Logger.Information("Starting journal scan for {Username}", username);
        var session = await AuthenticateAsync(username, password, cancellationToken);
        progress?.Report(new OperationProgress("Scanning journal entry metadata…", 0, 0));

        var metadata = await SendAsync(new Dictionary<string, string>
        {
            ["mode"] = "syncitems",
            ["lastsync"] = "0"
        }, session, cancellationToken);

        var items = ParseItems(metadata);
        Logger.Information("Journal metadata scan for {Username} found {EntryCount} entries", username, items.Count);
        if (items.Count == 0)
        {
            Logger.Information("Journal scan for {Username} completed with no entries", username);
            return [];
        }

        var entries = new ConcurrentBag<JournalEntry>();
        var completed = 0;
        progress?.Report(new OperationProgress("Downloading journal entries…", 0, items.Count));

        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentRequests,
            CancellationToken = cancellationToken
        }, async (item, token) =>
        {
            Logger.Debug("Downloading journal entry {ItemId} for {Username}", item.ItemId, username);
            var response = await SendAsync(new Dictionary<string, string>
            {
                ["mode"] = "getevents",
                ["selecttype"] = "one",
                ["itemid"] = item.ItemId.ToString(CultureInfo.InvariantCulture)
            }, session, token);

            entries.Add(ParseEntry(response, item));
            var current = Interlocked.Increment(ref completed);
            Logger.Debug("Downloaded journal entry {ItemId} for {Username}; completed {CompletedCount} of {TotalCount}", item.ItemId, username, current, items.Count);
            progress?.Report(new OperationProgress("Downloading journal entries…", current, items.Count));
        });

        var result = entries.OrderByDescending(entry => entry.EventTime).ToList();
        Logger.Information("Journal scan for {Username} completed with {EntryCount} entries", username, result.Count);
        return result;
    }

    public async Task<LiveJournalSession> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        Logger.Information("Starting LiveJournal login for {Username}", username);
        Logger.Debug("Submitting LiveJournal login request for {Username} using clear authentication", username);

        Dictionary<string, string> response;
        try
        {
            response = await SendAsync(new Dictionary<string, string>
            {
                ["mode"] = "login",
                ["auth_method"] = "clear",
                ["user"] = username,
                ["password"] = password,
                ["ver"] = "1",
                ["clientversion"] = "LJExport/1.0"
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "LiveJournal login request failed for {Username}", username);
            throw;
        }

        response.TryGetValue("ljsession", out var session);
        if (string.IsNullOrWhiteSpace(session))
        {
            Logger.Information("LiveJournal login completed for {Username} without a session; subsequent Flat API requests will use clear authentication", username);
            return new LiveJournalSession(username, password, null);
        }

        Logger.Information("LiveJournal login completed for {Username}", username);
        return new LiveJournalSession(username, password, session);
    }

    private Task<Dictionary<string, string>> SendAsync(
        Dictionary<string, string> parameters,
        LiveJournalSession session,
        CancellationToken cancellationToken)
    {
        return SendAsync(WithAuthentication(parameters, session), cancellationToken);
    }

    private Dictionary<string, string> WithAuthentication(
        Dictionary<string, string> parameters,
        LiveJournalSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            parameters["auth_method"] = "session";
            parameters["ljsession"] = session.SessionId;
        }
        else
        {
            parameters["auth_method"] = "clear";
            parameters["user"] = session.Username;
            parameters["password"] = session.Password;
        }

        return parameters;
    }

    private async Task<Dictionary<string, string>> SendAsync(
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var mode = parameters.GetValueOrDefault("mode") ?? "unknown";
        Logger.Debug("Sending LiveJournal API request with mode {Mode}", mode);
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await httpClient.PostAsync(Endpoint, content, cancellationToken);
        Logger.Debug("LiveJournal API request with mode {Mode} returned HTTP {StatusCode}", mode, (int)response.StatusCode);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        Logger.Debug("LiveJournal API response for mode {Mode} contained {ResponseLength} characters", mode, responseContent.Length);
        var data = ParseFlatResponse(responseContent);
        Logger.Debug("Parsed LiveJournal API response for mode {Mode} into {ValueCount} values", mode, data.Count);
        if (data.TryGetValue("success", out var success) && success == "FAIL")
        {
            data.TryGetValue("errmsg", out var errorMessage);
            Logger.Warning("LiveJournal API request with mode {Mode} was rejected", mode);
            throw new InvalidOperationException(errorMessage ?? "LiveJournal rejected the request.");
        }

        return data;
    }

    private static Dictionary<string, string> ParseFlatResponse(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index + 1 < lines.Length; index += 2)
        {
            values[lines[index]] = WebUtility.UrlDecode(lines[index + 1]);
        }

        return values;
    }

    private static List<JournalEntry> ParseItems(IReadOnlyDictionary<string, string> data)
    {
        return data
            .Where(pair => pair.Key.EndsWith("_item", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value.StartsWith("L-", StringComparison.OrdinalIgnoreCase)
                            && long.TryParse(pair.Value.AsSpan(2), CultureInfo.InvariantCulture, out var itemId)
                ? new { Prefix = pair.Key[..^"item".Length], ItemId = itemId }
                : null)
            .Where(item => item is not null)
            .Select(item => new JournalEntry
            {
                ItemId = item!.ItemId,
                EventTime = TryGetEventTime(data, item.Prefix),
                Subject = string.Empty,
                Poster = string.Empty
            })
            .GroupBy(entry => entry.ItemId)
            .Select(group => group.First())
            .ToList();
    }

    private static JournalEntry ParseEntry(IReadOnlyDictionary<string, string> data, JournalEntry metadata)
    {
        var eventPair = data.FirstOrDefault(pair => pair.Key.EndsWith("event", StringComparison.OrdinalIgnoreCase));
        var prefix = eventPair.Key is null ? string.Empty : eventPair.Key[..^"event".Length];

        return new JournalEntry
        {
            ItemId = metadata.ItemId,
            EventTime = TryGetEventTime(data, prefix, metadata.EventTime),
            Subject = data.GetValueOrDefault($"{prefix}subject") ?? metadata.Subject,
            Body = eventPair.Value ?? string.Empty,
            Poster = data.GetValueOrDefault($"{prefix}poster") ?? metadata.Poster
        };
    }

    private static DateTimeOffset TryGetEventTime(
        IReadOnlyDictionary<string, string> data,
        string prefix,
        DateTimeOffset? fallback = null)
    {
        var eventTime = data.GetValueOrDefault($"{prefix}eventtime")
            ?? data.GetValueOrDefault($"{prefix}time");
        return DateTimeOffset.TryParse(eventTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : fallback ?? DateTimeOffset.MinValue;
    }
}
