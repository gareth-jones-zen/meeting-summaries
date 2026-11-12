using System.Text.RegularExpressions;
using PyCompat;

namespace MeetingSummaries;

/// <summary>Finding instructions dictated to an assistant in a meeting: "Claude, add an item for me to ...".
///
/// People who use an assistant learn to say things like that in meetings, and reasonably assume they're picked up.
/// In the EA repo they weren't, at first: an audit found nine dictated instructions in the transcripts, of which
/// three had reached the to-do list (September 2026).
///
/// **The sweep is deliberately wide, so a person judges every candidate.** Speech-to-text mangles both halves:
/// the name ("Claude" comes through as Clark, Cloud, Clawed, Clyde) and, worse, the verb ("Claude, add a note"
/// became "Claude had a note" and "Claude, I didn't add a note"), so a candidate can never be judged on whether it
/// parses as an instruction. Speaker headings ("**Clark Kent** 0:11") are skipped.</summary>
public static class Dictation
{
    public static readonly IReadOnlyList<string> Names = ["claude", "clark", "cloud", "clawed", "claw", "clyde"];

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Action = new(Re.Word("(add|added|had|note|item|action|remind|inbox|please)"), Options);

    // Where the item should go. The name plus a destination catches a mangled verb: on 10 Nov 2026, "Claude, I did
    // items in my one to one with Dan" and "Claude, I don't have one to one with John" were missed by the verbs
    // above. Across 326 transcripts it added those two and ten false positives.
    private static readonly Regex Destination = new(Re.Word("(one[ -]to[ -]one|1[ :-]2[ :-]1|1:1|inbox|items|notes|actions|prep)"), Options);

    private static readonly Regex Phrase = new("add an? (item|note|action)|note to my action|my inbox|just started doing this", Options);

    /// <summary>A candidate: its line number (or "file:line" across a split meeting's parts) and its text.</summary>
    public sealed record Found(string Line, string Text)
    {
        public bool IsNumber => int.TryParse(Line, out _);
    }

    /// <summary>[(line number, text)] for each line that might be a dictated instruction. names: [] turns it off.</summary>
    public static List<Found> Find(string transcript, IReadOnlyList<string>? names = null)
    {
        names ??= Names;
        if (names.Count == 0)
            return [];
        var trigger = new Regex(Re.Word("(" + string.Join("|", names.Select(Regex.Escape)) + ")"), Options);
        var found = new List<Found>();
        var number = 0;
        foreach (var line in PyStr.SplitLines(transcript))
        {
            number++;
            if (line.StartsWith("**", StringComparison.Ordinal))
                continue;
            if ((trigger.IsMatch(line) && (Action.IsMatch(line) || Destination.IsMatch(line))) || Phrase.IsMatch(line))
                found.Add(new Found(number.ToString(System.Globalization.CultureInfo.InvariantCulture), PyStr.Head(PyStr.Strip(line), 200)));
        }
        return found;
    }

    /// <summary>The "For review" bullet: a question for the owner, since only they know what they meant.</summary>
    public static string Bullet(IReadOnlyList<Found> found, string owner, IReadOnlyList<string> names)
    {
        if (found.Count == 0)
            return "";
        var lines = new List<string>
        {
            $"- **[{owner}] Dictated instructions?** {found.Count} line{(found.Count > 1 ? "s" : "")} in the transcript " +
            $"may be addressed to {Re.Title(names[0])}. Were they meant as instructions, and are they done?",
        };
        lines.AddRange(found.Select(f => $"  - line {f.Line}: *{f.Text}*"));
        return string.Join("\n", lines) + "\n";
    }
}
