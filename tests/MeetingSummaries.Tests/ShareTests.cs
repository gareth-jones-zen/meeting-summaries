using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;
using PyCompat;

namespace MeetingSummaries.Tests;

/// <summary>[[share]] (1.5.0): an opted-in meeting's transcript posted to its chat from the hook, the summary's attendee
/// version from `share summary`, the hard stops, and the record. Made-up meetings; teams-post and the calendar faked.</summary>
public sealed class ShareTests : IDisposable
{
    private const string Chat = "19:meeting_weeklysync000000000000@thread.v2";

    private const string Settings = """
        teams_post = ["C:/tools/teams-post.exe", "--config", "teams-post.toml"]
        never_terms = ['\bbluebird\b', 'project\s+heron']
        org_domains = ["example.com"]
        share_name = "Alex"
        share_state = "shared.json"
        share_folder = "share"

        [review]
        file = "pending.json"
        source = "transcripts"

        """;

    private const string Rule = """
        [[share]]
        series = '^weekly-sync$'
        transcript = true
        summary = true
        why = "The team asked for the record of the weekly sync."

        """;

    private const string TranscriptText = """
        **Weekly sync-20261112_090000UTC-Meeting Recording**

        12 November 2026, 09:00am

        **Alex Smith** 0:01
        Morning all, shall we start with the rollout?

        **Sam Jones** 0:20
        I'll write up the plan by Friday.

        """;

    private const string Finalised = """
        ---
        date: 2026-11-12
        title: Weekly sync
        attendees: [Alex Smith, Sam Jones, Pat Lee]
        transcript: ../2026-11-12.md
        data-sensitivity: Nothing personal.
        ---

        # Weekly sync

        ## Purpose

        The weekly sync: the rollout plan.

        ## Key points

        - **The rollout is on track.** Alex: *"shall we start with the rollout?"*

        ## Decisions

        - The rollout goes ahead on Monday.
        - **[Noted]** "Monday" taken to be 16 Nov.

        ## Actions

        - **Sam Jones** — write up the plan, by Friday.

        ## Open questions

        - Who signs off the comms?

        ## Transcript notes

        - "rollout" is clear throughout.

        **Readings taken while drafting:**

        - Pat was on mute.

        """;

    private readonly Workspace ws;

    public ShareTests() : this(Settings + Rule)
    {
    }

    private ShareTests(string settings)
    {
        ws = new Workspace(settings);
        Workspace.Write(TranscriptPath, TranscriptText);
        ws.Meetings.Answer = Meeting();
    }

    public void Dispose() => ws.Dispose();

    private string TranscriptPath => Path.Combine(ws.Work, "meetings", "weekly-sync", "2026-11-12.md");
    private string SummaryPath => Path.Combine(ws.Work, "meetings", "weekly-sync", "summaries", "2026-11-12.md");
    private string MessagePath(string kind) => Path.Combine(ws.Dir, "share", $"weekly-sync-2026-11-12-{kind}.md");

    private static MeetingInfo Meeting(string? chat = Chat, params string[] others) =>
        new("Weekly sync", new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.Zero),
            new Attendee("Alex Smith", "alex.smith@example.com"),
            others.Length > 0 ? others.Select(e => new Attendee(e, e)).ToList()
                : [new Attendee("Sam Jones", "sam.jones@example.com"), new Attendee("Pat Lee", "pat.lee@example.com")],
            chat);

    private int Hook(Workspace? on = null, params string[] files)
    {
        var w = on ?? ws;
        return Cli.Run(["--config", w.Config.Path, "hook"], w.Host(stdin: string.Join("\n", files.Length > 0 ? files : [TranscriptPath])));
    }

    private (int Code, string Output) ShareSummary(params string[] extra)
    {
        var output = new StringWriter();
        var code = Cli.Run(["--config", ws.Config.Path, "share", "summary", SummaryPath, .. extra], ws.Host(output));
        return (code, output.ToString());
    }

    private List<JsonNode> Held(string type = Share.HeldType)
    {
        var path = Path.Combine(ws.Dir, "pending.json");
        return File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))!["items"]!.AsArray().Where(i => (string?)i!["type"] == type).Select(i => i!).ToList()
            : [];
    }

    private List<string> Reasons() => Held().SelectMany(h => h["reasons"]!.AsArray().Select(r => (string)r!)).ToList();

    // ---------------------------------------------------------------- no rule, no post

    [Fact]
    public void With_no_rule_nothing_is_posted_and_the_calendar_isnt_read()
    {
        using var plain = new Workspace(Settings);
        Workspace.Write(Path.Combine(plain.Work, "meetings", "weekly-sync", "2026-11-12.md"), TranscriptText);
        Assert.Equal(0, Hook(plain, Path.Combine(plain.Work, "meetings", "weekly-sync", "2026-11-12.md")));
        Assert.Empty(plain.ToolRuns);
        Assert.Empty(plain.Meetings.Asked);
        Assert.False(File.Exists(Path.Combine(plain.Dir, "shared.json")));
    }

    [Fact]
    public void A_meeting_no_rule_names_isnt_posted()
    {
        var other = Path.Combine(ws.Work, "meetings", "other-series", "2026-11-12.md");
        Workspace.Write(other, TranscriptText.Replace("Weekly sync", "Other meeting"));
        Assert.Equal(0, Hook(null, other));
        Assert.Empty(ws.ToolRuns);
        Assert.Empty(ws.Meetings.Asked);
    }

    // ---------------------------------------------------------------- the transcript

    [Fact]
    public void An_opted_in_transcript_is_posted_to_its_chat_attached_and_recorded()
    {
        Assert.Equal(0, Hook());
        var (argv, cwd) = Assert.Single(ws.ToolRuns);
        Assert.Equal(["C:/tools/teams-post.exe", "--config", "teams-post.toml", "post", "--chat", Chat, MessagePath("transcript"),
            "--attach", TranscriptPath, "--name", "Weekly sync 2026-11-12 transcript.md", "--yes"], argv);
        Assert.Equal(ws.Dir, cwd);
        Assert.Equal("Transcript of Weekly sync, 12 Nov 2026: speech-to-text, so names and terms may be wrong\n",
            Workspace.Read(MessagePath("transcript")));
        var asked = Assert.Single(ws.Meetings.Asked);
        Assert.Equal(("Weekly sync", new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)), (asked.Title, asked.Start!.Value));
        var record = Assert.Single(Share.LoadState(ws.Config));
        Assert.Equal(("meetings/weekly-sync/2026-11-12.md", "transcript", Chat, "2026-11-12 09:30"), (record.Transcript, record.Kind, record.Chat, record.At));
        Assert.Empty(Held());

        // never twice: the same transcript given again isn't posted again
        Assert.Equal(0, Hook());
        Assert.Single(ws.ToolRuns);
    }

    [Fact]
    public void A_transcript_posted_before_its_summary_is_carried_into_the_draft()
    {
        Assert.Equal(0, Hook());
        ws.Claude = new FakeClaude(Workspace.DraftReply.Replace("../2026-11-06.md", "../2026-11-12.md"));
        Assert.Equal("written", MeetingSummaries.Draft.WriteDraft(ws.Config, TranscriptPath, ws.Log.Add, ws.Host()));
        Assert.Equal(["transcript 2026-11-12 09:30"], Share.SharedEntries(Workspace.Read(SummaryPath)));
    }

    [Fact]
    public void A_transcript_posted_after_its_summary_is_recorded_in_it()
    {
        Workspace.Write(SummaryPath, Finalised);
        Assert.Equal(0, Hook());
        var summary = Workspace.Read(SummaryPath);
        Assert.Equal(["transcript 2026-11-12 09:30"], Share.SharedEntries(summary));
        Assert.Contains("data-sensitivity: Nothing personal.\nshared: [transcript 2026-11-12 09:30]\n---\n", summary);
    }

    [Fact]
    public void An_external_meeting_needs_external_true()
    {
        ws.Meetings.Answer = Meeting(Chat, "sam.jones@example.com", "guest@partner.example.org");
        Assert.Equal(0, Hook());
        Assert.Empty(ws.ToolRuns);
        Assert.Contains("1 attendee is external (the rule doesn't say external = true)", Reasons());

        using var open = new Workspace(Settings + Rule.Replace("transcript = true", "transcript = true\nexternal = true"));
        Workspace.Write(Path.Combine(open.Work, "meetings", "weekly-sync", "2026-11-12.md"), TranscriptText);
        open.Meetings.Answer = Meeting(Chat, "sam.jones@example.com", "guest@partner.example.org");
        Assert.Equal(0, Hook(open, Path.Combine(open.Work, "meetings", "weekly-sync", "2026-11-12.md")));
        Assert.Equal("--external", Assert.Single(open.ToolRuns).Argv[^1]);
    }

    [Theory]
    [InlineData("one-to-one")]
    [InlineData("never-term")]
    [InlineData("no-chat")]
    [InlineData("not-in-calendar")]
    [InlineData("no-header")]
    public void Each_hard_stop_holds_the_transcript_and_raises_it(string stop)
    {
        string expected;
        switch (stop)
        {
            case "one-to-one":
                ws.Meetings.Answer = Meeting(Chat, "sam.jones@example.com");
                expected = "it's a 1:1";
                break;
            case "never-term":
                Workspace.Write(TranscriptPath, TranscriptText + "**Sam Jones** 0:40\nAnd Project  Heron is next.\n");
                expected = "a need-to-know term appears in it (never_terms[1])";
                break;
            case "no-chat":
                ws.Meetings.Answer = Meeting(chat: null);
                expected = "the meeting's Teams chat couldn't be found";
                break;
            case "not-in-calendar":
                ws.Meetings.Answer = null;
                expected = "\"Weekly sync\" on 12 Nov 2026 isn't in the calendar";
                break;
            default:
                Workspace.Write(TranscriptPath, "**Alex Smith** 0:01\nMorning.\n");
                expected = "which meeting it is isn't known (the transcript doesn't start with the recording's name)";
                break;
        }
        Assert.Equal(0, Hook());
        Assert.Empty(ws.ToolRuns);
        Assert.Contains(expected, Reasons());
        var item = Assert.Single(Held());
        Assert.Equal(("meetings/weekly-sync/2026-11-12.md", "transcript"), ((string?)item["path"], (string?)item["kind"]));
        Assert.Empty(Share.LoadState(ws.Config));
        Assert.DoesNotContain("heron", File.ReadAllText(Path.Combine(ws.Dir, "pending.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Personnel_folders_are_never_posted()
    {
        using var all = new Workspace(Settings + Rule.Replace("'^weekly-sync$'", "'.*'"));
        var personal = Path.Combine(all.Work, "team", "robin-hall", "2026-11-12.md");
        Workspace.Write(personal, TranscriptText);
        all.Meetings.Answer = Meeting();
        Assert.Equal(0, Hook(all, personal));
        Assert.Empty(all.ToolRuns);
        var items = JsonNode.Parse(File.ReadAllText(Path.Combine(all.Dir, "pending.json")))!["items"]!.AsArray();
        Assert.Contains("team/robin-hall/2026-11-12.md is under team/, which holds personnel records",
            items.Single()!["reasons"]!.AsArray().Select(r => (string)r!));
    }

    [Fact]
    public void Without_Files_ReadWrite_the_transcript_is_held_and_not_recorded()
    {
        ws.ToolExit = 3;
        Assert.Equal(0, Hook());
        Assert.Single(ws.ToolRuns);
        Assert.Contains(Reasons(), r => r.Contains("needs Files.ReadWrite", StringComparison.Ordinal));
        Assert.Empty(Share.LoadState(ws.Config));
    }

    [Fact]
    public void A_teams_post_failure_is_raised_and_notified()
    {
        ws.ToolExit = 1;
        Assert.Equal(0, Hook());
        Assert.Single(Held(Share.FailedType));
        Assert.Single(ws.Notified);
        Assert.Empty(Share.LoadState(ws.Config));
    }

    // ---------------------------------------------------------------- the summary

    [Fact]
    public void The_attendee_version_keeps_only_the_shared_sections()
    {
        var text = Share.AttendeeVersion(Finalised, false, "Weekly sync", new DateOnly(2026, 11, 12), "Alex");
        Assert.Equal("""
            ## Purpose

            The weekly sync: the rollout plan.

            ## Decisions

            - The rollout goes ahead on Monday.

            ## Actions

            - **Sam Jones** — write up the plan, by Friday.

            ## Open questions

            - Who signs off the comms?

            _Summary of Weekly sync, 12 Nov 2026, by Alex's assistant, checked by Alex._

            """.Replace("\r\n", "\n"), text);
        var withKeyPoints = Share.AttendeeVersion(Finalised, true, "Weekly sync", new DateOnly(2026, 11, 12), "Alex");
        Assert.Contains("## Key points\n\n- **The rollout is on track.**", withKeyPoints);
        Assert.DoesNotContain("Transcript notes", withKeyPoints);
        Assert.DoesNotContain("Readings", withKeyPoints);
        Assert.DoesNotContain("[Noted]", withKeyPoints);
    }

    [Fact]
    public void Share_summary_previews_then_posts_once_and_records_it()
    {
        Workspace.Write(SummaryPath, Finalised);
        var (code, output) = ShareSummary();
        Assert.Equal(0, code);
        var (argv, _) = Assert.Single(ws.ToolRuns);
        Assert.Equal(["C:/tools/teams-post.exe", "--config", "teams-post.toml", "post", "--chat", Chat, MessagePath("summary")], argv);
        Assert.Contains("Opted in: The team asked for the record of the weekly sync.", output);
        Assert.StartsWith("## Purpose", Workspace.Read(MessagePath("summary")));
        Assert.Empty(Share.SharedEntries(Workspace.Read(SummaryPath)));

        (code, output) = ShareSummary("--yes");
        Assert.Equal(0, code);
        Assert.Equal("--yes", ws.ToolRuns[^1].Argv[^1]);
        Assert.Equal(["summary 2026-11-12 09:30"], Share.SharedEntries(Workspace.Read(SummaryPath)));
        Assert.Contains("Recorded in meetings/weekly-sync/summaries/2026-11-12.md", output);

        (code, output) = ShareSummary("--yes");
        Assert.Equal(1, code);
        Assert.Contains("Already posted (summary 2026-11-12 09:30)", output);
        Assert.Equal(2, ws.ToolRuns.Count);
    }

    [Fact]
    public void A_summary_not_opted_in_says_so_and_reads_nothing()
    {
        using var transcriptOnly = new ShareTests(Settings + Rule.Replace("summary = true\n", ""));
        Workspace.Write(transcriptOnly.SummaryPath, Finalised);
        var (code, output) = transcriptOnly.ShareSummary("--yes");
        Assert.Equal(0, code);
        Assert.Contains("Not opted in", output);
        Assert.Empty(transcriptOnly.ws.ToolRuns);
        Assert.Empty(transcriptOnly.ws.Meetings.Asked);
    }

    [Theory]
    [InlineData("status: draft\n", "it's still a draft (status: draft or [DRAFT])")]
    [InlineData("data-sensitivity: Keep it out of weekly updates, pre-reads and broadcasts.\n", "its data-sensitivity line keeps it out of chats, broadcasts or pre-reads")]
    [InlineData("questions\n", "it still has [Owner] questions to answer")]
    [InlineData("term\n", "a need-to-know term appears in it (never_terms[0])")]
    public void Each_summary_hard_stop_holds_it(string change, string reason)
    {
        var text = change switch
        {
            "questions\n" => Finalised.Replace("## Purpose", "## For review\n\n- **[Owner]** Which Monday?\n\n## Purpose"),
            "term\n" => Finalised.Replace("- \"rollout\" is clear throughout.", "- the bluebird budget came up."),
            _ when change.StartsWith("data-", StringComparison.Ordinal) => Finalised.Replace("data-sensitivity: Nothing personal.\n", change),
            _ => Finalised.Replace("---\n\n# Weekly sync", change + "---\n\n# Weekly sync"),
        };
        Workspace.Write(SummaryPath, text);
        var (code, output) = ShareSummary("--yes");
        Assert.Equal(1, code);
        Assert.Contains("Held back, not posted", output);
        Assert.Contains(reason, output);
        Assert.Empty(ws.ToolRuns);
        Assert.Empty(Share.SharedEntries(Workspace.Read(SummaryPath)));
    }

    // ---------------------------------------------------------------- settings

    [Theory]
    [InlineData("[[share]]\nseries = 'x'\ntranscript = true\n", "needs a why")]
    [InlineData("[[share]]\ntranscript = true\nwhy = 'y'\n", "needs series or title")]
    [InlineData("[[share]]\nseries = 'x'\nfile = 'pdf'\nwhy = 'y'\n", "file should be")]
    [InlineData("[[share]]\nseries = 'x'\nwhy = 'y'\nwhen = 'always'\n", "unknown setting 'when'")]
    [InlineData("[[share]]\nseries = '('\nwhy = 'y'\n", "isn't a valid regular expression")]
    public void Bad_share_rules_are_settings_errors(string rule, string expected)
    {
        var path = Path.Combine(ws.Dir, "bad.toml");
        Workspace.Write(path, "workspace = \"work\"\nteams_post = [\"tp.exe\"]\n" + rule);
        Assert.Contains(expected, Assert.Throws<ConfigError>(() => Config.Load(path)).Message);
    }

    [Fact]
    public void Rules_need_teams_post()
    {
        var path = Path.Combine(ws.Dir, "bad.toml");
        Workspace.Write(path, "workspace = \"work\"\n[[share]]\nseries = 'x'\nwhy = 'y'\n");
        Assert.Contains("needs teams_post", Assert.Throws<ConfigError>(() => Config.Load(path)).Message);
    }

    [Fact]
    public void Frontmatter_records_are_added_once()
    {
        var once = Share.WithShared(Finalised, "transcript 2026-11-12 09:30");
        var twice = Share.WithShared(Share.WithShared(once, "summary 2026-11-12 16:05"), "transcript 2026-11-12 09:30");
        Assert.Equal(["transcript 2026-11-12 09:30", "summary 2026-11-12 16:05"], Share.SharedEntries(twice));
        Assert.Equal("no frontmatter", Share.WithShared("no frontmatter", "x"));
    }
}

/// <summary>Finding the meeting in the calendar through Graph, against a fake Graph (reads only).</summary>
public sealed class CalendarTests
{
    private sealed class FakeGraph(JsonArray events, JsonArray? chats = null) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
            Urls.Add(url);
            Assert.Equal(HttpMethod.Get, request.Method);
            var body = url.StartsWith("/v1.0/me/calendarView", StringComparison.Ordinal) ? new JsonObject { ["value"] = events.DeepClone() }
                : new JsonObject { ["value"] = (chats ?? []).DeepClone() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    private static JsonObject Event(string subject, string start, string end, string join, params string[] attendees) => new()
    {
        ["subject"] = subject,
        ["start"] = new JsonObject { ["dateTime"] = start, ["timeZone"] = "UTC" },
        ["end"] = new JsonObject { ["dateTime"] = end, ["timeZone"] = "UTC" },
        ["organizer"] = new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Alex Smith", ["address"] = "alex.smith@example.com" } },
        ["attendees"] = new JsonArray([.. attendees.Select(a => (JsonNode)new JsonObject
        {
            ["type"] = a.StartsWith("room", StringComparison.Ordinal) ? "resource" : "required",
            ["emailAddress"] = new JsonObject { ["name"] = a, ["address"] = a },
        })]),
        ["onlineMeeting"] = new JsonObject { ["joinUrl"] = join },
    };

    private static GraphMeetingFinder Finder(FakeGraph graph) =>
        new(new GraphClient(new GraphClientOptions { Log = _ => { } }, graph, token: (_, _) => Task.FromResult("token"),
            delay: (_, _) => Task.CompletedTask), ["Calendars.Read", "Chat.Read"]);

    [Fact]
    public void The_event_is_matched_by_subject_and_time_and_its_chat_read_from_the_join_link()
    {
        var events = new JsonArray(
            Event("Alt Net & PCB", "2026-11-05T09:00:00.0000000", "2026-11-05T10:00:00.0000000",
                "https://teams.microsoft.com/l/meetup-join/19%3ameeting_OLD%40thread.v2/0?context=%7b%7d", "sam@example.com"),
            Event("Alt Net & PCB", "2026-11-12T09:00:00.0000000", "2026-11-12T10:00:00.0000000",
                "https://teams.microsoft.com/l/meetup-join/19%3ameeting_NEW%40thread.v2/0?context=%7b%7d", "sam@example.com", "room-1@example.com"),
            Event("Something else", "2026-11-12T09:00:00.0000000", "2026-11-12T10:00:00.0000000", "https://teams.microsoft.com/l/meetup-join/19%3ameeting_X%40thread.v2/0"));
        var graph = new FakeGraph(events);
        var found = Finder(graph).Find(new MeetingRef("Alt Net  PCB", new DateTimeOffset(2026, 11, 12, 9, 2, 0, TimeSpan.Zero), new DateOnly(2026, 11, 12)))!;
        Assert.Equal("19:meeting_NEW@thread.v2", found.ChatId);
        Assert.Equal(["alex.smith@example.com", "sam@example.com"], found.People.Select(p => p.Email).ToList());   // the room left out
        Assert.Contains("startDateTime=2026-11-11T21:02:00Z&endDateTime=2026-11-12T11:02:00Z", Assert.Single(graph.Urls));
    }

    [Fact]
    public void A_short_join_link_is_found_among_the_meeting_chats()
    {
        const string join = "https://teams.microsoft.com/meet/123456789?p=abc";
        var events = new JsonArray(Event("Weekly sync", "2026-11-12T09:00:00Z", "2026-11-12T09:30:00Z", join));
        var chats = new JsonArray(
            new JsonObject { ["id"] = "19:other@thread.v2", ["onlineMeetingInfo"] = null },
            new JsonObject { ["id"] = "19:meeting_sync@thread.v2", ["onlineMeetingInfo"] = new JsonObject { ["joinWebUrl"] = join } });
        var found = Finder(new FakeGraph(events, chats)).Find(new MeetingRef("Weekly sync", new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero), new DateOnly(2026, 11, 12)));
        Assert.Equal("19:meeting_sync@thread.v2", found!.ChatId);
    }

    [Fact]
    public void A_recording_outside_the_event_or_two_events_on_a_date_find_nothing()
    {
        var events = new JsonArray(
            Event("Weekly sync", "2026-11-12T09:00:00Z", "2026-11-12T09:30:00Z", ""),
            Event("Weekly sync", "2026-11-12T15:00:00Z", "2026-11-12T15:30:00Z", ""));
        Assert.Null(Finder(new FakeGraph(events)).Find(new MeetingRef("Weekly sync", new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 11, 12))));
        Assert.Null(Finder(new FakeGraph(events)).Find(new MeetingRef("Weekly sync", null, new DateOnly(2026, 11, 12))));
        Assert.Null(Finder(new FakeGraph(events)).Find(new MeetingRef("Weekly sync", new DateTimeOffset(2026, 11, 12, 9, 5, 0, TimeSpan.Zero), new DateOnly(2026, 11, 12)))!.ChatId);
    }

    [Fact]
    public void The_recording_name_says_which_meeting_and_when()
    {
        var utc = Share.Identify("**Weekly sync-20261112_090017UTC-Meeting Recording**\n\n12 November 2026\n")!;
        Assert.Equal(("Weekly sync", new DateTimeOffset(2026, 11, 12, 9, 0, 17, TimeSpan.Zero)), (utc.Title, utc.Start!.Value));
        Assert.Equal("Catch up about the plan", Share.Identify("\n**Catch up about the plan-20261124_103829-Meeting Transcript**\n")!.Title);
        Assert.Null(Share.Identify("**Alex Smith** 0:01\nHello\n"));
    }
}
