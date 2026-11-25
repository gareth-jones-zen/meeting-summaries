using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using M365Graph;

namespace MeetingSummaries;

/// <summary>Someone invited to a meeting, as the calendar has them.</summary>
public sealed record Attendee(string? Name, string? Email);

/// <summary>A meeting as the calendar has it: its subject and times, organiser, the people invited (rooms left out),
/// and its Teams chat (null if none could be found).</summary>
public sealed record MeetingInfo(string Subject, DateTimeOffset Start, DateTimeOffset End, Attendee Organiser,
    IReadOnlyList<Attendee> Attendees, string? ChatId)
{
    /// <summary>Everyone, organiser first, once each (by email, else name).</summary>
    public IReadOnlyList<Attendee> People =>
        new[] { Organiser }.Concat(Attendees)
            .GroupBy(a => (a.Email ?? a.Name ?? "").ToLowerInvariant())
            .Where(g => g.Key.Length > 0)
            .Select(g => g.First())
            .ToList();
}

/// <summary>Which meeting a transcript is of: its title and, from the recording's name, when the recording started
/// (null when only the date is known, as from a summary's frontmatter).</summary>
public sealed record MeetingRef(string Title, DateTimeOffset? Start, DateOnly Date);

/// <summary>Finds a meeting in the signed-in person's calendar. Only reads.</summary>
public interface IMeetingFinder
{
    MeetingInfo? Find(MeetingRef meeting);
}

/// <summary>The calendar through Microsoft Graph (delegated, reads only): the event whose subject is the meeting's
/// title (letters and digits compared, as Teams drops some punctuation from a recording's name) and whose time
/// contains the recording's start, the nearest if several; with only a date, the one event that day with that subject.
/// Its Teams chat comes from the join link (a "meetup-join" link carries the chat's id), else from the signed-in
/// person's meeting chats whose join link is the event's.</summary>
public sealed partial class GraphMeetingFinder(GraphClient graph, IReadOnlyList<string> scopes) : IMeetingFinder
{
    /// <summary>How far outside an event's times a recording may start (people start late, meetings overrun).</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(30);

    /// <summary>How many pages of 50 chats are looked through for a join link that doesn't carry the chat's id.</summary>
    public const int ChatPages = 10;

    [GeneratedRegex(@"/meetup-join/(19:[^/?#\s]+@thread\.(?:v2|skype|tacv2))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeetupJoin();

    /// <summary>The chat id in a Teams "meetup-join" link, or null (the short "teams.microsoft.com/meet/..." links
    /// don't carry it).</summary>
    public static string? ChatFromJoinUrl(string? joinUrl)
    {
        if (string.IsNullOrEmpty(joinUrl))
            return null;
        var match = MeetupJoin().Match(Uri.UnescapeDataString(joinUrl));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>A title compared by its letters and digits only, lower-cased.</summary>
    public static string Normalise(string title)
    {
        var text = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c))
                text.Append(char.ToLowerInvariant(c));
        return text.ToString();
    }

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Utc(JsonNode? node) =>
        (string?)node?["dateTime"] is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero)
            : null;

    private static Attendee Person(JsonNode? node) => new((string?)node?["emailAddress"]?["name"], (string?)node?["emailAddress"]?["address"]);

    public MeetingInfo? Find(MeetingRef meeting)
    {
        DateTimeOffset from, to;
        if (meeting.Start is { } start)
            (from, to) = (start.AddHours(-12), start.AddHours(2));
        else
        {
            var day = new DateTimeOffset(meeting.Date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(meeting.Date.ToDateTime(TimeOnly.MinValue)));
            (from, to) = (day, day.AddDays(1));
        }
        var url = $"/me/calendarView?startDateTime={Iso(from)}&endDateTime={Iso(to)}"
                  + "&$select=subject,start,end,organizer,attendees,onlineMeeting,isCancelled&$top=200";
        var headers = new Dictionary<string, string> { ["Prefer"] = "outlook.timezone=\"UTC\"" };
        var reply = graph.GetAsync(url, scopes, headers).GetAwaiter().GetResult();
        var wanted = Normalise(meeting.Title);
        var events = new List<(JsonNode Event, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var item in reply["value"] as JsonArray ?? [])
        {
            if (item is null || (bool?)item["isCancelled"] == true || Normalise((string?)item["subject"] ?? "") != wanted)
                continue;
            if (Utc(item["start"]) is not { } s || Utc(item["end"]) is not { } e)
                continue;
            if (meeting.Start is { } at && (at < s - Slack || at > e + Slack))
                continue;
            events.Add((item, s, e));
        }
        if (events.Count == 0 || (meeting.Start is null && events.Count > 1))
            return null;
        var (chosen, begins, ends) = meeting.Start is { } recorded
            ? events.OrderBy(x => (x.Start - recorded).Duration()).First()
            : events[0];
        var attendees = (chosen["attendees"] as JsonArray ?? [])
            .Where(a => !string.Equals((string?)a?["type"], "resource", StringComparison.OrdinalIgnoreCase))
            .Select(Person).ToList();
        var join = (string?)chosen["onlineMeeting"]?["joinUrl"];
        return new MeetingInfo((string?)chosen["subject"] ?? meeting.Title, begins, ends, Person(chosen["organizer"]), attendees,
            ChatFromJoinUrl(join) ?? ChatByJoinUrl(join));
    }

    /// <summary>The meeting chat whose join link is this one, among the signed-in person's most recent chats.</summary>
    private string? ChatByJoinUrl(string? joinUrl)
    {
        if (string.IsNullOrEmpty(joinUrl))
            return null;
        var wanted = Uri.UnescapeDataString(joinUrl);
        string? next = "/me/chats?$top=50";
        for (var page = 0; page < ChatPages && !string.IsNullOrEmpty(next); page++)
        {
            var reply = graph.GetAsync(next, scopes).GetAwaiter().GetResult();
            next = (string?)reply["@odata.nextLink"];
            foreach (var chat in reply["value"] as JsonArray ?? [])
                if ((string?)chat?["onlineMeetingInfo"]?["joinWebUrl"] is { } link
                    && string.Equals(Uri.UnescapeDataString(link), wanted, StringComparison.OrdinalIgnoreCase))
                    return (string?)chat?["id"];
        }
        return null;
    }
}
