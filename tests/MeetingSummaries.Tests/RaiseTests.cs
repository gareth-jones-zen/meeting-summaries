using System.Text.Json.Nodes;
using PyCompat;

namespace MeetingSummaries.Tests;

/// <summary>hook raising (1.4.0): what doc-filer files becomes items for the next session, classified by the settings.</summary>
public sealed class RaiseTests : IDisposable
{
    private readonly Workspace ws = new("""
        hook_drafts = true
        raise_transcripts = ["meetings/**", "team/**"]
        [review]
        file = "pending.json"
        source = "transcripts"
        [[raise]]
        match = "team/*/performance-reviews/????-??-review-form.md"
        type = "review-paperwork"
        [[raise]]
        match = "team/*/performance-reviews/????-??-review-transcript*.md"
        type = "review-transcript"
        sweep = true
        note = "If the review goes on in another session, refresh its prep from this part first."
        """);

    public void Dispose() => ws.Dispose();

    private JsonArray Items() => JsonNode.Parse(File.ReadAllText(Path.Combine(ws.Dir, "pending.json")))!["items"]!.AsArray();

    [Fact]
    public void Filed_transcripts_and_review_files_are_raised_and_only_meetings_are_drafted()
    {
        var oneToOne = Path.Combine(ws.Work, "team", "robin-hall", "one-to-ones", "2026-11-10.md");
        var form = Path.Combine(ws.Work, "team", "robin-hall", "performance-reviews", "2026-09-review-form.md");
        var review = Path.Combine(ws.Work, "team", "robin-hall", "performance-reviews", "2026-09-review-transcript-part1.md");
        var summary = Path.Combine(ws.Work, "meetings", "alex", "summaries", "2026-11-06.md");
        var other = Path.Combine(ws.Work, "reports", "note.md");
        Workspace.Write(oneToOne, "**GJ** 0:01\nClaude, please add a note to my inbox to check the tags.\n");
        Workspace.Write(form, "the form");
        Workspace.Write(review, "**GJ** 0:01\nClaude, add an item to chase the training budget.\n");
        Workspace.Write(summary, "a summary");
        Workspace.Write(other, "a report");
        var report = new JsonObject { ["written"] = new JsonArray(new[] { ws.TranscriptPath, oneToOne, form, review, summary, other }
            .Select(p => (JsonNode)Path.GetRelativePath(ws.Work, p).Replace('\\', '/')).ToArray()) };
        var host = ws.Host(stdin: report.ToJsonString());
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = ws.Work;   // doc-filer runs the hook in the repo, and reports paths relative to it
        try
        {
            Assert.Equal(0, Cli.Run(["--config", ws.Config.Path, "hook"], host));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        var items = Items().Select(i => ((string?)i!["type"], (string?)i["path"], (bool?)i["summary_needed"], i["dictation"]!.AsArray().Count)).ToList();
        Assert.Equal([
            ("transcript", "meetings/alex/2026-11-06.md", true, 0),
            ("transcript", "team/robin-hall/one-to-ones/2026-11-10.md", false, 1),
            ("review-paperwork", "team/robin-hall/performance-reviews/2026-09-review-form.md", false, 0),
            ("review-transcript", "team/robin-hall/performance-reviews/2026-09-review-transcript-part1.md", false, 1),
        ], items);
        var swept = Items()[1]!;
        Assert.Equal(("2026-11-12T09:30:00", "transcripts", 2), ((string?)swept["arrived"], (string?)swept["source"], (int)swept["dictation"]![0]!["line"]!));
        Assert.Equal("If the review goes on in another session, refresh its prep from this part first.", (string?)Items()[3]!["note"]);
        Assert.Equal(ws.TranscriptPath, Assert.Single(ws.Queue.Pending()).Argv[^1]);   // only the meeting is drafted
    }

    [Fact]
    public void Raising_needs_a_review_queue()
    {
        Workspace.Write(Path.Combine(ws.Dir, "bad.toml"), "workspace = \"work\"\nraise_transcripts = [\"**\"]\n");
        var error = Assert.Throws<ConfigError>(() => Config.Load(Path.Combine(ws.Dir, "bad.toml")));
        Assert.Contains("need [review]", error.Message);
    }

    [Fact]
    public void Hook_drafts_false_raises_without_drafting()
    {
        using var quiet = new Workspace("hook_drafts = false\nraise_transcripts = [\"meetings/**\"]\n[review]\nfile = \"pending.json\"\nsource = \"t\"\n");
        Assert.Equal(0, Cli.Run(["--config", quiet.Config.Path, "hook"], quiet.Host(stdin: quiet.TranscriptPath)));
        Assert.Empty(quiet.Queue.Pending());
        Assert.Single(JsonNode.Parse(File.ReadAllText(Path.Combine(quiet.Dir, "pending.json")))!["items"]!.AsArray());
    }
}
