using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHeadless;
using PyCompat;
using WinBackground;

namespace MeetingSummaries;

/// <summary>meeting-summaries: a draft summary of each meeting transcript, written by Claude Code for you to review.
///
///     meeting-summaries config                 create the settings file (if missing) and say where it is
///     meeting-summaries draft &lt;transcript&gt;     draft one, into summaries/ beside it (--out to write elsewhere)
///     meeting-summaries hook                   for teams-transcripts' after_run: queue a draft of each new
///                                              transcript, then start working through the queue
///     meeting-summaries check &lt;summary&gt;...     check a summary's quotes against its transcript
///     meeting-summaries dictation &lt;transcript&gt;...   lines that may be instructions dictated to an assistant
///     meeting-summaries autostart on           work through queued drafts every 15 minutes (off, status)
///     meeting-summaries run-queue              work through queued drafts now (what the hook and the task run)</summary>
public static class Cli
{
    /// <summary>From the build (Directory.Build.props, or the release's -p:Version from its tag).</summary>
    public static readonly string Version = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Cli).Assembly)?.InformationalVersion ?? "unknown";

    public const string Usage = """
        usage: meeting-summaries [-h] [--version] [--config CONFIG] {config,draft,hook,check,dictation,autostart,run-queue} ...

        meeting-summaries: a draft summary of each meeting transcript, written by Claude Code for you to review.

            meeting-summaries config                 create the settings file (if missing) and say where it is
            meeting-summaries draft <transcript>     draft one, into summaries/ beside it (--out to write elsewhere)
            meeting-summaries hook                   for teams-transcripts' after_run: queue a draft of each new
                                                     transcript, then start working through the queue
            meeting-summaries check <summary>...     check a summary's quotes against its transcript
            meeting-summaries dictation <transcript>...   lines that may be instructions dictated to an assistant
            meeting-summaries autostart on           work through queued drafts every 15 minutes (off, status)
            meeting-summaries run-queue              work through queued drafts now
        """;

    private static int Bad(SummariesHost host, string message)
    {
        host.Err.WriteLine("usage: meeting-summaries [-h] [--version] [--config CONFIG] {config,draft,hook,check,dictation,autostart,run-queue} ...");
        host.Err.WriteLine($"meeting-summaries: error: {message}");
        return 2;
    }

    public static int Run(IReadOnlyList<string> args, SummariesHost host)
    {
        string? configPath = null;
        var i = 0;
        for (; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                host.Out.WriteLine(Usage);
                return 0;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return 0;
            }
            if (arg == "--config")
            {
                if (i + 1 >= args.Count)
                    return Bad(host, "argument --config: expected one argument");
                configPath = args[++i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                configPath = arg["--config=".Length..];
            else
                break;
        }
        if (i >= args.Count)
            return Bad(host, "the following arguments are required: command");
        var command = args[i];
        var rest = args.Skip(i + 1).ToList();
        switch (command)
        {
            case "config":
                if (rest.Count > 0)
                    return Bad(host, $"unrecognized arguments: {string.Join(' ', rest)}");
                host.Out.WriteLine(configPath ?? Config.CreateDefault(host.Env));
                return 0;
            case "check":
                return rest.Count == 0 ? Bad(host, "the following arguments are required: summaries") : CheckCommand(rest, host);
            case "dictation":
                return rest.Count == 0 ? Bad(host, "the following arguments are required: transcripts") : DictationCommand(rest, configPath, host);
            case "autostart":
                // The queue belongs to the seat, so it's claude-headless's task.
                if (rest.Count != 1 || rest[0] is not ("on" or "off" or "status"))
                    return Bad(host, "argument action: invalid choice (choose from 'on', 'off', 'status')");
                return HeadlessCli.Run(["autostart", rest[0]], host.Headless());
            case "run-queue":
                return rest.Count > 0 ? Bad(host, $"unrecognized arguments: {string.Join(' ', rest)}") : HeadlessCli.Run(["run-queue"], host.Headless());
            case "hook":
            case "draft":
                break;
            default:
                return Bad(host, $"argument command: invalid choice: '{command}' (choose from 'config', 'draft', 'hook', 'check', 'dictation', 'autostart', 'run-queue')");
        }

        string? transcriptArg = null, outArg = null;
        for (var k = 0; k < rest.Count; k++)
        {
            if (command == "draft" && rest[k] == "--out" && k + 1 < rest.Count)
                outArg = rest[++k];
            else if (command == "draft" && rest[k].StartsWith("--out=", StringComparison.Ordinal))
                outArg = rest[k]["--out=".Length..];
            else if (command == "draft" && transcriptArg is null && !rest[k].StartsWith('-'))
                transcriptArg = rest[k];
            else
                return Bad(host, $"unrecognized arguments: {string.Join(' ', rest.Skip(k))}");
        }
        if (command == "draft" && transcriptArg is null)
            return Bad(host, "the following arguments are required: transcript");

        Config config;
        try
        {
            config = Config.Load(configPath, host.Env);
        }
        catch (ConfigError error)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        var log = Logger.Create(config.LogFile, host.LogEcho, stampEcho: false);

        if (command == "hook")
            return Hook(config, log, host);
        var transcript = PyPath.Resolve(transcriptArg!);
        if (!File.Exists(transcript) && !Directory.Exists(transcript))
        {
            log($"no such transcript: {transcript}");
            return 2;
        }
        try
        {
            var status = Draft.WriteDraft(config, transcript, log, host, outArg is null ? null : PyPath.Resolve(outArg));
            return status == "limit" ? 1 : 0;
        }
        catch (Exception error)
        {
            log($"{Paths.Name(transcript)}: FAILED: {error.Message}");
            host.Notify("Summary draft failed", [Paths.Name(transcript), $"Log: {config.LogFile}"]);
            return 1;
        }
    }

    /// <summary>stdin: teams-transcripts' after_run report (a list of results with "path"), doc-filer's
    /// ({"written": [...]}), or a plain list of paths, one per line.</summary>
    private static int Hook(Config config, Action<string> log, SummariesHost host)
    {
        var raw = host.Stdin().ReadToEnd();
        JsonNode? data;
        try
        {
            data = PyStr.Strip(raw).Length > 0 ? PyJson.Parse(raw) : new JsonArray();
        }
        catch (JsonException)
        {
            data = new JsonArray(PyStr.SplitLines(raw).Select(PyStr.Strip).Where(l => l.Length > 0).Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
        }
        var items = data is JsonObject o ? o["written"] as JsonArray ?? [] : data as JsonArray ?? [];
        var paths = items.Select(item => item is JsonObject d
                ? FirstTruthy(d["path"], d["file"]) ?? ""
                : Py.Str(item))
            .Select(p => p.Length == 0 ? "." : p);
        var wanted = paths.Where(p => (File.Exists(p) || Directory.Exists(p)) && config.Wanted(p)).Select(PyPath.Resolve).ToList();
        foreach (var transcript in wanted)
        {
            host.Queue.Add(host.DraftCommand(config, transcript), config.Workspace);
            log($"queued a draft: {config.Relative(transcript)}");
        }
        if (wanted.Count > 0 || host.Queue.Pending().Count > 0)
            host.StartQueueRunner();   // in the background, so the hook returns at once; a second runner just exits
        return 0;
    }

    private static string? FirstTruthy(params JsonNode?[] values) =>
        values.FirstOrDefault(Py.Truthy) is { } v ? Py.Str(v) : null;

    private static int CheckCommand(List<string> paths, SummariesHost host)
    {
        var worst = 0;
        foreach (var given in paths)
        {
            var path = PyPath.Pure(given);
            try
            {
                var summary = PyStr.ReadText(path);
                var (count, doubtful) = QuoteCheck.Check(summary, QuoteCheck.TranscriptsFor(path, summary));
                host.Out.WriteLine($"{path}: {count} quotes, {doubtful.Count} to verify");
                foreach (var d in doubtful)
                {
                    host.Out.WriteLine($"  \"{d.Quote}\" ({d.Why})");
                    host.Out.WriteLine($"      transcript: {d.Found}");
                }
                worst = Math.Max(worst, doubtful.Count);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.DecoderFallbackException)
            {
                // Python stops here with a traceback; this says why, and stops too.
                host.Out.Flush();
                host.Err.WriteLine($"meeting-summaries: {path}: {error.Message}");
                return 1;
            }
        }
        return worst > 0 ? 1 : 0;
    }

    private static int DictationCommand(List<string> paths, string? configPath, SummariesHost host)
    {
        IReadOnlyList<string>? names;
        try
        {
            names = Config.Load(configPath, host.Env).Dictation;
        }
        catch (ConfigError)
        {
            names = null;
        }
        foreach (var given in paths)
        {
            var path = PyPath.Pure(given);
            List<Dictation.Found> found;
            try
            {
                found = Dictation.Find(PyStr.ReadText(path), names);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                host.Out.Flush();
                host.Err.WriteLine($"meeting-summaries: {path}: {error.Message}");
                return 1;
            }
            host.Out.WriteLine($"{path}: {found.Count} candidate(s)");
            foreach (var f in found)
                host.Out.WriteLine($"  {f.Line}: {f.Text}");
        }
        return 0;
    }
}
