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

Windows (x64). **Needs Claude Code installed and signed in** (`claude` on the PATH), on a plan that allows your
meetings' content; nothing else.

1. Download `meeting-summaries-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/meeting-summaries/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\meeting-summaries`, and add that folder to your `PATH` (or run it from there).
3. Then:

```
meeting-summaries config         # create the settings file and say where it is
meeting-summaries autostart on   # drafts held up by Claude's usage limit carry on by themselves
```

It holds two programs: `meeting-summaries.exe`, for a console, and `meeting-summaries-background.exe`, the same
program with no window, for hooks and the scheduled task. It runs Claude Code headless, copes with the usage limit
by queueing drafts until it resets, and notifies you only when something needs you.

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
after_draft = ["powershell", "-File", "x.ps1"]   # optional: run after each draft, with a JSON report on stdin
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

## The queue

Drafts wait in one queue per Claude seat (`%LOCALAPPDATA%\claude-headless\queue.json`), because every tool that
runs Claude headless shares the same usage limit. `meeting-summaries run-queue` works through it now (every tool's
jobs, not only drafts); `autostart on` registers a task, `claude-headless run-queue`, that does it every 15 minutes.

## Building

It's .NET 11, published as Native AOT. It needs [background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)
checked out beside this repository (or set the `BackgroundToolsLib` property to a checkout elsewhere), then:

```
dotnet test -c Release
dotnet publish src/MeetingSummaries.Console -c Release -r win-x64 -o out
dotnet publish src/MeetingSummaries.Background -c Release -r win-x64 -o out
```

Native AOT needs the Visual Studio C++ build tools. The tests use synthetic transcripts only and never run Claude.
A tag `v*` builds and publishes a release.
