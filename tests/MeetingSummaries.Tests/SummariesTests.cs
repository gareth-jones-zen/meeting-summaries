using ClaudeHeadless;
using PyCompat;

namespace MeetingSummaries.Tests;

/// <summary>The Python tests (tests/test_summaries.py), ported, with synthetic transcripts only.</summary>
public class SettingsTests : IDisposable
{
    private readonly Workspace ws = new();

    public void Dispose() => ws.Dispose();

    [Fact]
    public void Which_transcripts_the_hook_takes()
    {
        Assert.True(ws.Config.Wanted(ws.TranscriptPath));
        Assert.False(ws.Config.Wanted(ws.SummaryPath));
        Assert.False(ws.Config.Wanted(Path.Combine(ws.Work, "meetings", "private", "a.md")));
        Assert.False(ws.Config.Wanted(Path.Combine(ws.Dir, "elsewhere.md")));
        Assert.False(ws.Config.Wanted(Path.Combine(ws.Work, "meetings", "alex", "notes.docx")));
        Assert.True(ws.Config.Wanted(Path.Combine(ws.Work, "MEETINGS", "alex", "x.TXT")));   // Windows: case ignored
    }

    [Fact]
    public void Prompt_carries_the_house_rules_and_ledger()
    {
        var text = Prompt.Build(ws.Config, ["meetings/alex/2026-11-06.md"], "meetings/alex/summaries/");
        Assert.Contains("**[Owner]**", text);
        Assert.Contains("read ledger.md", text);
        Assert.True(text.IndexOf("House rules", StringComparison.Ordinal) > text.IndexOf("The shape:", StringComparison.Ordinal));
        Assert.Contains("British English", text);
        Assert.Contains("transcript: ../2026-11-06.md,", text);
        var split = Prompt.Build(ws.Config, ["a/x-part1.md", "a/x-part2.md"], null);
        Assert.Contains("transcript: [../x-part1.md, ../x-part2.md]", split);
        // Python's str.format put the list itself into the shape line for a split meeting; kept, byte for byte.
        Assert.Contains("transcript: ../['x-part1.md', 'x-part2.md'], and status: draft", split);
        Assert.DoesNotContain("{", split);
    }

    [Fact]
    public void Settings_resolve_paths_and_defaults()
    {
        Assert.Equal(Paths.Resolve(ws.Work), ws.Config.Workspace);
        Assert.Equal(Path.Combine(ws.Dir, "log.txt"), ws.Config.LogFile);
        Assert.Equal("opus", ws.Config.Model);
        Assert.Equal(30, ws.Config.TimeoutMinutes);
        Assert.Null(ws.Config.Dictation);
        Assert.Equal("- British English.", ws.Config.Style);
    }

    [Fact]
    public void Bad_settings_say_what_is_wrong()
    {
        var missing = Assert.Throws<ConfigError>(() => Config.Load(Path.Combine(ws.Dir, "nope.toml")));
        Assert.True(missing.Missing);
        Workspace.Write(Path.Combine(ws.Dir, "bad.toml"), "workspace = \"not-there\"\n");
        Assert.Contains("isn't a folder", Assert.Throws<ConfigError>(() => Config.Load(Path.Combine(ws.Dir, "bad.toml"))).Message);
        Workspace.Write(Path.Combine(ws.Dir, "bad.toml"), "workspace = \"work\"\ntemplate = \"t.md\"\n");
        Assert.Contains("template in", Assert.Throws<ConfigError>(() => Config.Load(Path.Combine(ws.Dir, "bad.toml"))).Message);
        Workspace.Write(Path.Combine(ws.Dir, "bad.toml"), "workspace = [\n");
        Assert.Throws<ConfigError>(() => Config.Load(Path.Combine(ws.Dir, "bad.toml")));
    }

    [Fact]
    public void Dictation_names_and_variables()
    {
        Workspace.Write(Path.Combine(ws.Dir, "d.toml"), "workspace = \"%MS_TEST_WS%\"\ndictation = [\"Claude\", \"Clark\"]\ntimeout_minutes = 5\n");
        var config = Config.Load(Path.Combine(ws.Dir, "d.toml"), name => name == "MS_TEST_WS" ? ws.Work : Environment.GetEnvironmentVariable(name));
        Assert.Equal(["claude", "clark"], config.Dictation);
        Assert.Equal(5, config.TimeoutMinutes);
    }
}

public class DraftingTests : IDisposable
{
    private readonly Workspace ws = new();

    public void Dispose() => ws.Dispose();

    [Fact]
    public void Writes_the_draft_with_quotes_checked_and_questions_in_the_ledger()
    {
        Assert.Equal("written", ws.Draft());
        var command = ws.Claude.Calls[0].Command;
        Assert.Equal("Read,Grep,Glob", command[command.ToList().IndexOf("--tools") + 1]);
        var text = Workspace.Read(ws.SummaryPath);
        var check = text.IndexOf("[Check] 1 of 2 quotes", StringComparison.Ordinal);
        Assert.True(text.IndexOf("[Owner]", StringComparison.Ordinal) < check && check < text.IndexOf("[Noted]", StringComparison.Ordinal));
        Assert.Contains("negation", text);   // an added "not"
        var ledger = Workspace.Read(ws.Ledger);
        Assert.Contains("- 2026-11-12 \u00b7 [alex 2026-11-06](meetings/alex/summaries/2026-11-06.md) \u00b7 Did you mean Monday the 9th?", ledger);
        Assert.True(ledger.IndexOf("Monday the 9th", StringComparison.Ordinal) < ledger.IndexOf("## Settled", StringComparison.Ordinal));
        Assert.Equal("meetings/alex/2026-11-06.md: draft written to meetings/alex/summaries/2026-11-06.md (3 turns; 1 question(s) added to the ledger)", ws.Log[0]);
        Assert.Contains("\r\n", File.ReadAllText(ws.SummaryPath));   // written in text mode, as Python wrote it
    }

    [Fact]
    public void Never_overwrites_a_summary()
    {
        Workspace.Write(ws.SummaryPath, "finished");
        Assert.Equal("exists", ws.Draft());
        Assert.Empty(ws.Claude.Calls);
    }

    [Fact]
    public void Out_touches_nothing_else()
    {
        var output = Path.Combine(ws.Dir, "compare.md");
        Assert.Equal("written", ws.Draft(output: output));
        Assert.True(File.Exists(output));
        Assert.False(Directory.Exists(Path.GetDirectoryName(ws.SummaryPath)));
        Assert.DoesNotContain("Monday the 9th", Workspace.Read(ws.Ledger));
    }

    [Fact]
    public void A_later_part_waits_for_the_first()
    {
        var part1 = Path.Combine(ws.Work, "meetings", "alex", "2026-11-07-part1.md");
        var part2 = Path.Combine(ws.Work, "meetings", "alex", "2026-11-07-part2.md");
        Workspace.Write(part1, Workspace.Transcript);
        Workspace.Write(part2, Workspace.Transcript);
        Assert.Equal("waiting", ws.Draft(part2));
        Assert.Equal(part1, ws.Queue.Pending()[0].Argv[^1]);
        Assert.Equal(["--config", ws.Config.Path, "draft"], ws.Queue.Pending()[0].Argv.Skip(1).Take(3));
        Assert.Equal("2026-11-07.md", Path.GetFileName(Draft.SummaryPath(ws.Config, part1)));
        Assert.Equal([part1, part2], Draft.Parts(part2));
    }

    [Fact]
    public void A_split_meeting_is_one_summary_from_every_part()
    {
        var part1 = Path.Combine(ws.Work, "meetings", "alex", "2026-11-07-part1.md");
        Workspace.Write(part1, Workspace.Transcript);
        Workspace.Write(Path.Combine(ws.Work, "meetings", "alex", "2026-11-07-part2.md"), "**A** 0:01\nClaude, please add a note to my inbox.\n");
        Workspace.Write(Path.Combine(ws.Work, "meetings", "alex", "2026-11-07-part10.md"), Workspace.Transcript);
        Assert.Equal(["2026-11-07-part1.md", "2026-11-07-part2.md", "2026-11-07-part10.md"], Draft.Parts(part1).Select(Path.GetFileName));
        Assert.Equal("written", ws.Draft(part1));
        var prompt = ws.Claude.Calls[0].Prompt;
        Assert.Contains("Transcript: meetings/alex/2026-11-07-part1.md, meetings/alex/2026-11-07-part2.md, meetings/alex/2026-11-07-part10.md", prompt);
        Assert.Contains("  - line 2026-11-07-part2.md:2: *Claude, please add a note to my inbox.*", Workspace.Read(Path.Combine(ws.Work, "meetings", "alex", "summaries", "2026-11-07.md")));
    }

    [Fact]
    public void The_usage_limit_queues_and_notifies_once()
    {
        var reset = DateTimeOffset.UtcNow + TimeSpan.FromHours(2);
        ws.Claude = new FakeClaude(null, new LimitReachedException(reset, "limit"));
        Assert.Equal("queued", ws.Draft());
        var other = Path.Combine(ws.Work, "meetings", "alex", "2026-11-08.md");
        Workspace.Write(other, Workspace.Transcript);
        ws.Draft(other);
        Assert.Single(ws.Notified);
        Assert.Equal("Claude usage limit reached", ws.Notified[0].Title);
        Assert.Equal(2, ws.Queue.Pending().Count);
        Assert.Equal(@"C:\tools\meeting-summaries.exe", ws.Queue.Pending()[0].Argv[0]);
        Assert.StartsWith("meetings/alex/2026-11-06.md: Claude's usage limit; queued for after ", ws.Log[0]);
    }

    [Fact]
    public void The_usage_limit_with_out_is_reported_not_queued()
    {
        ws.Claude = new FakeClaude(null, new LimitReachedException(new DateTimeOffset(2026, 11, 12, 19, 0, 0, TimeSpan.Zero), "limit"));
        Assert.Equal("limit", ws.Draft(output: Path.Combine(ws.Dir, "x.md")));
        Assert.Equal("meetings/alex/2026-11-06.md: Claude's usage limit, resets 19:00", ws.Log[0]);
        Assert.Empty(ws.Queue.Pending());
        Assert.Empty(ws.Notified);
    }

    [Fact]
    public void A_reply_that_isnt_a_draft_is_an_error()
    {
        ws.Claude = new FakeClaude("Sorry, I couldn't read the file.");
        Assert.Throws<ClaudeRunException>(() => ws.Draft());
    }

    [Fact]
    public void Code_fences_and_preamble_are_stripped()
    {
        ws.Claude = new FakeClaude("Here it is:\n\n```markdown\n" + Workspace.DraftReply + "```\n");
        ws.Draft();
        Assert.StartsWith("---\ndate: 2026-11-06", Workspace.Read(ws.SummaryPath));
        Assert.EndsWith("## Transcript notes\n", Workspace.Read(ws.SummaryPath));
    }

    [Fact]
    public void A_draft_with_no_for_review_gets_one()
    {
        ws.Claude = new FakeClaude(Workspace.DraftReply.Replace("## For review\n\n- **[Owner]** Did you mean Monday the 9th?\n- **[Noted]** \"Sam\" taken to be Sam Jones.\n\n", ""));
        ws.Draft();
        Assert.Contains("# [DRAFT] Catch-up\n\n## For review\n\n- **[Check] 1 of 2 quotes", Workspace.Read(ws.SummaryPath));
    }

    [Fact]
    public void After_draft_gets_the_report()
    {
        using var other = new Workspace("after_draft = [\"python\", \"x.py\"]\n");
        Workspace.Write(other.TranscriptPath, Workspace.Transcript + "Claude, please add a note to my inbox.\n");
        other.Draft();
        var (argv, input, cwd) = Assert.Single(other.AfterDrafts);
        Assert.Equal(["python", "x.py"], argv);
        Assert.Equal(other.Dir, cwd);
        Assert.Equal("{\"transcript\": \"meetings/alex/2026-11-06.md\", \"transcripts\": [\"meetings/alex/2026-11-06.md\"], " +
                     "\"summary\": \"meetings/alex/summaries/2026-11-06.md\", \"questions\": [\"Did you mean Monday the 9th?\"], " +
                     "\"session\": \"s1\", \"dictation\": [{\"line\": 8, \"text\": \"Claude, please add a note to my inbox.\"}]}", input);
    }
}

public class DictationTests : IDisposable
{
    private const string Lines = """
        **Clark Kent** 0:11
        Morning all.
        Claude had a note for me to check with Sam about the rollout.
        Clark, please remind me about the budget.
        Sorry, I've just started doing this now.
        We had a note from the supplier.
        Claude is good at this.
        Claude, I did items in my one to one with Dan tomorrow.
        """;

    private readonly Workspace ws = new();

    public void Dispose() => ws.Dispose();

    [Fact]
    public void Finds_mangled_instructions_and_skips_speaker_headings()
    {
        Assert.Equal(["3", "4", "5", "8"], Dictation.Find(Lines).Select(f => f.Line));   // not the heading, the supplier note, or the remark
        Assert.Empty(Dictation.Find(Lines, []));
        Assert.Equal(["3", "5", "8"], Dictation.Find(Lines, ["claude"]).Select(f => f.Line));
    }

    [Fact]
    public void Word_boundaries_are_pythons()
    {
        // Python's \b doesn't count a combining mark as part of a word; .NET's does.
        Assert.Single(Dictation.Find("Claude\u0301 add it"));
        Assert.Empty(Dictation.Find("Claudes adding"));
        Assert.Single(Dictation.Find("CLAUDE: 1:1 tomorrow"));
        Assert.Single(Dictation.Find("x\u2028Claude add"));   // a line break Python's splitlines knows
        Assert.Equal("2", Dictation.Find("x\u2028Claude add")[0].Line);
    }

    [Fact]
    public void The_draft_puts_candidates_to_the_owner_first()
    {
        Workspace.Write(ws.TranscriptPath, Workspace.Transcript + "\n" + Lines);
        ws.Draft();
        var text = Workspace.Read(ws.SummaryPath);
        var review = text[(text.IndexOf("## For review", StringComparison.Ordinal) + "## For review".Length)..];
        Assert.StartsWith("- **[Owner] Dictated instructions?** 4 lines", PyStr.LStrip(review));
        Assert.Contains("Claude had a note for me to check with Sam", review);
        Assert.Contains("may be addressed to Claude.", review);
        // A dictation question is about this meeting only, so it isn't added to the ledger.
        Assert.DoesNotContain("Dictated", Workspace.Read(ws.Ledger));
    }

    [Fact]
    public void None_found_adds_nothing()
    {
        ws.Draft();
        Assert.DoesNotContain("Dictated", Workspace.Read(ws.SummaryPath));
    }
}

public class QuoteCheckTests
{
    private const string Transcript = "**Alex Smith** 0:01\nIt's that complex. I'm not worried, honestly, about any of it at all.\n\n**Sam** 1:02:03\nWe wasn't the whole team there, yeah yeah, on the Tuesday.\n";

    private static string Summary(params string[] quotes) =>
        "---\ntitle: x\ntranscript: ../t.md\n---\n\n# x\n\n" + string.Join("\n", quotes.Select(q => $"- *\"{q}\"*")) +
        "\n\n## Transcript notes\n\n- \"this one is never checked at all\"\n";

    [Fact]
    public void A_quote_found_word_for_word_passes()
    {
        var (count, doubtful) = QuoteCheck.Check(Summary("I'm not worried, honestly, about any"), Transcript);
        Assert.Equal(1, count);
        Assert.Empty(doubtful);
    }

    [Fact]
    public void An_added_not_is_always_flagged()
    {
        var (_, doubtful) = QuoteCheck.Check(Summary("It's not that complex at all"), Transcript);
        Assert.Equal("it has a negation the transcript doesn't", Assert.Single(doubtful).Why);
        Assert.StartsWith("its that complex", doubtful[0].Found);
    }

    [Fact]
    public void A_negation_just_before_the_passage_counts()
    {
        var (_, doubtful) = QuoteCheck.Check(Summary("not the whole team there on the Tuesday"), Transcript);
        Assert.Empty(doubtful);
    }

    [Fact]
    public void A_paraphrase_reports_the_share_found()
    {
        var (_, doubtful) = QuoteCheck.Check(Summary("the entire group went home on Friday evening"), Transcript);
        Assert.EndsWith("% of its words found", Assert.Single(doubtful).Why);
    }

    [Fact]
    public void Ellipses_and_inserted_words_join_fragments_and_short_ones_are_skipped()
    {
        var (count, doubtful) = QuoteCheck.Check(Summary("It's that complex \u2026 about any of it at all", "a tiny fragment!"), Transcript);
        Assert.Equal(2, count);
        Assert.Empty(doubtful);
    }

    [Fact]
    public void Percentages_round_half_to_even_as_python()
    {
        // 5 of 8 words: 62.5%, which Python prints as 62%.
        var (_, doubtful) = QuoteCheck.Check(Summary("its that complex im not zebra yak quail"), "its that complex im not worried honestly about");
        Assert.Equal("62% of its words found", Assert.Single(doubtful).Why);
    }

    [Fact]
    public void Norm_and_flatten()
    {
        Assert.Equal("ems and its fine", QuoteCheck.Norm("EM's and \u2018it\u2019s\u2019 FINE!"));
        Assert.Equal("hello there again", QuoteCheck.Flatten("**Alex** 0:01\nHello there, 12:30 again."));
        Assert.Equal("i stanbul", QuoteCheck.Norm("\u0130stanbul"));   // Python lowers U+0130 to two characters
    }

    [Fact]
    public void An_empty_transcript_finds_nothing()
    {
        var (_, doubtful) = QuoteCheck.Check(Summary("anything at all that is long"), "");
        Assert.Equal("0% of its words found", Assert.Single(doubtful).Why);
    }

    [Fact]
    public void Sequence_matcher_blocks_match_difflib()
    {
        // difflib.SequenceMatcher(None, a, b, autojunk=False).get_matching_blocks(), printed by Python.
        var blocks = SequenceMatcher.MatchingBlocks(["a", "b", "x", "c", "d"], ["a", "b", "c", "d", "y", "a", "b"]);
        Assert.Equal([new(0, 0, 2), new(3, 2, 2), new SequenceMatcher.Match(5, 7, 0)], blocks);
        var ties = SequenceMatcher.MatchingBlocks(["q", "r"], ["r", "q", "r", "q"]);
        Assert.Equal([new(0, 1, 2), new SequenceMatcher.Match(2, 4, 0)], ties);
    }
}

public class PathTests
{
    [Theory]
    [InlineData("meetings/catch-ups/x.md", "meetings/**", true)]
    [InlineData("meetings/catch-ups/x.md", "**/one-to-ones/**", false)]
    [InlineData("team/a/one-to-ones/x.md", "**/one-to-ones/**", true)]
    [InlineData("Meetings/A.md", "meetings/*.md", true)]
    [InlineData("meetings/a.md", "meetings/?.md", true)]
    [InlineData("meetings/ab.md", "meetings/[ab].md", false)]
    [InlineData("meetings/b.md", "meetings/[a-c].md", true)]
    [InlineData("meetings/b.md", "meetings/[!a-c].md", false)]
    [InlineData("meetings/[x.md", "meetings/[x.md", true)]
    public void Fnmatch_as_python_on_windows(string name, string pattern, bool expected) =>
        Assert.Equal(expected, Paths.FnMatch(name, pattern));

    [Fact]
    public void Names_stems_and_suffixes()
    {
        Assert.Equal(".md", Paths.Suffix(@"C:\a\x-part1.md"));
        Assert.Equal("x-part1", Paths.Stem(@"C:\a\x-part1.md"));
        Assert.Equal("", Paths.Suffix(@"C:\a\.md"));
        Assert.Equal(".md", Paths.Stem(@"C:\a\.md"));
        Assert.Equal("a.b", Paths.Stem("a.b.md"));
        Assert.Equal("meetings/a.md", Paths.RelativeTo(@"C:\W\meetings\a.md", @"c:\w"));
        Assert.Null(Paths.RelativeTo(@"C:\Wx\a.md", @"C:\W"));
    }

    [Fact]
    public void Resolve_gives_the_case_on_disk()
    {
        var dir = Directory.CreateTempSubdirectory("Ms-Case-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Meetings"));
            var resolved = Paths.Resolve(Path.Combine(dir.ToUpperInvariant(), "meetings", "not-yet.md"));
            Assert.EndsWith(@"\Meetings\not-yet.md", resolved);
            Assert.Contains(@"\Ms-Case-", resolved);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Title_as_python()
    {
        Assert.Equal("Claude", Re.Title("claude"));
        Assert.Equal("O'Brien 3D", Re.Title("o'brien 3d"));
    }
}
