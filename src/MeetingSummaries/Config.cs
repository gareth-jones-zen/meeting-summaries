using System.Text;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace MeetingSummaries;

/// <summary>The settings file is missing (Python's FileNotFoundError) or wrong (ValueError).</summary>
public sealed class ConfigError(string message, bool missing = false) : Exception(message)
{
    public bool Missing { get; } = missing;
}

/// <summary>The settings file.
///
///     workspace = "."                   # the folder Claude works in, read-only: your notes, or a repo
///     model = "opus"
///     owner = "You"                     # the label on "For review" questions only you can answer
///     template = "templates/summary.md" # optional: the summary's shape (relative to workspace)
///     style = "summary-style.md"        # optional: your house rules, read into the prompt (relative to this file)
///     context = ["people.md"]           # optional: files Claude reads for who people are (relative to workspace)
///     ledger = "review-ledger.md"       # optional: questions asked and answers given, so none is asked twice
///     include = ["meetings/**"]         # optional: which transcripts `hook` drafts (globs, relative to workspace)
///     exclude = ["**/one-to-ones/**"]   # optional: and which it never does
///     summaries = "summaries"           # the folder beside each transcript that its summary goes in
///     log_file = "..."                  # optional
///     after_draft = ["python", "x.py"]  # optional: run after each draft, with a JSON report on stdin
///     [review]                          # optional: note each draft against its transcript's item in a review queue
///     file = "%LOCALAPPDATA%/my-tools/review.json"
///     source = "transcripts"
///     dictation = ["claude", "clark"]   # optional: names an assistant is addressed by, and how transcripts
///                                       #   mishear them; lines dictated to it are put to you. [] turns it off
///     timeout_minutes = 30
///     hook_drafts = true                # optional: false, and `hook` queues no drafts (it still raises, below)
///     raise_transcripts = ["meetings/**"]   # optional (1.4.0, needs [review]): files `hook` is given that it raises
///                                       #   as "transcript" items, swept for dictation, with whether a draft is wanted
///     [[raise]]                         # optional: files raised as items of their own instead, first match wins
///     match = "**/review-form.md"       #   a glob, relative to workspace
///     type = "review-paperwork"         #   the item's type
///     sweep = false                     #   sweep it for dictation too
///     note = "Add the review's row."    #   optional: a note on the item
///     [[share]]                         # optional (1.5.0): post an opted-in meeting's transcript and summary to its
///     series = "^weekly-sync$"          #   Teams chat; series (its folder) or title, regular expressions
///     transcript = true                 #   post the transcript when the hook is given it
///     summary = true                    #   `share summary` may post the summary's attendee version
///     why = "..."                       #   (also organiser, key_points, file, external; see the README)
///     teams_post = ["teams-post.exe", "--config", "teams-post.toml"]   # needed with [[share]]
///     never_terms = ['\bsecret\b']      # optional: anything matching is never posted
///     org_domains = ["example.com"]     # an attendee outside these is external
///     share_name = "Alex"               # the summary's footer: "by Alex's assistant, checked by Alex"
///     login_hint, tenant, msal_cache, graph_scopes   # signing in to read the calendar (with [[share]] only)
///
/// Paths may use ~ and %VARIABLES%.</summary>
public sealed class Config
{
    /// <summary>Every top-level setting this file accepts (tables included): the ones a base file can supply.</summary>
    public static readonly string[] KnownKeys =
    [
        "workspace", "model", "owner", "template", "style", "context", "ledger", "include", "exclude", "summaries", "log_file",
        "after_draft", "review", "dictation", "timeout_minutes", "hook_drafts", "raise_transcripts", "raise",
        "share", "never_terms", "share_name", "org_domains", "teams_post", "share_state", "share_folder",
        "login_hint", "tenant", "msal_cache", "graph_scopes",
    ];

    public const string Template = """
        # meeting-summaries settings. See the README.
        # base = "base-config.toml"   # optional: settings shared with other tools
        # The folder holding your transcripts (teams-transcripts' output folder, say).
        workspace = "~/Documents/Meetings"
        owner = "You"

        """;

    public required string Path { get; init; }
    public required string Workspace { get; init; }
    public string Model { get; init; } = "opus";
    public string Owner { get; init; } = "You";
    public string? TemplateFile { get; init; }
    public string Style { get; init; } = "";
    public IReadOnlyList<string> Context { get; init; } = [];
    public string? Ledger { get; init; }
    public IReadOnlyList<string> Include { get; init; } = ["**"];
    public IReadOnlyList<string> Exclude { get; init; } = [];
    public string Summaries { get; init; } = "summaries";
    public required string LogFile { get; init; }
    public IReadOnlyList<string> AfterDraft { get; init; } = [];

    /// <summary>[review], or null: nothing noted. After each draft, the review queue's items for its transcripts get
    /// summary_draft.</summary>
    public ReviewQueue.ReviewSettings? Review { get; init; }
    public int TimeoutMinutes { get; init; } = 30;

    /// <summary>Whether `hook` queues drafts of the transcripts include wants (1.4.0: it can also only raise them).</summary>
    public bool HookDrafts { get; init; } = true;

    /// <summary>Globs (relative to workspace) of the files `hook` raises as "transcript" items (1.4.0, with [review]).</summary>
    public IReadOnlyList<string> RaiseTranscripts { get; init; } = [];

    /// <summary>[[raise]]: files `hook` raises as items of their own, checked before RaiseTranscripts, first match wins.</summary>
    public IReadOnlyList<RaiseRule> Raise { get; init; } = [];

    /// <summary>1.5.0: posting opted-in meetings' transcripts and summaries to their chats ([[share]] and the rest).</summary>
    public ShareSettings Sharing { get; init; } = ShareSettings.None;

    /// <summary>Null: the defaults in Dictation.Names. Empty: the sweep is off.</summary>
    public IReadOnlyList<string>? Dictation { get; init; }

    /// <summary>The path relative to the workspace, as a posix path. Throws ArgumentException (Python's ValueError)
    /// for a path outside it.</summary>
    public string Relative(string path) =>
        Paths.RelativeTo(PyPath.Resolve(path), Workspace) ?? throw new ArgumentException($"{path} is not in the subpath of {Workspace}");

    /// <summary>Whether `hook` drafts this transcript: inside the workspace, matching include and not exclude, and
    /// not itself a summary.</summary>
    public bool Wanted(string transcript)
    {
        string rel;
        try
        {
            rel = Relative(transcript);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if ($"/{rel}".Contains($"/{Summaries}/", StringComparison.Ordinal) || PyStr.Lower(Paths.Suffix(transcript)) is not (".md" or ".txt"))
            return false;
        return Include.Any(g => Paths.FnMatch(rel, g)) && !Exclude.Any(g => Paths.FnMatch(rel, g));
    }

    public static string DefaultPath(Func<string, string?> env) =>
        System.IO.Path.Combine(env("APPDATA") ?? PyPath.Home(env), "meeting-summaries", "config.toml");

    public static string DefaultLog(Func<string, string?> env) =>
        System.IO.Path.Combine(env("LOCALAPPDATA") ?? PyPath.Home(env), "meeting-summaries", "meeting-summaries.log");

    private static string PathValue(string value, string baseFolder, Func<string, string?> env)
    {
        var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, env), env);
        return PyPath.Resolve(PyPath.IsAbsolute(expanded) ? expanded : PyPath.ResolveAgainst(baseFolder, expanded));
    }

    /// <summary>Read the settings: the path given, else MEETING_SUMMARIES_CONFIG, else the default. Throws ConfigError.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var given = !string.IsNullOrEmpty(path) ? path : env("MEETING_SUMMARIES_CONFIG") is { Length: > 0 } e ? e : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new ConfigError($"No settings file at {full}. Run `meeting-summaries config` to create one.", missing: true);
        TomlTable raw;
        try
        {
            // With the base file it names, if any: its [paths] are variables for the paths here (so env is replaced),
            // and its settings this file knows and doesn't set are added.
            var read = BaseSettings.Read(PyStr.ReadText(full), full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (FormatException error)
        {
            throw new ConfigError(error.Message);   // the base: missing, unreadable, or naming a base itself
        }
        catch (Exception error) when (error is not IOException and not UnauthorizedAccessException)
        {
            throw new ConfigError($"{full}: {error.Message}");
        }
        var folder = Paths.Parent(full);
        string Text(string key, string fallback) => raw.TryGetValue(key, out var v) ? Str(v) : fallback;
        bool Truthy(string key) => raw.TryGetValue(key, out var v) && v is not null && !(v is string s && s.Length == 0)
                                   && !(v is bool b && !b) && !(v is long l && l == 0) && !(v is TomlArray a && a.Count == 0);
        List<string> List(string key, List<string> fallback) =>
            raw.TryGetValue(key, out var v) ? v is TomlArray a ? a.Select(Str).ToList() : Str(v).Select(c => c.ToString()).ToList() : fallback;

        var workspace = PathValue(Text("workspace", "."), folder, env);
        if (!Directory.Exists(workspace))
            throw new ConfigError($"workspace in {full} isn't a folder: {workspace}");
        var style = "";
        if (Truthy("style"))
        {
            var stylePath = PathValue(Text("style", ""), folder, env);
            if (!File.Exists(stylePath))
                throw new ConfigError($"style in {full} names a file that isn't there: {stylePath}");
            style = PyStr.ReadText(stylePath);
        }
        ReviewQueue.ReviewSettings? review = null;
        if (raw.TryGetValue("review", out var reviewSection))
        {
            try
            {
                review = ReviewQueue.ReviewSettings.Read(reviewSection, full, value => PathValue(value, folder, env));
            }
            catch (FormatException error)
            {
                throw new ConfigError(error.Message);
            }
        }
        var raise = new List<RaiseRule>();
        if (raw.TryGetValue("raise", out var r))
        {
            if (r is not TomlTableArray rules)
                throw new ConfigError($"{full}: raise should be [[raise]] tables: match, type, sweep, note");
            foreach (var rule in rules)
            {
                foreach (var key in rule.Keys)
                    if (key is not ("match" or "type" or "sweep" or "note"))
                        throw new ConfigError($"{full}: [[raise]] has an unknown setting '{key}'");
                if (!rule.TryGetValue("match", out var m) || m is not string { Length: > 0 } match
                    || !rule.TryGetValue("type", out var ty) || ty is not string { Length: > 0 } type)
                    throw new ConfigError($"{full}: each [[raise]] needs match (a glob) and type");
                var sweep = rule.TryGetValue("sweep", out var sw) && (sw is bool b ? b : throw new ConfigError($"{full}: [[raise]] sweep should be true or false"));
                raise.Add(new RaiseRule(match, type, sweep, rule.TryGetValue("note", out var n) ? Str(n) : null));
            }
        }
        var owner = Text("owner", "You");
        var sharing = ShareSettings.Read(raw, full, value => PathValue(value, folder, env), env, owner,
            System.IO.Path.Combine(env("LOCALAPPDATA") ?? PyPath.Home(env), "meeting-summaries"));
        if ((raise.Count > 0 || Truthy("raise_transcripts")) && review is null)
            throw new ConfigError($"{full}: raise and raise_transcripts need [review], the queue they raise items in");
        var config = new Config
        {
            Path = full,
            HookDrafts = !raw.TryGetValue("hook_drafts", out var hd) || (hd is bool hb ? hb : throw new ConfigError($"{full}: hook_drafts should be true or false")),
            RaiseTranscripts = List("raise_transcripts", []),
            Raise = raise,
            Review = review,
            Workspace = workspace,
            Model = Text("model", "opus"),
            Owner = owner,
            Sharing = sharing,
            TemplateFile = Truthy("template") ? PathValue(Text("template", ""), workspace, env) : null,
            Style = style,
            Context = List("context", []),
            Ledger = Truthy("ledger") ? PathValue(Text("ledger", ""), workspace, env) : null,
            Include = List("include", ["**"]),
            Exclude = List("exclude", []),
            Summaries = Text("summaries", "summaries"),
            LogFile = Truthy("log_file") ? PathValue(Text("log_file", ""), folder, env) : DefaultLog(env),
            AfterDraft = List("after_draft", []),
            TimeoutMinutes = raw.TryGetValue("timeout_minutes", out var t) ? Int(t) : 30,
            Dictation = raw.TryGetValue("dictation", out var d) ? List("dictation", []).Select(PyStr.Lower).ToList() : null,
        };
        if (config.TemplateFile is { } template && !File.Exists(template))
            throw new ConfigError($"template in {full} names a file that isn't there: {template}");
        return config;
    }

    /// <summary>Python's str() of a TOML value.</summary>
    private static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double x => Py.Repr(x),
        _ => value.ToString() ?? "",
    };

    private static int Int(object? value) => value switch
    {
        long l => checked((int)l),
        double x => checked((int)Math.Truncate(x)),
        bool b => b ? 1 : 0,
        string s when int.TryParse(s.Trim(), out var n) => n,
        _ => throw new ConfigError($"timeout_minutes must be a whole number, not {Str(value)}"),
    };

    /// <summary>Create the default settings file if it's missing; returns where it is.</summary>
    public static string CreateDefault(Func<string, string?>? env = null)
    {
        var path = DefaultPath(env ?? Environment.GetEnvironmentVariable);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PyStr.WindowsNewlines(Template.Replace("\r\n", "\n")), new UTF8Encoding(false));
        }
        return path;
    }
}

/// <summary>A [[raise]] rule: files matching the glob are raised as items of this type.</summary>
public sealed record RaiseRule(string Match, string Type, bool Sweep, string? Note);

[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal partial class TomlContext : Tomlyn.Serialization.TomlSerializerContext;
