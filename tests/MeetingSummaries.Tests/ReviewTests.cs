using System.Text.Json.Nodes;
using PyCompat;

namespace MeetingSummaries.Tests;

/// <summary>[review]: after a draft, the review queue's items for its transcripts get summary_draft, exactly as the EA's
/// after_draft hook (tools/summary-drafted.py) set them. Golden/hooks.json is that hook run, unchanged, on each report
/// against a seeded queue: the items it left.</summary>
public sealed class ReviewTests
{
    private static JsonArray Golden() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "hooks.json")))!.AsArray();

    public static TheoryData<string> Cases() => [.. Golden().Select(g => (string)g!["name"]!)];

    private static string Settings(string queue) =>
        $"\n[review]\nfile = \"{queue.Replace('\\', '/')}\"\nsource = \"transcripts\"\nlock = \"ms-review-test-{Guid.NewGuid():N}\"\n";

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_items_are_the_python_hooks(string name)
    {
        var golden = Golden().Single(g => (string)g!["name"]! == name)!;
        var queue = Path.Combine(Path.GetTempPath(), $"ms-review-{Guid.NewGuid():N}", "pending.json");
        using var ws = new Workspace(Settings(queue));
        try
        {
            Workspace.Write(queue, PyJson.Dump(golden["seed"], indent: 2, ensureAscii: false));
            var report = golden["report"]!;
            var log = new List<string>();
            Draft.Review(ws.Config, [.. report["transcripts"]!.AsArray().Select(t => (string)t!)], (string)report["summary"]!, log.Add);
            Assert.Equal(PyJson.Dump(golden["items"]), PyJson.Dump(new JsonArray([.. ws.Config.Review!.Open().Items()])));
            var noted = golden["items"]!.AsArray().Count(i => i!["summary_draft"] is not null);
            Assert.Equal([$"  review: the draft noted against {noted} item(s) under transcripts"], log);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(queue)!, true);
        }
    }

    [Fact]
    public void A_draft_is_noted_against_its_transcripts_item_and_a_draft_written_elsewhere_isnt()
    {
        var queue = Path.Combine(Path.GetTempPath(), $"ms-review-{Guid.NewGuid():N}", "pending.json");
        using var ws = new Workspace(Settings(queue));
        try
        {
            var item = new JsonObject { ["path"] = "meetings/alex/2026-11-06.md", ["title"] = "Catch-up" };
            ws.Config.Review!.Open().Add("transcripts", "transcript", item);

            ws.Claude = new FakeClaude(Workspace.DraftReply);
            Assert.Equal("written", ws.Draft(output: Path.Combine(ws.Dir, "elsewhere.md")));   // --out: nothing noted
            Assert.Null(ws.Config.Review.Open().Items().Single()["summary_draft"]);

            Assert.Equal("written", ws.Draft());
            Assert.Equal("meetings/alex/summaries/2026-11-06.md", (string)ws.Config.Review.Open().Items().Single()["summary_draft"]!);
            Assert.Contains("  review: the draft noted against 1 item(s) under transcripts", ws.Log);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(queue)!, true);
        }
    }

    [Fact]
    public void No_review_section_notes_nothing()
    {
        using var ws = new Workspace();
        Assert.Null(ws.Config.Review);
        var log = new List<string>();
        Draft.Review(ws.Config, ["meetings/alex/2026-11-06.md"], "s.md", log.Add);
        Assert.Empty(log);
    }

    [Fact]
    public void A_queue_that_cant_be_written_is_logged_and_the_draft_goes_on()
    {
        var queue = Path.Combine(Path.GetTempPath(), $"ms-review-{Guid.NewGuid():N}", "pending.json");
        using var ws = new Workspace(Settings(queue));
        try
        {
            Workspace.Write(queue, "[]");   // JSON, but not a queue
            Assert.Equal("written", ws.Draft());
            Assert.Contains(ws.Log, l => l.StartsWith("  review queue failed: ", StringComparison.Ordinal));
            Assert.Equal("[]", File.ReadAllText(queue));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(queue)!, true);
        }
    }

    [Theory]
    [InlineData("\n[review]\nsource = \"transcripts\"\n", "needs file")]
    [InlineData("\n[review]\nfile = \"q.json\"\nsource = \"transcripts\"\nact = true\n", "act under [review] in ")]
    [InlineData("\nreview = 1\n", "review must be a [review] table")]
    public void Bad_settings_are_refused(string toml, string message) =>
        Assert.Contains(message, Assert.Throws<ConfigError>(() => new Workspace(toml)).Message);
}
