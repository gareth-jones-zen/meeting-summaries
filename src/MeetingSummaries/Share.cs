using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn.Model;
using WinBackground;

namespace MeetingSummaries;

/// <summary>A [[share]] rule: which meetings' transcript and summary are posted to the meeting's Teams chat.</summary>
public sealed record ShareRule(Regex? Series, Regex? Title, Regex? Organiser, bool Transcript, bool Summary, bool KeyPoints,
    string File, bool External, string Why)
{
    /// <summary>Whether the rule names this meeting, before the calendar is read (the organiser is checked after).</summary>
    public bool Names(string series, string? title) =>
        (Series is null || Series.IsMatch(series)) && (Title is null || (title is not null && Title.IsMatch(title)));

    public bool OrganisedBy(Attendee? organiser) =>
        Organiser is null || (organiser is not null && (Organiser.IsMatch(organiser.Name ?? "") || Organiser.IsMatch(organiser.Email ?? "")));
}

/// <summary>The settings for posting a meeting's transcript and summary to its chat (1.5.0). With no [[share]] rule
/// nothing is ever posted, and nothing signs in.</summary>
public sealed record ShareSettings
{
    public static readonly ShareSettings None = new();

    public IReadOnlyList<ShareRule> Rules { get; init; } = [];

    /// <summary>never_terms: regular expressions (case ignored) that hold back anything containing them.</summary>
    public IReadOnlyList<Regex> NeverTerms { get; init; } = [];

    /// <summary>share_name: whose assistant wrote the summary, and who checked it, in its footer.</summary>
    public string? Name { get; init; }

    /// <summary>org_domains: email domains inside the organisation; an attendee at any other is external.</summary>
    public IReadOnlyList<string> OrgDomains { get; init; } = [];

    /// <summary>teams_post: the command that posts (teams-post and its --config), run from the settings file's folder.</summary>
    public IReadOnlyList<string> TeamsPost { get; init; } = [];

    /// <summary>share_state: what was posted, by transcript (JSON).</summary>
    public string StateFile { get; init; } = "";

    /// <summary>share_folder: the message files posted (each meeting's, so teams-post can edit one later).</summary>
    public string Folder { get; init; } = "";

    /// <summary>Signing in to read the calendar (and, for a short join link, the meeting chats).</summary>
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public IReadOnlyList<string> GraphScopes { get; init; } = ["Calendars.Read", "Chat.Read"];

    public static readonly string[] RuleKeys = ["series", "title", "organiser", "transcript", "summary", "key_points", "file", "external", "why"];

    /// <summary>Read the sharing settings out of the raw settings. Throws ConfigError.</summary>
    internal static ShareSettings Read(TomlTable raw, string full, Func<string, string> path, Func<string, string?> env, string owner,
        string localFolder)
    {
        string? Text(string key) => raw.TryGetValue(key, out var v) ? v as string ?? throw new ConfigError($"{full}: {key} should be text") : null;
        List<string> Strings(string key) => raw.TryGetValue(key, out var v)
            ? v is TomlArray a && a.All(x => x is string) ? a.Cast<string>().ToList() : throw new ConfigError($"{full}: {key} should be a list of text")
            : [];
        Regex Pattern(string where, string text)
        {
            try
            {
                return new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException error)
            {
                throw new ConfigError($"{full}: {where} isn't a valid regular expression ({error.Message})");
            }
        }

        var rules = new List<ShareRule>();
        if (raw.TryGetValue("share", out var s))
        {
            if (s is not TomlTableArray tables)
                throw new ConfigError($"{full}: share should be [[share]] tables: {string.Join(", ", RuleKeys)}");
            var n = 0;
            foreach (var rule in tables)
            {
                n++;
                foreach (var key in rule.Keys)
                    if (!RuleKeys.Contains(key))
                        throw new ConfigError($"{full}: [[share]] {n} has an unknown setting '{key}'");
                string? RuleText(string key) => rule.TryGetValue(key, out var v) ? v as string ?? throw new ConfigError($"{full}: [[share]] {n}: {key} should be text") : null;
                bool Flag(string key, bool fallback) => !rule.TryGetValue(key, out var v) ? fallback
                    : v is bool b ? b : throw new ConfigError($"{full}: [[share]] {n}: {key} should be true or false");
                var series = RuleText("series");
                var title = RuleText("title");
                if (string.IsNullOrEmpty(series) && string.IsNullOrEmpty(title))
                    throw new ConfigError($"{full}: [[share]] {n} needs series or title (a regular expression), to say which meetings");
                var why = RuleText("why");
                if (string.IsNullOrWhiteSpace(why))
                    throw new ConfigError($"{full}: [[share]] {n} needs a why: the reason these meetings are posted");
                var file = RuleText("file") ?? "markdown";
                if (file is not ("markdown" or "docx"))
                    throw new ConfigError($"{full}: [[share]] {n}: file should be \"markdown\" or \"docx\", not \"{file}\"");
                var organiser = RuleText("organiser");
                rules.Add(new ShareRule(
                    string.IsNullOrEmpty(series) ? null : Pattern($"[[share]] {n} series", series),
                    string.IsNullOrEmpty(title) ? null : Pattern($"[[share]] {n} title", title),
                    string.IsNullOrEmpty(organiser) ? null : Pattern($"[[share]] {n} organiser", organiser),
                    Flag("transcript", false), Flag("summary", false), Flag("key_points", false), file, Flag("external", false), why));
            }
        }
        var terms = Strings("never_terms").Select((t, i) => Pattern($"never_terms[{i}]", t)).ToList();
        var teamsPost = Strings("teams_post").Select(a => PyPath.ExpandVars(a, env)).ToList();
        if (rules.Count > 0 && teamsPost.Count == 0)
            throw new ConfigError($"{full}: [[share]] needs teams_post, the command that posts (teams-post.exe and its --config)");
        return new ShareSettings
        {
            Rules = rules,
            NeverTerms = terms,
            Name = Text("share_name") ?? owner,
            OrgDomains = Strings("org_domains").Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).ToList(),
            TeamsPost = teamsPost,
            StateFile = Text("share_state") is { Length: > 0 } state ? path(state) : Path.Combine(localFolder, "shared.json"),
            Folder = Text("share_folder") is { Length: > 0 } folder ? path(folder) : Path.Combine(localFolder, "share"),
            LoginHint = Text("login_hint"),
            Tenant = Text("tenant") ?? "organizations",
            MsalCache = Text("msal_cache") is { Length: > 0 } cache ? path(cache) : null,
            GraphScopes = raw.ContainsKey("graph_scopes") ? Strings("graph_scopes") : ["Calendars.Read", "Chat.Read"],
        };
    }
}

/// <summary>One post made: which transcript (relative to the workspace), what ("transcript" or "summary"), where and when.</summary>
public sealed record SharedRecord(string Transcript, string Kind, string Chat, string At);

/// <summary>Posting a meeting's transcript and summary to its Teams chat, through teams-post (1.5.0).
///
/// - **Nothing is posted unless a [[share]] rule opts the meeting in**: transcript = true posts the transcript as soon as
///   it's filed (the hook), with no one asked: the rule is the owner's yes. summary = true lets `share summary` post the
///   finalised summary's attendee version, previewed first.
/// - **Hard stops hold a post back whatever a rule says**, and raise it (a "share/held" item): a 1:1; a file under
///   team/ or me/; a never_terms match; for a summary, a data-sensitivity line keeping it out of chats, broadcasts or
///   pre-reads, a draft, or unanswered owner items; external attendees (unless external = true); no chat found; and,
///   for a transcript, Files.ReadWrite not granted (teams-post's exit 3).
/// - **Never twice:** each post is recorded in the state file and as a `shared:` line in the summary's frontmatter
///   (carried in when the summary is drafted later), and teams-post refuses a file twice in a chat.</summary>
public static partial class Share
{
    public const string HeldType = "share/held";
    public const string FailedType = "share/failed";

    [GeneratedRegex(@"^\*\*(?<title>.+?)-(?<date>\d{8})_(?<time>\d{6})(?<utc>UTC)?-Meeting (?:Recording|Transcript)\*\*[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    [GeneratedRegex(@"\b(?:1[-:]2[-:]1|1:1|one[- ]to[- ]one)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneToOneTitle();

    [GeneratedRegex(@"^data-sensitivity:.*\b(?:chats?|broadcasts?|pre-?reads?)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex KeepOutOfChats();

    [GeneratedRegex(@"^status:[ \t]*[""']?draft\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DraftStatus();

    [GeneratedRegex(@"^## (?<name>.+?)[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^shared:[ \t]*\[(?<items>[^\]\n]*)\][ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SharedLine();

    [GeneratedRegex(@"^[ \t]*- \*\*\[(?:Noted|Check)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ReviewBullet();

    /// <summary>The sections an attendee version keeps, in the summary's order; Key points only if the rule says.</summary>
    public static readonly string[] AttendeeSections = ["purpose", "key points", "decisions", "actions", "open questions"];

    // ---------------------------------------------------------------- the meeting

    /// <summary>The meeting a transcript is of, from its first line (the recording's name, as Teams writes it:
    /// "**Title-20261125_143017UTC-Meeting Recording**"); null if it doesn't start that way. A time without "UTC" is
    /// local.</summary>
    public static MeetingRef? Identify(string transcriptText)
    {
        var first = PyStr.SplitLines(transcriptText).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        var match = first is null ? null : Header().Match(first);
        if (match is not { Success: true })
            return null;
        var at = DateTime.ParseExact(match.Groups["date"].Value + match.Groups["time"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var start = match.Groups["utc"].Success
            ? new DateTimeOffset(at, TimeSpan.Zero)
            : new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at));
        return new MeetingRef(match.Groups["title"].Value.Trim(), start, DateOnly.FromDateTime(at));
    }

    /// <summary>The series a transcript belongs to: the name of the folder it's filed in.</summary>
    public static string SeriesOf(string transcript) => Paths.Name(Paths.Parent(transcript));

    private static IFormatProvider En => CultureInfo.InvariantCulture;

    private static string Day(DateOnly date) => date.ToString("d MMM yyyy", En);

    // ---------------------------------------------------------------- hard stops

    /// <summary>Why a post must be held back, whatever the rule says (empty: it may go). text is what would be posted
    /// (the transcript, or the summary file); paths are the files involved, relative to the workspace.</summary>
    public static List<string> HardStops(Config config, ShareRule rule, IEnumerable<string> paths, string text, MeetingRef? meeting,
        MeetingInfo? found, string? summary = null)
    {
        var reasons = new List<string>();
        var sharing = config.Sharing;
        foreach (var rel in paths)
        {
            var top = rel.Split('/')[0];
            if (top is "team" or "me")
                reasons.Add($"{rel} is under {top}/, which holds personnel records");
        }
        var title = found?.Subject ?? meeting?.Title ?? "";
        if (OneToOneTitle().IsMatch(title) || (found is not null && found.People.Count <= 2))
            reasons.Add("it's a 1:1");
        for (var i = 0; i < sharing.NeverTerms.Count; i++)
            if (sharing.NeverTerms[i].IsMatch(text) || (summary is not null && sharing.NeverTerms[i].IsMatch(summary)))
                reasons.Add($"a need-to-know term appears in it (never_terms[{i}])");
        if (summary is not null)
        {
            if (KeepOutOfChats().IsMatch(Frontmatter(summary)))
                reasons.Add("its data-sensitivity line keeps it out of chats, broadcasts or pre-reads");
            if (DraftStatus().IsMatch(Frontmatter(summary)) || summary.Contains("[DRAFT]", StringComparison.Ordinal))
                reasons.Add("it's still a draft (status: draft or [DRAFT])");
            if (summary.Contains($"**[{config.Owner}]", StringComparison.Ordinal))
                reasons.Add($"it still has [{config.Owner}] questions to answer");
        }
        if (found is null)
            reasons.Add(meeting is null
                ? "which meeting it is isn't known (the transcript doesn't start with the recording's name)"
                : $"\"{meeting.Title}\" on {Day(meeting.Date)} isn't in the calendar");
        else
        {
            if (!rule.External)
            {
                if (sharing.OrgDomains.Count == 0)
                    reasons.Add("org_domains isn't set, so external attendees can't be ruled out");
                else
                {
                    var outside = found.People.Where(p => !sharing.OrgDomains.Contains(DomainOf(p.Email))).ToList();
                    if (outside.Count > 0)
                        reasons.Add($"{outside.Count} attendee{(outside.Count == 1 ? " is" : "s are")} external (the rule doesn't say external = true)");
                }
            }
            if (found.ChatId is null)
                reasons.Add("the meeting's Teams chat couldn't be found");
        }
        return reasons;
    }

    private static string DomainOf(string? email) =>
        email is not null && email.LastIndexOf('@') is var at and >= 0 ? email[(at + 1)..].Trim().ToLowerInvariant() : "";

    // ---------------------------------------------------------------- the attendee version

    /// <summary>The summary as attendees get it, copied from the finalised summary and never rewritten: its Purpose,
    /// (Key points if keyPoints), Decisions, Actions and Open questions sections as they are, in its order, [Noted] and
    /// [Check] bullets left out; then the footer.</summary>
    public static string AttendeeVersion(string summary, bool keyPoints, string meeting, DateOnly date, string by)
    {
        var body = summary.Replace("\r\n", "\n");
        body = body[Frontmatter(body).Length..];
        var headings = SectionHeading().Matches(body).ToList();
        var output = new StringBuilder();
        for (var i = 0; i < headings.Count; i++)
        {
            var name = headings[i].Groups["name"].Value.Trim();
            var key = name.ToLowerInvariant();
            if (!AttendeeSections.Contains(key) || (key == "key points" && !keyPoints))
                continue;
            var from = headings[i].Index + headings[i].Length;
            var to = i + 1 < headings.Count ? headings[i + 1].Index : body.Length;
            var lines = body[from..to].Split('\n').Where(l => !ReviewBullet().IsMatch(l));
            var content = string.Join("\n", lines).Trim('\n');
            if (content.Trim().Length == 0)
                continue;
            output.Append("## ").Append(name).Append("\n\n").Append(content.TrimEnd()).Append("\n\n");
        }
        output.Append($"_Summary of {meeting}, {Day(date)}, by {by}'s assistant, checked by {by}._\n");
        return output.ToString();
    }

    // ---------------------------------------------------------------- the record: frontmatter and state

    /// <summary>The frontmatter block (with its --- lines and the newline after), or "" if there isn't one.</summary>
    public static string Frontmatter(string text)
    {
        var body = text.Replace("\r\n", "\n");
        if (!body.StartsWith("---\n", StringComparison.Ordinal))
            return "";
        var end = body.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return "";
        var close = body.IndexOf('\n', end + 4);
        return close < 0 ? body : body[..(close + 1)];
    }

    /// <summary>The shared: entries in a summary's frontmatter ("transcript 2026-11-25 10:42", ...).</summary>
    public static List<string> SharedEntries(string summary)
    {
        var match = SharedLine().Match(Frontmatter(summary));
        return match.Success
            ? match.Groups["items"].Value.Split(',').Select(e => e.Trim()).Where(e => e.Length > 0).ToList()
            : [];
    }

    /// <summary>The summary with an entry added to its frontmatter's shared: line (made if missing); unchanged if it
    /// has no frontmatter or already has the entry.</summary>
    public static string WithShared(string summary, string entry)
    {
        var text = summary.Replace("\r\n", "\n");
        var front = Frontmatter(text);
        if (front.Length == 0 || SharedEntries(text).Contains(entry))
            return text;
        var line = SharedLine().Match(front);
        var updated = line.Success
            ? front[..line.Index] + $"shared: [{string.Join(", ", SharedEntries(text).Append(entry))}]" + front[(line.Index + line.Length)..]
            : front[..front.LastIndexOf("---", StringComparison.Ordinal)] + $"shared: [{entry}]\n---" + front[(front.LastIndexOf("---", StringComparison.Ordinal) + 3)..];
        return updated + text[front.Length..];
    }

    public static string Entry(string kind, DateTime at) => $"{kind} {at.ToString("yyyy-MM-dd HH:mm", En)}";

    public static List<SharedRecord> LoadState(Config config)
    {
        var path = config.Sharing.StateFile;
        if (!File.Exists(path))
            return [];
        try
        {
            return (JsonNode.Parse(File.ReadAllBytes(path)) as JsonArray ?? [])
                .Select(n => new SharedRecord((string?)n?["transcript"] ?? "", (string?)n?["kind"] ?? "", (string?)n?["chat"] ?? "", (string?)n?["at"] ?? ""))
                .ToList();
        }
        catch (JsonException error)
        {
            throw new IOException($"{path} isn't readable JSON ({error.Message}); it records what was posted, so fix or move it.", error);
        }
    }

    private static void AddState(Config config, SharedRecord record)
    {
        var records = LoadState(config);
        records.Add(record);
        var array = new JsonArray(records.Select(r => (JsonNode?)new JsonObject
        {
            ["transcript"] = r.Transcript, ["kind"] = r.Kind, ["chat"] = r.Chat, ["at"] = r.At,
        }).ToArray());
        var path = config.Sharing.StateFile;
        Directory.CreateDirectory(Paths.Parent(path));
        File.WriteAllText(path + ".tmp", array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>A summary being drafted gets the shared: entries of the transcripts already posted.</summary>
    public static string CarryInto(Config config, string summary, IEnumerable<string> relTranscripts)
    {
        if (config.Sharing.Rules.Count == 0 && !File.Exists(config.Sharing.StateFile))
            return summary;
        var wanted = relTranscripts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var record in LoadState(config).Where(r => wanted.Contains(r.Transcript)))
            summary = WithShared(summary, $"{record.Kind} {record.At}");
        return summary;
    }

    // ---------------------------------------------------------------- raising what's held

    private static void Raise(Config config, SummariesHost host, Action<string> log, string type, string rel, string title, string kind,
        IReadOnlyList<string> reasons)
    {
        log($"{rel}: {kind} {(type == HeldType ? "held" : "not posted")}: {string.Join("; ", reasons)}");
        if (config.Review is { } review)
        {
            try
            {
                review.Open().Add(review.Source, type, new JsonObject
                {
                    ["arrived"] = host.Now().ToString("yyyy-MM-ddTHH:mm:ss", En),
                    ["path"] = rel,
                    ["title"] = title,
                    ["kind"] = kind,
                    ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                });
                if (type == HeldType)
                    return;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                log($"  review queue failed: {error.Message}");
            }
        }
        host.Notify(type == HeldType ? "A meeting's transcript was held back" : "A meeting's transcript wasn't posted",
            [title, reasons[0], $"Log: {config.LogFile}"]);
    }

    private static string Tail(Completed done) =>
        PyStr.Tail(PyStr.Strip(done.StandardError + "\n" + done.StandardOutput), 400);

    /// <summary>A name OneDrive accepts: the characters it refuses become "-".</summary>
    public static string SafeName(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name)
            text.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c);
        return text.ToString().Trim().TrimEnd('.');
    }

    private static string MessageFile(Config config, string transcript, string kind) =>
        Path.Combine(config.Sharing.Folder, SafeName($"{SeriesOf(transcript)}-{Paths.Stem(transcript)}-{kind}.md"));

    private static IMeetingFinder? Finder(Config config, SummariesHost host, Action<string> log)
    {
        try
        {
            return host.Meetings(config);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"  the calendar can't be read: {error.Message}");
            return null;
        }
    }

    private static MeetingInfo? Lookup(IMeetingFinder? finder, MeetingRef? meeting, Action<string> log)
    {
        if (finder is null || meeting is null)
            return null;
        try
        {
            return finder.Find(meeting);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"  the calendar couldn't be read: {error.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------- the transcript, from the hook

    /// <summary>The hook's step: each transcript just filed whose meeting a rule opts in (transcript = true) is posted
    /// to the meeting's chat with the file attached, unless a hard stop holds it back (raised as share/held). No rule:
    /// nothing happens, and nothing signs in.</summary>
    public static void Transcripts(Config config, IReadOnlyList<string> files, Action<string> log, SummariesHost host)
    {
        var rules = config.Sharing.Rules.Where(r => r.Transcript).ToList();
        if (rules.Count == 0)
            return;
        IMeetingFinder? finder = null;
        var finderMade = false;
        foreach (var file in files.Where(File.Exists))
        {
            string rel;
            try
            {
                rel = config.Relative(file);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if ($"/{rel}".Contains($"/{config.Summaries}/", StringComparison.Ordinal) || PyStr.Lower(Paths.Suffix(file)) is not (".md" or ".txt"))
                continue;
            var text = PyStr.ReadText(file);
            var meeting = Identify(text);
            var series = SeriesOf(file);
            var candidates = rules.Where(r => r.Names(series, meeting?.Title)).ToList();
            if (candidates.Count == 0)
                continue;
            if (LoadState(config).Any(r => r.Kind == "transcript" && string.Equals(r.Transcript, rel, StringComparison.OrdinalIgnoreCase)))
            {
                log($"{rel}: transcript already posted");
                continue;
            }
            if (!finderMade)
            {
                finder = Finder(config, host, log);
                finderMade = true;
            }
            var found = Lookup(finder, meeting, log);
            var rule = found is null ? candidates[0] : candidates.FirstOrDefault(r => r.OrganisedBy(found.Organiser));
            if (rule is null)
                continue;   // the organiser isn't the rule's
            var title = found?.Subject ?? meeting?.Title ?? Paths.Stem(file);
            var date = meeting?.Date ?? DateOnly.FromDateTime(host.Now());
            var reasons = HardStops(config, rule, [rel], text, meeting, found);
            var attach = file;
            if (rule.File == "docx")
            {
                attach = Path.Combine(Paths.Parent(file), Paths.Stem(file) + ".docx");
                if (!File.Exists(attach))
                    reasons.Add("the rule says file = \"docx\", but there's no .docx beside the transcript");
            }
            if (reasons.Count > 0)
            {
                Raise(config, host, log, HeldType, rel, title, "transcript", reasons);
                continue;
            }

            var message = MessageFile(config, file, "transcript");
            Directory.CreateDirectory(Paths.Parent(message));
            PyStr.WriteText(message, $"Transcript of {title}, {Day(date)}: speech-to-text, so names and terms may be wrong\n");
            var name = SafeName($"{title} {date.ToString("yyyy-MM-dd", En)} transcript{Paths.Suffix(attach)}");
            List<string> argv = [.. config.Sharing.TeamsPost, "post", "--chat", found!.ChatId!, message, "--attach", attach, "--name", name, "--yes"];
            if (rule.External)
                argv.Add("--external");
            Completed done;
            try
            {
                done = host.RunTool(argv, Paths.Parent(config.Path));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Raise(config, host, log, FailedType, rel, title, "transcript", [$"teams-post couldn't be run: {error.Message}"]);
                continue;
            }
            if (done.ExitCode == 3)
            {
                Raise(config, host, log, HeldType, rel, title, "transcript",
                    ["ready to post, but attaching it needs Files.ReadWrite, which the sign-in doesn't hold yet (teams-post sign-in --files)"]);
                continue;
            }
            if (done.ExitCode != 0)
            {
                Raise(config, host, log, FailedType, rel, title, "transcript", [$"teams-post exited {done.ExitCode}: {Tail(done)}"]);
                continue;
            }
            Recorded(config, file, rel, "transcript", found.ChatId!, host.Now(), log);
            log($"{rel}: transcript posted to the meeting's chat ({rule.Why})");
        }
    }

    /// <summary>Records a post: in the state file, and in the summary's frontmatter if the summary exists.</summary>
    private static void Recorded(Config config, string transcript, string rel, string kind, string chat, DateTime at, Action<string> log)
    {
        var stamp = at.ToString("yyyy-MM-dd HH:mm", En);
        AddState(config, new SharedRecord(rel, kind, chat, stamp));
        var summary = Draft.SummaryPath(config, transcript);
        if (!File.Exists(summary))
            return;
        var text = PyStr.ReadText(summary);
        var updated = WithShared(text, $"{kind} {stamp}");
        if (updated != text.Replace("\r\n", "\n"))
        {
            PyStr.WriteText(summary, updated);
            log($"  recorded in {config.Relative(summary)}: shared: {kind} {stamp}");
        }
    }

    // ---------------------------------------------------------------- the summary, from the skill

    /// <summary>share summary: the finalised summary's attendee version, previewed (teams-post without --yes) or, with
    /// yes, posted to the meeting's chat and recorded. 0: previewed, posted, or not opted in (it says which); 1: held
    /// back, already posted, or teams-post refused; teams-post's own code otherwise.</summary>
    public static int Summary(Config config, string summaryPath, bool yes, Action<string> log, SummariesHost host)
    {
        var output = host.Out;
        summaryPath = PyPath.Resolve(summaryPath);
        if (!File.Exists(summaryPath))
        {
            host.Err.WriteLine($"meeting-summaries: no summary at {summaryPath}");
            return 2;
        }
        var summary = PyStr.ReadText(summaryPath).Replace("\r\n", "\n");
        var front = Frontmatter(summary);
        string? Field(string key) => Regex.Match(front, $@"^{key}:[ \t]*(.+?)[ \t]*$", RegexOptions.Multiline) is { Success: true } m
            ? m.Groups[1].Value.Trim().Trim('"', '\'')
            : null;
        var transcripts = (Field("transcript") ?? "").Trim('[', ']').Split(',').Select(t => t.Trim().Trim('"', '\''))
            .Where(t => t.Length > 0).Select(t => PyPath.Resolve(Path.Combine(Paths.Parent(summaryPath), t))).ToList();
        var transcript = transcripts.FirstOrDefault()
                         ?? Path.Combine(Paths.Parent(Paths.Parent(summaryPath)), Paths.Name(summaryPath));
        var series = SeriesOf(transcript);
        var meeting = File.Exists(transcript) ? Identify(PyStr.ReadText(transcript)) : null;
        if (meeting is null && Field("title") is { } frontTitle && DateOnly.TryParseExact(Field("date"), "yyyy-MM-dd", En, DateTimeStyles.None, out var frontDate))
            meeting = new MeetingRef(frontTitle, null, frontDate);
        var title = meeting?.Title ?? Field("title");
        var candidates = config.Sharing.Rules.Where(r => r.Summary && (r.Names(series, title) || r.Names(series, Field("title")))).ToList();
        string NotOptedIn() => $"Not opted in: no [[share]] rule with summary = true matches \"{title ?? Paths.Stem(summaryPath)}\" (series {series}). Nothing to post.";
        if (candidates.Count == 0)
        {
            output.WriteLine(NotOptedIn());
            return 0;
        }
        var message = MessageFile(config, transcript, "summary");
        if (SharedEntries(summary).FirstOrDefault(e => e.StartsWith("summary ", StringComparison.Ordinal)) is { } posted)
        {
            output.WriteLine($"Already posted ({posted}). To correct it, edit {message} and run: teams-post edit \"{message}\"");
            return 1;
        }
        var found = Lookup(Finder(config, host, log), meeting, log);
        var rule = found is null ? candidates[0] : candidates.FirstOrDefault(r => r.OrganisedBy(found.Organiser));
        if (rule is null)
        {
            output.WriteLine(NotOptedIn() + " (the rule's organiser isn't this meeting's)");
            return 0;
        }
        var meetingTitle = found?.Subject ?? title ?? Paths.Stem(summaryPath);
        var date = meeting?.Date ?? DateOnly.FromDateTime(host.Today());
        var rels = transcripts.Where(File.Exists).Select(config.Relative).Append(config.Relative(summaryPath)).ToList();
        var attendee = AttendeeVersion(summary, rule.KeyPoints, meetingTitle, date, config.Sharing.Name ?? config.Owner);
        var reasons = HardStops(config, rule, rels, attendee, meeting, found, summary);
        if (reasons.Count > 0)
        {
            output.WriteLine($"Held back, not posted ({config.Relative(summaryPath)}):");
            foreach (var reason in reasons)
                output.WriteLine($"  - {reason}");
            log($"{config.Relative(summaryPath)}: summary held: {string.Join("; ", reasons)}");
            return 1;
        }

        Directory.CreateDirectory(Paths.Parent(message));
        PyStr.WriteText(message, attendee);
        output.WriteLine($"Opted in: {rule.Why}");
        output.WriteLine($"The attendee version is {message}.");
        output.WriteLine();
        List<string> argv = [.. config.Sharing.TeamsPost, "post", "--chat", found!.ChatId!, message];
        if (yes)
            argv.Add("--yes");
        var done = host.RunTool(argv, Paths.Parent(config.Path));
        output.Write(done.StandardOutput);
        if (done.StandardError.Length > 0)
            host.Err.Write(done.StandardError);
        if (done.ExitCode != 0 || !yes)
            return done.ExitCode;

        var stamp = host.Now().ToString("yyyy-MM-dd HH:mm", En);
        var rel = transcripts.Count > 0 && File.Exists(transcripts[0]) ? config.Relative(transcripts[0]) : config.Relative(summaryPath);
        AddState(config, new SharedRecord(rel, "summary", found.ChatId!, stamp));
        var updated = WithShared(CarryInto(config, summary, transcripts.Where(File.Exists).Select(config.Relative)), $"summary {stamp}");
        PyStr.WriteText(summaryPath, updated);
        output.WriteLine($"Recorded in {config.Relative(summaryPath)}: shared: summary {stamp}");
        log($"{config.Relative(summaryPath)}: summary posted to the meeting's chat ({rule.Why})");
        return 0;
    }
}
