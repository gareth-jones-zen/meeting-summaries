using System.Text.RegularExpressions;
using PyCompat;

namespace MeetingSummaries;

/// <summary>Check a summary's quotes against its transcript.
///
///     meeting-summaries check summaries/2026-11-06.md
///
/// Every quotation in the summary (outside the frontmatter and the Transcript notes, which quote manglings on
/// purpose) is looked for in the transcript, ignoring case, punctuation, timestamps and speaker labels. One that
/// isn't there word for word is reported, with the nearest wording the transcript has, if under 75% of its words
/// appear there in order, or if it has a negation ("not", "never", "doesn't"...) the transcript's wording doesn't.
///
/// Built 9 Nov 2026, after a review of 13 drafts: about 750 quotes, of which four didn't say what the transcript
/// says (a "not" added, two paraphrases in quotation marks, a bleeped word cleaned up). Every draft is checked, and
/// the result goes at the top of "For review".</summary>
public static class QuoteCheck
{
    /// <summary>3 of 4 words is a misheard word, not a misquote.</summary>
    public const double Threshold = 0.75;

    private const RegexOptions Options = RegexOptions.CultureInvariant;

    // Python's \s (PyCompat's whitespace class: .NET's, plus U+001C to U+001F).
    private const string S = "[" + PyStr.SpaceClass + "]";

    // ["“]([^"”\n*]{15,}?)["”], counting code points as Python does, so an emoji is one character.
    private static readonly Regex Quote = new(@"[""“]((?:[^""”\n*\uD800-\uDFFF]|[\uD800-\uDBFF][\uDC00-\uDFFF]){15,}?)[""”]", Options);

    // An ellipsis or [inserted words] joins fragments.
    private static readonly Regex Split = new(@"…|\.\.\.|\[[^\]]*\]", Options);

    private static readonly Regex NotWord = new("[^a-z0-9 ]+", Options);
    private static readonly Regex Spaces = new(S + "+", Options);
    private static readonly Regex Label = new(@"\*\*[^*\n]+\*\*" + S + @"+\d+:\d+(?::\d+)?", Options);
    private static readonly Regex Stamp = new(@"\d+:\d+(?::\d+)?", Options);
    private static readonly Regex Frontmatter = new(@"\A---\n.*?\n---\n", Options | RegexOptions.Singleline);
    private static readonly Regex TranscriptNotes = new("^## Transcript notes", Options | RegexOptions.Multiline);
    private static readonly Regex TranscriptField = new(@"^transcript:" + S + "*(.+)$", Options | RegexOptions.Multiline);
    private static readonly Regex MdName = new(@"[^\[\],]+\.md", Options);

    public static readonly IReadOnlySet<string> Negations = new HashSet<string>(StringComparer.Ordinal)
    {
        "not", "no", "never", "nobody", "nobodys", "none", "nothing", "noone", "nor", "cant", "cannot",
        "dont", "doesnt", "didnt", "wont", "wouldnt", "isnt", "arent", "wasnt", "werent", "havent",
        "hasnt", "hadnt", "shouldnt", "couldnt", "unrelated",
    };

    /// <summary>A quote to verify: the quote, why, and the transcript's nearest wording.</summary>
    public sealed record Doubt(string Quote, string Why, string Found);

    /// <summary>Lower case, apostrophes dropped (so "em's" and "EMs" agree), punctuation to spaces.</summary>
    public static string Norm(string text)
    {
        text = PyStr.Lower(text).Replace("’", "").Replace("‘", "").Replace("'", "");
        text = NotWord.Replace(text, " ");
        return PyStr.Strip(Spaces.Replace(text, " "));
    }

    /// <summary>The transcript as one stream of words: timestamps and the bold speaker labels go.</summary>
    public static string Flatten(string transcript) => Norm(Stamp.Replace(Label.Replace(transcript, " "), " "));

    /// <summary>The part of a summary whose quotes should be the speakers' words.</summary>
    public static string Body(string summary) => TranscriptNotes.Split(Frontmatter.Replace(summary, "", 1))[0];

    /// <summary>The transcript passage that best contains the fragment, as (the share of the fragment's words found
    /// there in order, the fragment's words that weren't, the word just before the passage, the passage). The
    /// passage may have a few extra words, so a stutter, a filler or the other speaker's "yeah" doesn't count against
    /// the quote.</summary>
    public static (double Recall, List<string> Missing, string Before, List<string> Span) Nearest(IReadOnlyList<string> fragment, IReadOnlyList<string> words)
    {
        var n = fragment.Count;
        var first = new HashSet<string>(fragment.Take(3), StringComparer.Ordinal);
        double best = 0;
        var missing = fragment.ToList();
        var before = "";
        var span = new List<string>();
        if (words.Count == 0)
            return (best, missing, before, span);
        for (var i = 0; i < Math.Max(1, words.Count - n + 1); i++)
        {
            if (!first.Contains(words[i]) && (i + 1 >= words.Count || !first.Contains(words[i + 1])))
                continue;
            var window = words.Skip(i).Take(n + 4).ToList();
            var blocks = SequenceMatcher.MatchingBlocks(fragment, window).Where(b => b.Size > 0).ToList();
            var recall = (double)blocks.Sum(b => b.Size) / n;
            if (recall > best)
            {
                var matched = new HashSet<int>(blocks.SelectMany(b => Enumerable.Range(b.A, b.Size)));
                int lo = i + blocks[0].B, hi = i + blocks[^1].B + blocks[^1].Size;
                best = recall;
                span = words.Skip(lo).Take(hi - lo).ToList();
                missing = fragment.Where((_, k) => !matched.Contains(k)).ToList();
                before = lo > 0 ? words[lo - 1] : "";
            }
        }
        return (best, missing, before, span);
    }

    /// <summary>(how many quotes were checked, the ones to verify).
    ///
    /// A quote is reported if under 75% of its words are found in order, or if it has a negation ("not", "never",
    /// "nobody", "doesn't"...) that the transcript's wording doesn't: an added "not" reads fine and inverts the
    /// meaning (9 Nov 2026: "It's that complex. I'm not worried" became "It's not that complex").</summary>
    public static (int Count, List<Doubt> Doubtful) Check(string summary, string transcript)
    {
        var flat = Flatten(transcript);
        var words = Words(flat);
        var quotes = Quote.Matches(Body(summary)).Select(m => m.Groups[1].Value).ToList();
        var doubtful = new List<Doubt>();
        foreach (var quote in quotes)
        {
            foreach (var piece in Split.Split(quote))
            {
                var fragment = Words(Norm(piece));
                if (fragment.Count < 4 || flat.Contains(string.Join(' ', fragment), StringComparison.Ordinal))
                    continue;
                var (recall, missing, before, span) = Nearest(fragment, words);
                var found = string.Join(' ', span);
                // A negation in the quote that the transcript doesn't have, unless the word just before the passage
                // is one ("not the whole" for "wasn't the whole").
                if (missing.Any(Negations.Contains) && !Negations.Contains(before))
                {
                    doubtful.Add(new Doubt(quote, "it has a negation the transcript doesn't", found));
                    break;
                }
                if (recall < Threshold)
                {
                    doubtful.Add(new Doubt(quote, $"{Py.Fixed(recall * 100, 0)}% of its words found", found));
                    break;
                }
            }
        }
        return (quotes.Count, doubtful);
    }

    // str.split() on text that Norm has left with single spaces only.
    private static List<string> Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The transcripts a summary's transcript: field names, read and joined (each followed by "\n").
    /// Throws ArgumentException where it has no such field.</summary>
    public static string TranscriptsFor(string summaryPath, string summary)
    {
        var field = TranscriptField.Match(summary);
        if (!field.Success)
            throw new ArgumentException($"{PyPath.Pure(summaryPath)} has no transcript: field");
        var text = new System.Text.StringBuilder();
        foreach (Match name in MdName.Matches(field.Groups[1].Value))
        {
            var path = PyPath.Resolve(Path.Combine(Paths.Parent(summaryPath), PyStr.Strip(name.Value).Trim('\'', '"')));
            text.Append(PyStr.ReadText(path)).Append('\n');
        }
        return text.ToString();
    }
}
