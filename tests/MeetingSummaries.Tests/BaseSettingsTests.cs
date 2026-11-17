using PyCompat;

namespace MeetingSummaries.Tests;

/// <summary>Settings that name a shared base file (base = "base-config.toml").</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly Workspace ws = new();

    public BaseSettingsTests()
    {
        Workspace.Write(Path.Combine(ws.Dir, "base-config.toml"), $"""
            login_hint = "me@example.com"
            tenant = "contoso"
            model = "sonnet"
            owner = "Base owner"

            [paths]
            ROOT = '{ws.Dir}\shared'
            WORK = '{ws.Work}'

            [git]
            repo = "."

            [review]
            file = "%ROOT%/pending.json"
            lock = "shared-lock"
            """);
    }

    public void Dispose() => ws.Dispose();

    private Config Load(string text)
    {
        var path = Path.Combine(ws.Dir, "with-base.toml");
        Workspace.Write(path, "base = \"base-config.toml\"\n" + text);
        return Config.Load(path);
    }

    private string Shared(string name) => PyPath.Resolve(Path.Combine(ws.Dir, "shared", name));

    [Fact]
    public void Settings_and_path_variables_come_from_the_base()
    {
        var config = Load("workspace = \"%WORK%\"\nlog_file = \"%ROOT%/meeting-summaries.log\"\n");
        Assert.Equal(PyPath.Resolve(ws.Work), config.Workspace);
        Assert.Equal(Shared("meeting-summaries.log"), config.LogFile);
        Assert.Equal(("sonnet", "Base owner"), (config.Model, config.Owner));
    }

    [Fact]
    public void The_tools_own_settings_win()
    {
        var config = Load("workspace = \"work\"\nmodel = \"opus\"\nowner = \"Me\"\n");
        Assert.Equal(("opus", "Me"), (config.Model, config.Owner));
    }

    [Fact]
    public void Review_applies_only_where_this_file_has_the_section()
    {
        Assert.Null(Load("workspace = \"work\"\n").Review);
        var review = Load("workspace = \"work\"\n\n[review]\nsource = \"transcripts\"\nlock = \"mine\"\n").Review!;
        Assert.Equal((Shared("pending.json"), "transcripts", "mine"), (review.File, review.Source, review.Lock));
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var path = Path.Combine(ws.Dir, "missing.toml");
        Workspace.Write(path, "base = \"nope.toml\"\nworkspace = \"work\"\n");
        var error = Assert.Throws<ConfigError>(() => Config.Load(path));
        Assert.Contains("doesn't exist", error.Message);
        Assert.False(error.Missing);
    }
}
