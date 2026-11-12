using System.Text.Json.Nodes;
using ClaudeHeadless;
using PyCompat;
using WinBackground;

namespace MeetingSummaries.Tests;

/// <summary>A synthetic workspace (no real meeting content), with settings, as the Python tests set up.</summary>
public sealed class Workspace : IDisposable
{
    public const string Transcript = """
        # Catch-up 2026-11-06

        **Alex Smith** 0:01
        We agreed the rollout goes ahead on Monday, and I'm worried about the dates.

        **Sam Jones** 0:20
        I'll write up the plan by Friday.

        """;

    public const string DraftReply = """
        ---
        date: 2026-11-06
        title: Catch-up
        transcript: ../2026-11-06.md
        status: draft
        ---

        # [DRAFT] Catch-up

        ## For review

        - **[Owner]** Did you mean Monday the 9th?
        - **[Noted]** "Sam" taken to be Sam Jones.

        ## Purpose

        Weekly catch-up.

        ## Key points

        - Alex: *"the rollout goes ahead on Monday"*, and *"I'm not worried about the dates"*.

        ## Decisions

        ## Actions

        ## Open questions

        ## Transcript notes

        """;

    public const string LedgerText = "# Review ledger\n\n## Open\n\n- 2026-11-01 \u00b7 [x](x) \u00b7 an older question\n\n## Settled\n\n- leave: 16-31 Dec\n";

    public string Dir { get; } = Paths.Resolve(Path.Combine(Path.GetTempPath(), "meeting-summaries-tests-" + Guid.NewGuid().ToString("N")));
    public string Work => Path.Combine(Dir, "work");
    public string TranscriptPath => Path.Combine(Work, "meetings", "alex", "2026-11-06.md");
    public string SummaryPath => Path.Combine(Work, "meetings", "alex", "summaries", "2026-11-06.md");
    public string Ledger => Path.Combine(Work, "ledger.md");
    public Config Config { get; }
    public JobQueue Queue { get; }
    public List<string> Log { get; } = [];
    public List<(string Title, IReadOnlyList<string> Lines)> Notified { get; } = [];
    public List<(IReadOnlyList<string> Argv, string Input, string Cwd)> AfterDrafts { get; } = [];
    public int QueueRunnerStarts { get; set; }
    public FakeClaude Claude { get; set; } = new(DraftReply);

    public Workspace(string extraSettings = "")
    {
        Directory.CreateDirectory(Path.Combine(Work, "meetings", "alex"));
        Write(TranscriptPath, Transcript);
        Write(Ledger, LedgerText);
        Write(Path.Combine(Dir, "style.md"), "- British English.");
        Write(Path.Combine(Dir, "config.toml"),
            "workspace = \"work\"\nowner = \"Owner\"\nstyle = \"style.md\"\nledger = \"ledger.md\"\n" +
            "include = [\"meetings/**\"]\nexclude = [\"**/private/**\"]\nlog_file = \"log.txt\"\n" + extraSettings);
        Config = Config.Load(Path.Combine(Dir, "config.toml"));
        Queue = new JobQueue(Path.Combine(Dir, "queue"));
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PyStr.WriteText(path, text.Replace("\r\n", "\n"));
    }

    public static string Read(string path) => PyStr.ReadText(path);

    public SummariesHost Host(TextWriter? output = null, string stdin = "") => new()
    {
        Out = output ?? new StringWriter(),
        Err = new StringWriter(),
        Stdin = () => new StringReader(stdin),
        Claude = Claude,
        Queue = Queue,
        Notify = (title, lines) => Notified.Add((title, lines)),
        Today = () => new DateTime(2026, 11, 12),
        Exe = @"C:\tools\meeting-summaries.exe",
        BackgroundExe = @"C:\tools\meeting-summaries-background.exe",
        StartQueueRunner = () => QueueRunnerStarts++,
        RunAfterDraft = (argv, input, cwd) =>
        {
            AfterDrafts.Add((argv, input, cwd));
            return new Completed(0, "", "");
        },
    };

    public string Draft(string? transcript = null, string? output = null) =>
        MeetingSummaries.Draft.WriteDraft(Config, transcript ?? TranscriptPath, Log.Add, Host(), output);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Claude, faked: never run for real in a test.</summary>
public sealed class FakeClaude(string? reply, Exception? error = null) : IClaudeRunner
{
    public List<(IReadOnlyList<string> Command, string Prompt, string Cwd)> Calls { get; } = [];

    public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout)
    {
        Calls.Add((command, prompt, cwd));
        if (error is not null)
            throw error;
        return new JsonObject { ["result"] = reply?.Replace("\r\n", "\n"), ["num_turns"] = 3, ["session_id"] = "s1" };
    }
}
