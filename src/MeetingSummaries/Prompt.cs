using System.Text.RegularExpressions;
using PyCompat;

namespace MeetingSummaries;

/// <summary>The drafting prompt: the tool's own rules, then the settings' house rules, which win where they differ.
/// Byte for byte the prompt the Python version sent.</summary>
public static partial class Prompt
{
    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    // str.format with named fields, in one pass, so a value containing "{owner}" is left alone.
    private static string Format(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template.Replace("\r\n", "\n"), m => values[m.Groups[1].Value]);

    /// <summary>The prompt for a meeting: transcripts relative to the workspace (posix), and the folder of earlier
    /// summaries (relative, ending "/"), if there is one.</summary>
    public static string Build(Config config, IReadOnlyList<string> transcripts, string? previousFolder)
    {
        var names = transcripts.Select(p => p[(p.LastIndexOf('/') + 1)..]).ToList();
        var text = Format(PromptText.Core, new Dictionary<string, string>
        {
            ["owner"] = config.Owner,
            ["paths"] = string.Join(", ", transcripts),
            // Python formats a list here for a split meeting: "['x-part1.md', 'x-part2.md']".
            ["name"] = names.Count == 1 ? names[0] : "[" + string.Join(", ", names.Select(Py.ReprString)) + "]",
            ["previous"] = previousFolder is not null
                ? $"Also read the two most recent summaries in {previousFolder}, if any, for format and context.\n"
                : "",
            ["template"] = config.TemplateFile is { } template
                ? $"Follow the template {config.Relative(template)} for the summary's shape.\n"
                : "",
            ["context"] = config.Context.Count > 0 ? $"Also read, for who people are: {string.Join(", ", config.Context)}.\n" : "",
            ["ledger"] = config.Ledger is { } ledger
                ? Format(PromptText.Ledger, new Dictionary<string, string> { ["owner"] = config.Owner, ["ledger"] = config.Relative(ledger) })
                : "",
            ["style"] = config.Style.Length > 0 ? $"\nHouse rules. Where they differ from the above, they win:\n\n{PyStr.Strip(config.Style)}\n" : "",
        });
        if (transcripts.Count > 1)
            text += "\n\nThis meeting was split across recordings. Read every part; write one summary for the whole " +
                    "meeting, with transcript: [" + string.Join(", ", names.Select(n => $"../{n}")) + "] in the frontmatter.";
        return text;
    }
}
