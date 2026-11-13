using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHeadless;
using PyCompat;

namespace MeetingSummaries;

/// <summary>Drafting one meeting's summary.
///
/// - **The model only reads.** Claude Code runs headless in the workspace with Read, Grep and Glob, and no
///   connectors; this writes the file.
/// - **A split meeting is one meeting.** `x-part1.md`, `x-part2.md` get one summary, `summaries/x.md`, from every
///   part; a later part's draft waits for the first.
/// - **A summary that already exists is never overwritten**, draft or finished.
/// - **Every quote is checked against the transcript** (QuoteCheck), and the result goes at the top of "For
///   review", so the reviewer checks only the quotes that need it.
/// - **Lines that may be instructions dictated to an assistant** ("Claude, add an item...") are put to the owner at
///   the top of "For review" (Dictation): a person judges every one.
/// - **Questions for the owner go in the ledger**, if there is one, so the next draft doesn't ask them again.
/// - **At the usage limit the draft is queued** (ClaudeHeadless) and notified once per limit.</summary>
public static partial class Draft
{
    [GeneratedRegex(@"^(?<base>.+)-part(?<n>\d+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Part();

    [GeneratedRegex(@"^## For review[ \t]*\n", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ReviewHeading();

    [GeneratedRegex(@"^## For review[ \t]*\n\n?", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ReviewHeadingAndGap();

    [GeneratedRegex(@"^- \*\*\[Noted\]|^## ", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex AfterQuestions();

    [GeneratedRegex(@"^## For review[ \t]*\n(.*?)(?=^## )", RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex ReviewSection();

    // Python's \s (PyCompat's whitespace class: .NET's, plus U+001C to U+001F).
    private const string S = "[" + PyStr.SpaceClass + "]";

    private static readonly Regex Fences = new(@"^```(?:markdown|md)?" + S + @"*\n|\n```" + S + "*$", RegexOptions.CultureInvariant);

    /// <summary>Every part of a split meeting, in order (just the transcript if it isn't one).</summary>
    public static List<string> Parts(string transcript)
    {
        var match = Part().Match(Paths.Stem(transcript));
        if (!match.Success)
            return [transcript];
        var folder = Paths.Parent(transcript);
        var suffix = Paths.Suffix(transcript);
        var prefix = match.Groups["base"].Value + "-part";
        var found = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder)
                .Where(p => Paths.Name(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && Paths.Name(p).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                            && Paths.Name(p).Length >= prefix.Length + suffix.Length)
                .Select(p => (Path: PyPath.Pure(p), Match: Part().Match(Paths.Stem(p))))
                .Where(p => p.Match.Success)
                .OrderBy(p => Paths.Name(p.Path), StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => int.Parse(p.Match.Groups["n"].Value, CultureInfo.InvariantCulture))
                .Select(p => p.Path)
                .ToList()
            : [];
        return found.Count > 0 ? found : [transcript];
    }

    /// <summary>Where a transcript's summary goes: summaries/ beside it, named for the meeting (without -partN).</summary>
    public static string SummaryPath(Config config, string transcript)
    {
        var match = Part().Match(Paths.Stem(transcript));
        var name = match.Success ? match.Groups["base"].Value + ".md" : Paths.Stem(transcript) + ".md";
        return Path.Combine(Paths.Parent(transcript), config.Summaries, name);
    }

    /// <summary>The draft with the quote check's [Check] bullet after the owner's questions in "For review".</summary>
    public static string WithQuoteCheck(string text, IReadOnlyList<string> transcripts)
    {
        string bullet;
        try
        {
            var source = string.Join("\n", transcripts.Select(PyStr.ReadText));
            var (count, doubtful) = QuoteCheck.Check(text, source);
            if (doubtful.Count == 0)
                bullet = $"- **[Check] Quotes:** all {count} match the transcript.\n";
            else
            {
                var lines = new List<string>
                {
                    $"- **[Check] {doubtful.Count} of {count} quotes to verify against the transcript.** " +
                    "Fix the wording, or take the quotation marks off a paraphrase:",
                };
                lines.AddRange(doubtful.Select(d => $"  - *\"{d.Quote}\"*: {d.Why}. The transcript has *{d.Found}*."));
                bullet = string.Join("\n", lines) + "\n";
            }
        }
        catch (Exception error)
        {
            bullet = $"- **[Check] Quotes weren't checked:** the quote check failed ({error.Message}).\n";
        }
        var heading = ReviewHeading().Match(text);
        if (!heading.Success)
            return ReplaceFirst(text, "\n## Purpose", $"\n## For review\n\n{bullet}\n## Purpose");
        var end = heading.Index + heading.Length;
        var after = AfterQuestions().Match(text[end..]);
        var at = after.Success ? end + after.Index : text.Length;
        if (text[at..].StartsWith("## ", StringComparison.Ordinal))
            at = text[..at].TrimEnd('\n').Length + 1;
        return text[..at] + bullet + text[at..];
    }

    /// <summary>(the draft with a dictation bullet first in "For review", the candidates).</summary>
    public static (string Text, List<Dictation.Found> Found) WithDictation(string text, IReadOnlyList<string> transcripts, Config config)
    {
        var names = config.Dictation ?? Dictation.Names;
        var found = transcripts.SelectMany(p => Dictation.Find(PyStr.ReadText(p), names)
                .Select(f => transcripts.Count > 1 ? f with { Line = $"{Paths.Name(p)}:{f.Line}" } : f))
            .ToList();
        var bullet = Dictation.Bullet(found, config.Owner, names);
        if (bullet.Length == 0)
            return (text, found);
        var heading = ReviewHeadingAndGap().Match(text);
        if (!heading.Success)
            return (ReplaceFirst(text, "\n## Purpose", $"\n## For review\n\n{bullet}\n## Purpose"), found);
        var end = heading.Index + heading.Length;
        return (text[..end] + bullet + text[end..], found);
    }

    /// <summary>The owner's questions in "For review".</summary>
    public static List<string> Questions(string text, string owner)
    {
        var section = ReviewSection().Match(text);
        var label = $"- **[{owner}]**";
        return (section.Success ? PyStr.SplitLines(section.Groups[1].Value) : [])
            .Where(line => line.StartsWith(label, StringComparison.Ordinal))
            .Select(line => PyStr.Strip(line[label.Length..]))
            .ToList();
    }

    /// <summary>The draft's owner questions under Open in the ledger (it must have a "## Settled" heading after
    /// Open). Returns how many were added.</summary>
    public static int AddToLedger(Config config, string text, string summary, DateTime today)
    {
        var asked = Questions(text, config.Owner);
        if (asked.Count == 0 || config.Ledger is null || !File.Exists(config.Ledger))
            return 0;
        var ledger = PyStr.ReadText(config.Ledger);
        var cut = ledger.IndexOf("\n## Settled", StringComparison.Ordinal);
        if (cut < 0)
            return 0;
        var rel = Paths.RelativeTo(summary, Paths.Parent(config.Ledger)) ?? config.Relative(summary);
        var name = $"{Paths.Name(Paths.Parent(Paths.Parent(summary)))} {Paths.Stem(summary)}";
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var lines = string.Concat(asked.Where(q => !ledger.Contains(q, StringComparison.Ordinal))
            .Select(q => $"- {date} · [{name}]({rel}) · {q}\n"));
        if (lines.Length == 0)
            return 0;
        var head = ledger[..cut];
        var tail = ledger[(cut + "\n## Settled".Length)..];
        PyStr.WriteText(config.Ledger, head.TrimEnd('\n') + "\n\n" + lines + "\n## Settled" + tail);
        return lines.Count(c => c == '\n');
    }

    private static string ReplaceFirst(string text, string old, string replacement)
    {
        var at = text.IndexOf(old, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + replacement + text[(at + old.Length)..];
    }

    /// <summary>Draft and write. Returns "written", "exists", "waiting" (a later part), "queued" or "limit" (usage
    /// limit). out: write there instead, for comparing and testing: no ledger, no after_draft, never queued.</summary>
    public static string WriteDraft(Config config, string transcript, Action<string> log, SummariesHost host, string? output = null)
    {
        transcript = PyPath.Resolve(transcript);
        var every = Parts(transcript);
        if (output is null && !string.Equals(every[0], transcript, StringComparison.OrdinalIgnoreCase) && File.Exists(every[0]))
        {
            host.Queue.Add(host.DraftCommand(config, every[0]), config.Workspace);
            log($"{config.Relative(transcript)}: part of a split meeting; drafted with {Paths.Name(every[0])}");
            return "waiting";
        }
        var target = output ?? SummaryPath(config, transcript);
        if (output is null && File.Exists(target))
        {
            log($"{config.Relative(transcript)}: a summary already exists ({config.Relative(target)}), not overwriting");
            return "exists";
        }
        var rel = every.Select(config.Relative).ToList();
        var folder = Paths.Parent(SummaryPath(config, transcript));
        var previous = Directory.Exists(folder) || File.Exists(folder) ? config.Relative(folder) + "/" : null;
        var prompt = Prompt.Build(config, rel, previous);
        string[] command = ["-p", "--output-format", "json", "--model", config.Model,
            "--tools", "Read,Grep,Glob", "--strict-mcp-config", "--permission-mode", "dontAsk"];
        JsonObject result;
        try
        {
            result = host.Claude.Run(command, prompt, config.Workspace, TimeSpan.FromMinutes(config.TimeoutMinutes));
        }
        catch (LimitReachedException limit)
        {
            var at = limit.Reset.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (output is not null)
            {
                log($"{rel[0]}: Claude's usage limit, resets {at}");
                return "limit";
            }
            if (host.Queue.Retry(host.DraftCommand(config, transcript), config.Workspace, limit.Reset))
                host.Notify("Claude usage limit reached", [$"Summary drafts will carry on after {at}."]);
            log($"{rel[0]}: Claude's usage limit; queued for after {PyTime.Day(limit.Reset)} {at}");
            return "queued";
        }
        var reply = result["result"] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : throw new ClaudeRunException("the reply had no text result");
        var text = Fences.Replace(PyStr.Strip(reply), "");
        var start = text.IndexOf("---", StringComparison.Ordinal);
        if (start < 0 || !text.Contains("# [DRAFT]", StringComparison.Ordinal))
            throw new ClaudeRunException("the reply wasn't a draft summary: " + PyStr.Head(text, 200));
        text = WithQuoteCheck(PyStr.RStrip(text[start..]) + "\n", every);
        (text, var dictated) = WithDictation(text, every, config);
        Directory.CreateDirectory(Paths.Parent(target));
        PyStr.WriteText(target, text);
        var added = output is not null ? 0 : AddToLedger(config, text, target, host.Today());
        log($"{rel[0]}: draft written to {(output is not null ? PyPath.Pure(target) : config.Relative(target))} ({Py.Str(result["num_turns"])} turns"
            + (added > 0 ? $"; {added} question(s) added to the ledger" : "") + ")");
        if (config.AfterDraft.Count > 0 && output is null)
        {
            var report = new JsonObject
            {
                ["transcript"] = rel[0],
                ["transcripts"] = new JsonArray(rel.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["summary"] = config.Relative(target),
                ["questions"] = new JsonArray(Questions(text, config.Owner).Select(q => (JsonNode?)JsonValue.Create(q)).ToArray()),
                ["session"] = result["session_id"]?.DeepClone(),
                ["dictation"] = new JsonArray(dictated.Select(d => (JsonNode?)new JsonObject
                {
                    ["line"] = d.IsNumber ? PyJson.Int(long.Parse(d.Line, CultureInfo.InvariantCulture)) : JsonValue.Create(d.Line),
                    ["text"] = d.Text,
                }).ToArray()),
            };
            try
            {
                var done = host.RunAfterDraft(config.AfterDraft, PyJson.Dump(report), Paths.Parent(config.Path));
                if (done.ExitCode != 0)
                    log($"  after_draft exited {done.ExitCode}: {PyStr.Tail(done.StandardError.Length > 0 ? done.StandardError : done.StandardOutput, 300)}");
            }
            catch (Exception error)
            {
                log($"  after_draft failed: {error.Message}");
            }
        }
        return "written";
    }
}
