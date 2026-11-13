> **The .NET port is on this branch (`dotnet`).** It becomes `meeting-summaries.exe` and
> `meeting-summaries-background.exe` (Native AOT, nothing to install first). The text below still describes the
> Python meeting-summaries 0.2.0 (tag `v0.2.0`), which is what runs until the switch-over; the port does the
> same, and the differences are listed under **The .NET port** at the end.

# meeting-summaries

A draft summary of each meeting transcript, written by Claude Code for you to review: purpose,
key points, decisions, actions and open questions, with every quote checked against the
transcript, and the few questions only you can answer put first.

It pairs with [teams-transcripts](https://github.com/gareth-jones-zen/teams-transcripts): that
gets each transcript onto your disk when a meeting ends, and this turns it into something you
can search six months later.

```
meeting-summaries draft "Meetings/Alex catch-up 2026-11-06.md"
  Meetings/Alex catch-up 2026-11-06.md: draft written to Meetings/summaries/Alex catch-up 2026-11-06.md (14 turns)
```

## Install

```
pipx install --preinstall git+https://github.com/gareth-jones-zen/win-background --preinstall git+https://github.com/gareth-jones-zen/claude-headless git+https://github.com/gareth-jones-zen/meeting-summaries
meeting-summaries config      # create the settings file and say where it is
meeting-summaries autostart on   # drafts held up by Claude's usage limit carry on by themselves
```

**Needs Claude Code installed and signed in** (`claude` on the PATH), on a plan that allows your
meetings' content. [claude-headless](https://github.com/gareth-jones-zen/claude-headless) runs it
and copes with the usage limit; [win-background](https://github.com/gareth-jones-zen/win-background)
does notifications and the log.

**To draft every new transcript automatically**, point teams-transcripts' `after_run` at the hook:

```toml
after_run = ["meeting-summaries", "hook"]
```

## Settings

`%APPDATA%\meeting-summaries\config.toml` by default (`--config` or `MEETING_SUMMARIES_CONFIG`).

```toml
workspace = "~/Documents/Meetings"   # where your transcripts are; Claude reads here, and only reads
owner = "Alex"                       # the label on the questions only you can answer
model = "opus"
template = "templates/summary.md"    # optional: your summary's shape
style = "summary-style.md"           # optional: your house rules (beside this file)
context = ["people.md"]              # optional: files that say who people are
ledger = "review-ledger.md"          # optional: see below
include = ["**"]                     # which transcripts the hook drafts
exclude = ["**/one-to-ones/**"]      # and which it never does
after_draft = ["python", "x.py"]     # optional: run after each draft, with a JSON report on stdin
```

**`style` is where your own rules go**: what stays out of a summary (health, family, small talk),
how to spell people's names, which labels you use. They're added to the tool's own rules and win
where they differ.

## What a draft looks like, and why

```
---
date: 2026-11-06
title: Alex catch-up
attendees: [...]
topics: [rollout, budget]
transcript: ../Alex catch-up 2026-11-06.md
status: draft
---

# [DRAFT] Alex catch-up

## For review
- **[Alex]** Did you mean Monday the 9th?
- **[Check] 1 of 23 quotes to verify against the transcript.** ...
- **[Noted]** "Sam" taken to be Sam Jones.

## Purpose / Key points / Decisions / Actions / Open questions / Transcript notes
```

These came from reviewing the first hundred or so drafts:

- **"For review" is labelled, so you answer only what needs you.** In a batch of 13 drafts there
  were about 100 review items, and only 8 needed the owner. **[owner]** questions come first;
  **[Noted]** items are readings the draft chose, accepted unless they look wrong.
- **Every quote is checked against the transcript** (`meeting-summaries check` runs the same check
  on any summary). About 750 quotes in that batch; 4 didn't say what the transcript says. One had an
  added "not", which reads fine and inverts the meaning, so **a negation the transcript doesn't have
  is always flagged.**
- **Questions are asked once.** With a `ledger` (a markdown file with `## Open` and `## Settled`
  headings), each draft's questions go under Open, and drafts treat your Settled answers as fact.
  A test draft once asked for a leave date given an hour earlier.
- **Instructions dictated to your assistant are put to you first.** If you say "Claude, add an item to
  my one-to-one with Sam…" in a meeting, you'll assume it was picked up. Speech-to-text mangles both the
  name (Clark, Cloud, Clyde) and the verb ("Claude had a note", "Claude, I did items in my one to one"),
  so the sweep is deliberately wide: the name plus an action word or a destination (one-to-one, inbox,
  notes, prep). Every candidate line goes in a **[owner] Dictated instructions?** bullet for you to judge.
  Set `dictation` to the names you use and their manglings, or `[]` to turn it off;
  `meeting-summaries dictation <transcript>` runs it alone.
- **Claude only reads.** It runs headless in your workspace with Read, Grep and Glob, and no
  connectors; the tool writes the file. **A summary that exists is never overwritten.**
- **A meeting split across recordings is one meeting:** `x-part1.md` and `x-part2.md` get one
  summary, `summaries/x.md`, from every part.
- **The usage limit doesn't lose drafts.** They're queued with the reset time and notified once;
  `autostart on` works through the queue every 15 minutes, and so does the hook whenever a new
  transcript arrives.

## The .NET port

A port of meeting-summaries 0.2.0 to .NET 11, published with Native AOT: `meeting-summaries.exe` (a console
program, every command) and `meeting-summaries-background.exe` (the same, with no window: for hooks and the
autostart task). Same settings file, same commands, same drafts.

### Building

It uses three projects from [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
(ClaudeHeadless, WinBackground, PyCompat), as a **sibling checkout**:

```
source/repos/
  background-tools-lib/      (branch main)
  meeting-summaries/         (this repo, branch dotnet)
```

```
dotnet build -c Release
dotnet test -c Release
dotnet publish src/MeetingSummaries.Console -c Release -r win-x64 -o out
dotnet publish src/MeetingSummaries.Background -c Release -r win-x64 -o out
```

Another checkout of the library can be named with `-p:BackgroundToolsLib=<folder>\`.

Native AOT needs the Visual Studio C++ build tools; from Git Bash, put
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` on the `PATH` first. The tests use xunit v3 on the
Microsoft Testing Platform, with synthetic transcripts only, and never run Claude: drafting takes an
`IClaudeRunner`, and the tests give it a fake.

### Checked against the Python version

Before the switch, the port was run beside the Python 0.2.0 on a real workspace (read only), with `claude` replaced
by a stand-in that records what it's sent and replies with a recorded draft:

| What | How | Result |
|---|---|---|
| The dictation sweep | `dictation` over every transcript, 328 files | Identical output; 129 candidate lines |
| The quote check | `check` on every summary against its transcripts, 124 summaries | Identical output; 4,110 quotes, 99 to verify |
| The prompt | the bytes sent to `claude` on stdin, and its arguments, for 41 transcripts (2 split) | Byte-identical |
| Post-processing | `draft --out` with a recorded reply (existing summaries, in four shapes: with and without "For review", fenced, with a preamble) | Byte-identical drafts and log lines |
| The review ledger | two drafts into a copy of a real ledger, with a question already in it | Byte-identical ledger, summaries, log and after_draft report |
| The usage limit | a limit on stderr, then a second run while it's in force | Identical exit codes, log lines and `limit.json` |

Python's own behaviour is reproduced where it shows in the output: `str.splitlines`, `str.strip` and `str.lower`
(which know more line breaks and spaces than .NET, and lower a final sigma), `\b`, `\w` and `\s` in regular
expressions (Python's word characters don't include combining marks; the dictation patterns run through PyRegex, so
case-insensitive matching folds dotted and dotless i and the long s as Python's does), slices counted in code points,
`difflib.SequenceMatcher` (ported, ties included), the `round-half-even` percentages, text-mode files (written with
CRLF), and `Path.resolve()` (the case on disk). Most of these come from the library's PyCompat; `str.title` and
`SequenceMatcher.get_matching_blocks` (with `autojunk=False`) are still here, as PyCompat doesn't have them.

### Deliberate differences

- **`run-queue` is a command here.** Python's hook started `python -m claude_headless run-queue`; an exe can't
  rely on another being installed beside it, so `meeting-summaries run-queue` does the same job (the queue is the
  seat's, so it runs every tool's jobs). `autostart on` registers the same task name as Python's,
  `claude-headless run-queue`, running `meeting-summaries-background.exe run-queue`.
- **A queued draft runs `meeting-summaries.exe --config ... draft ...`**, not `python -m meeting_summaries`. The
  queue file is shared with the Python tools while they move across, and either runner can run either kind of job.
- **Output to a file or pipe is UTF-8**, whatever the console's code page.
- **Errors are reported, not raised:** a summary with no `transcript:` field, or a file that can't be read, stops
  `check` with a one-line message (exit 1) rather than a traceback. A split meeting's file that doesn't fit the
  `-partN` pattern (`x-part1-notes.md`) is left out rather than stopping the draft.
- **The settings file is read by Tomlyn**, so a malformed file's message differs from Python's.
- **The version is 0.3.0.**
