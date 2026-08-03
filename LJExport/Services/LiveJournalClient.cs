using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using LJExport.Models;

namespace LJExport.Services;

public sealed class LiveJournalClient(HttpClient httpClient)
{
    private const string Endpoint = "https://www.livejournal.com/interface/flat";
    private const int MaxConcurrentRequests = 6;

    public async Task<IReadOnlyList<JournalEntry>> GetAllEntriesAsync(
        string username,
        string password,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var session = await AuthenticateAsync(username, password, cancellationToken);
        progress?.Report(new OperationProgress("Scanning journal entry metadata…", 0, 0));

        var metadata = await SendAsync(new Dictionary<string, string>
        {
            ["mode"] = "syncitems",
            ["lastsync"] = "0",
            ["auth_method"] = "session",
            ["ljsession"] = session.SessionId
        }, cancellationToken);

        var items = ParseItems(metadata);
        if (items.Count == 0)
        {
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
            var response = await SendAsync(new Dictionary<string, string>
            {
                ["mode"] = "getevents",
                ["selecttype"] = "one",
                ["itemid"] = item.ItemId.ToString(CultureInfo.InvariantCulture),
                ["auth_method"] = "session",
                ["ljsession"] = session.SessionId
            }, token);

            entries.Add(ParseEntry(response, item));
            var current = Interlocked.Increment(ref completed);
            progress?.Report(new OperationProgress("Downloading journal entries…", current, items.Count));
        });

        return entries.OrderByDescending(entry => entry.EventTime).ToList();
    }

    public async Task<LiveJournalSession> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(new Dictionary<string, string>
        {
            ["mode"] = "login",
            ["auth_method"] = "clear",
            ["user"] = username,
            ["password"] = password,
            ["ver"] = "1",
            ["clientversion"] = "LJExport/1.0"
        }, cancellationToken);

        if (!response.TryGetValue("ljsession", out var session) || string.IsNullOrWhiteSpace(session))
        {
            throw new InvalidOperationException("LiveJournal did not return a session. Check the account credentials.");
        }

        return new LiveJournalSession(username, session);
    }

    private async Task<Dictionary<string, string>> SendAsync(
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await httpClient.PostAsync(Endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var data = ParseFlatResponse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (data.TryGetValue("success", out var success) && success == "FAIL")
        {
            data.TryGetValue("errmsg", out var errorMessage);
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
            .Where(pair => pair.Key.EndsWith("itemid", StringComparison.OrdinalIgnoreCase))
            .Select(pair => long.TryParse(pair.Value, CultureInfo.InvariantCulture, out var itemId)
                ? new { Prefix = pair.Key[..^"itemid".Length], ItemId = itemId }
                : null)
            .Where(item => item is not null)
            .Select(item => new JournalEntry
            {
                ItemId = item!.ItemId,
                EventTime = TryGetEventTime(data, item.Prefix),
                Subject = data.GetValueOrDefault($"{item.Prefix}subject") ?? string.Empty,
                Poster = data.GetValueOrDefault($"{item.Prefix}poster") ?? string.Empty
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
        var eventTime = data.GetValueOrDefault($"{prefix}eventtime");
        return DateTimeOffset.TryParse(eventTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : fallback ?? DateTimeOffset.MinValue;
    }
}
