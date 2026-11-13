using System.Text;
using System.Text.RegularExpressions;
using PyCompat;

namespace MeetingSummaries;

/// <summary>Paths as Python's pathlib handled them on Windows, where the difference shows and PyCompat has nothing:
/// name, stem and suffix, relative_to, and fnmatch. (resolve() is PyCompat's PyPath.Resolve.)</summary>
public static class Paths
{
    /// <summary>PurePath.name.</summary>
    public static string Name(string path) => Path.GetFileName(PyPath.Pure(path));

    /// <summary>PurePath.suffix: from the last dot of the name, unless the name starts with it or ends with it.</summary>
    public static string Suffix(string path)
    {
        var name = Name(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[dot..] : "";
    }

    /// <summary>PurePath.stem: the name without its suffix.</summary>
    public static string Stem(string path)
    {
        var name = Name(path);
        var suffix = Suffix(path);
        return suffix.Length == 0 ? name : name[..^suffix.Length];
    }

    /// <summary>PurePath.parent.</summary>
    public static string Parent(string path) => Path.GetDirectoryName(PyPath.Pure(path)) ?? PyPath.Pure(path);

    /// <summary>PurePath.relative_to(root), as a posix path; null where Python raises ValueError. Windows paths
    /// compare ignoring case, and the result keeps the path's own case.</summary>
    public static string? RelativeTo(string path, string root)
    {
        var p = PyPath.Pure(path);
        var r = PyPath.Pure(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
            return ".";
        var prefix = r.EndsWith('\\') ? r : r + "\\";
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return p[prefix.Length..].Replace('\\', '/');
    }

    /// <summary>fnmatch.fnmatch on Windows: both sides lower-cased with "/" as "\", then "*" matches anything
    /// (separators too), "?" one character, and [seq] / [!seq] a set.</summary>
    public static bool FnMatch(string name, string pattern) =>
        Regex.IsMatch(NormCase(name), Translate(NormCase(pattern)), RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string NormCase(string text) => PyStr.Lower(text).Replace('/', '\\');

    /// <summary>fnmatch.translate, as a .NET pattern matching the whole name.</summary>
    public static string Translate(string pattern)
    {
        var result = new StringBuilder("^");
        var i = 0;
        var n = pattern.Length;
        while (i < n)
        {
            var c = pattern[i++];
            if (c == '*')
            {
                while (i < n && pattern[i] == '*')
                    i++;
                result.Append(".*");
            }
            else if (c == '?')
                result.Append('.');
            else if (c == '[')
            {
                var j = i;
                if (j < n && pattern[j] == '!')
                    j++;
                if (j < n && pattern[j] == ']')
                    j++;
                while (j < n && pattern[j] != ']')
                    j++;
                if (j >= n)
                {
                    result.Append(@"\[");
                    continue;
                }
                var stuff = pattern[i..j];
                i = j + 1;
                if (stuff.Length == 0)
                    result.Append("(?!)");
                else if (stuff == "!")
                    result.Append('.');
                else
                {
                    var set = new StringBuilder("[");
                    var k = 0;
                    if (stuff[0] == '!')
                    {
                        set.Append('^');
                        k = 1;
                    }
                    for (; k < stuff.Length; k++)
                    {
                        var s = stuff[k];
                        if (s == '-' && k > 0 && k < stuff.Length - 1 && !(k == 1 && stuff[0] == '!'))
                            set.Append('-');
                        else if (s is '\\' or '[' or ']' or '^' or '-')
                            set.Append('\\').Append(s);
                        else
                            set.Append(s);
                    }
                    result.Append(set.Append(']'));
                }
            }
            else
                result.Append(Regex.Escape(c.ToString()));
        }
        return result.Append(@"\z").ToString();
    }
}
