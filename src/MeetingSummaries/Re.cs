using System.Text;

namespace MeetingSummaries;

/// <summary>Python's re module, where .NET's regular expressions differ: \b and \w (Python's word characters are
/// letters, numbers and the underscore; .NET's add combining marks and connector punctuation) and \s (Python's
/// whitespace includes U+001C to U+001F; .NET's doesn't).</summary>
public static class Re
{
    /// <summary>A Python word character.</summary>
    public const string W = @"[\p{L}\p{N}_]";

    /// <summary>Python's \b.</summary>
    public const string B = @"(?:(?<=" + W + ")(?!" + W + ")|(?<!" + W + ")(?=" + W + "))";

    /// <summary>Python's \s.</summary>
    public const string S = @"[\s\x1C\x1D\x1E\x1F]";

    /// <summary>\b(inner)\b.</summary>
    public static string Word(string inner) => B + inner + B;

    /// <summary>str.title(): the first letter of each run of letters upper case, the rest lower.</summary>
    public static string Title(string text)
    {
        var result = new StringBuilder(text.Length);
        var previousCased = false;
        foreach (var c in text)
        {
            var d = previousCased ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
            result.Append(d);
            previousCased = char.IsUpper(d) || char.IsLower(d) || char.GetUnicodeCategory(d) == System.Globalization.UnicodeCategory.TitlecaseLetter;
        }
        return result.ToString();
    }
}
