using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using PyCompat;

namespace MeetingSummaries;

/// <summary>Paths as Python's pathlib handled them on Windows, where the difference shows: resolve() (the real path,
/// with the case the folder has on disk), name, stem and suffix, relative_to, and fnmatch.</summary>
public static partial class Paths
{
    /// <summary>Path.resolve(): absolute, "." and ".." gone, links followed and the case as it is on disk, as
    /// ntpath.realpath gives it. A path that doesn't exist yet is resolved as far as it does, and the rest kept.</summary>
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path.Replace('/', '\\'));
        var tail = new List<string>();
        var current = full;
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && FinalPath(current) is { } real)
            {
                tail.Reverse();
                return PyPath.Pure(tail.Count == 0 ? real : Path.Combine([real, .. tail]));
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
                return PyPath.Pure(full);
            tail.Add(Path.GetFileName(current));
            current = parent;
        }
    }

    private static unsafe string? FinalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;
        // FILE_FLAG_BACKUP_SEMANTICS opens a folder too; no access is needed to ask for the name.
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x02000000, 0);
        if (handle.IsInvalid)
            return null;
        const int size = 1024;
        var buffer = stackalloc char[size];
        var length = GetFinalPathNameByHandleW(handle, buffer, size, 0);
        if (length == 0 || length >= size)
            return null;
        var text = new string(buffer, 0, (int)length);
        if (text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + text[8..];
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
            return text[4..];
        return text;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle handle, char* buffer, uint size, uint flags);

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

    private static string NormCase(string text) => Lower(text).Replace('/', '\\');

    /// <summary>str.lower(): .NET's invariant lower case, except U+0130, which Python lowers to two characters.</summary>
    public static string Lower(string text)
    {
        return text.Contains('\u0130') ? text.Replace("\u0130", "i\u0307").ToLowerInvariant() : text.ToLowerInvariant();
    }

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
