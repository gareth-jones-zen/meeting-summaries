namespace MeetingSummaries.Tests;

public class CliTests : IDisposable
{
    private readonly Workspace ws = new();

    public void Dispose() => ws.Dispose();

    private (int Code, string Out, string Err) Run(string stdin, params string[] args)
    {
        var output = new StringWriter();
        var host = ws.Host(output, stdin);
        var code = Cli.Run(args, host);
        return (code, output.ToString().Replace("\r\n", "\n"), host.Err.ToString()!);
    }

    [Fact]
    public void The_hook_queues_wanted_transcripts_from_any_report_shape()
    {
        var privateFile = Path.Combine(ws.Work, "meetings", "private", "b.md");
        Workspace.Write(privateFile, "x");
        var t = ws.TranscriptPath.Replace(@"\", @"\\");
        string[] reports =
        [
            $"[{{\"status\": \"written\", \"path\": \"{t}\"}}, {{\"path\": \"{privateFile.Replace(@"\", @"\\")}\"}}]",
            $"{{\"written\": [\"{t}\"]}}",
            ws.TranscriptPath + "\n\n" + privateFile + "\n",
            $"[{{\"file\": \"{t}\"}}, {{\"path\": \"\"}}, \"{t}\"]",
        ];
        foreach (var report in reports)
            Assert.Equal(0, Run(report, "--config", ws.Config.Path, "hook").Code);
        Assert.Equal(4, ws.QueueRunnerStarts);
        var job = Assert.Single(ws.Queue.Pending());
        Assert.Equal([@"C:\tools\meeting-summaries.exe", "--config", ws.Config.Path, "draft", ws.TranscriptPath], job.Argv);
        Assert.Equal(ws.Config.Workspace, job.Cwd);
    }

    [Fact]
    public void The_hook_with_nothing_new_and_nothing_queued_starts_nothing()
    {
        Assert.Equal(0, Run("", "--config", ws.Config.Path, "hook").Code);
        Assert.Equal(0, ws.QueueRunnerStarts);
    }

    [Fact]
    public void Draft_from_the_command_line()
    {
        var (code, output, _) = Run("", "--config", ws.Config.Path, "draft", ws.TranscriptPath);
        Assert.Equal(0, code);
        Assert.True(File.Exists(ws.SummaryPath));
        Assert.Equal("", output);   // the test host doesn't echo the log
        Assert.Contains("draft written to meetings/alex/summaries/2026-11-06.md", File.ReadAllText(Path.Combine(ws.Dir, "log.txt")));
        Assert.Equal(2, Run("", "--config", ws.Config.Path, "draft", Path.Combine(ws.Dir, "missing.md")).Code);
    }

    [Fact]
    public void A_failed_draft_is_logged_and_notified()
    {
        ws.Claude = new FakeClaude("not a draft");
        Assert.Equal(1, Run("", "--config", ws.Config.Path, "draft", ws.TranscriptPath).Code);
        Assert.Equal("Summary draft failed", Assert.Single(ws.Notified).Title);
        Assert.Contains("2026-11-06.md: FAILED: the reply wasn't a draft summary: not a draft", File.ReadAllText(Path.Combine(ws.Dir, "log.txt")));
    }

    [Fact]
    public void Check_prints_each_quote_to_verify()
    {
        var summary = Path.Combine(ws.Work, "meetings", "alex", "summaries", "2026-11-06.md");
        Workspace.Write(summary, Workspace.DraftReply);
        var (code, output, _) = Run("", "check", summary);
        Assert.Equal(1, code);
        Assert.Equal($"{summary}: 2 quotes, 1 to verify\n  \"I'm not worried about the dates\" (it has a negation the transcript doesn't)\n" +
                     "      transcript: im worried about the dates\n", output);
        Workspace.Write(summary, "no field here");
        Assert.Equal(1, Run("", "check", summary).Code);
    }

    [Fact]
    public void Dictation_lists_candidates_with_line_numbers()
    {
        Workspace.Write(ws.TranscriptPath, "**Clark** 0:01\nClaude, please add a note to my inbox.\n");
        var (code, output, _) = Run("", "--config", ws.Config.Path, "dictation", ws.TranscriptPath);
        Assert.Equal(0, code);
        Assert.Equal($"{ws.TranscriptPath}: 1 candidate(s)\n  2: Claude, please add a note to my inbox.\n", output);
        // No settings file: the default names.
        Assert.Equal(0, Run("", "--config", Path.Combine(ws.Dir, "none.toml"), "dictation", ws.TranscriptPath).Code);
    }

    [Fact]
    public void Config_says_where_the_settings_are()
    {
        Assert.Equal((0, "x.toml\n", ""), Run("", "--config", "x.toml", "config"));
    }

    [Fact]
    public void Bad_arguments_exit_2()
    {
        Assert.Equal(2, Run("").Code);
        Assert.Equal(2, Run("", "nonsense").Code);
        Assert.Equal(2, Run("", "draft").Code);
        Assert.Equal(2, Run("", "autostart", "sideways").Code);
        Assert.Equal(2, Run("", "--config").Code);
        Assert.Equal(2, Run("", "--config", Path.Combine(ws.Dir, "none.toml"), "hook").Code);
        Assert.Equal((0, "0.3.0\n", ""), Run("", "--version"));
    }

    [Fact]
    public void Run_queue_works_the_seats_queue()
    {
        ws.Queue.Add(["cmd.exe", "/c", "exit 0"], ws.Dir);
        var (code, output, _) = Run("", "run-queue");
        Assert.Equal(0, code);
        Assert.EndsWith("1 job(s) run; 0 still queued.\n", output);
    }
}
