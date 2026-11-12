using System.Diagnostics;
using System.Text;
using ClaudeHeadless;
using WinBackground;

namespace MeetingSummaries;

/// <summary>Everything the tool touches outside itself, so tests and parity checks can give it fakes: where it
/// writes, Claude, the seat's queue, notifications, the date, and the programs it starts.</summary>
public sealed class SummariesHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where log lines are echoed, as typed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<TextReader> Stdin { get; init; } = () => Console.In;
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;

    private IClaudeRunner? claude;

    /// <summary>Claude Code, headless. Created on first use, so a fake can be given instead.</summary>
    public IClaudeRunner Claude
    {
        get => claude ??= new ClaudeRunner(Queue);
        init => claude = value;
    }

    public JobQueue Queue { get; init; } = JobQueue.Default;
    public Action<string, IReadOnlyList<string>> Notify { get; init; } = Toast.Notify;
    public Func<DateTime> Today { get; init; } = () => DateTime.Today;

    /// <summary>meeting-summaries.exe, the console program: what a queued draft runs (with no window).</summary>
    public string Exe { get; init; } = Path.Combine(AppContext.BaseDirectory, "meeting-summaries.exe");

    /// <summary>meeting-summaries-background.exe, the windowless twin: what the hook starts to work through the
    /// queue, and what the autostart task runs.</summary>
    public string BackgroundExe { get; init; } = Path.Combine(AppContext.BaseDirectory, "meeting-summaries-background.exe");

    /// <summary>The command line that drafts a transcript later, from the queue.</summary>
    public IReadOnlyList<string> DraftCommand(Config config, string transcript) =>
        [Exe, "--config", config.Path, "draft", transcript];

    /// <summary>Starts the queue runner in the background and returns at once (a second runner just exits).</summary>
    public Action StartQueueRunner { get; init; } = () => { };

    /// <summary>Runs after_draft with the report on stdin.</summary>
    public Func<IReadOnlyList<string>, string, string, Completed> RunAfterDraft { get; init; } = AfterDraft;

    /// <summary>The settings for claude-headless's own commands (run-queue, autostart), passed through.</summary>
    public HeadlessHost Headless() => new()
    {
        Out = Out,
        Err = Err,
        LogEcho = LogEcho,
        Queue = Queue,
        BackgroundCommand = () => (BackgroundExe, []),
    };

    /// <summary>The real queue runner: the windowless twin's run-queue, detached.</summary>
    public static Action Detached(string backgroundExe) => () =>
    {
        var info = new ProcessStartInfo(backgroundExe) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("run-queue");
        using var _ = Process.Start(info);
    };

    private static Completed AfterDraft(IReadOnlyList<string> argv, string input, string cwd)
    {
        var info = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in argv.Skip(1))
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {argv[0]}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Command {string.Join(' ', argv)} timed out after 300 seconds");
        }
        process.WaitForExit();
        return new Completed(process.ExitCode, output.Result.Replace("\r\n", "\n"), error.Result.Replace("\r\n", "\n"));
    }
}
