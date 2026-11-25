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

## Sharing settings with other tools: `base`

Several tools built on the shared library can keep their common settings in one file, named at the top of each
tool's settings with `base = "base-config.toml"` (relative to the settings file). Then:

- **the base's `[paths]` are variables for this tool's paths**: `ROOT = "%LOCALAPPDATA%/tools"` there lets this file
  say `log_file = "%ROOT%/meeting-summaries.log"`. They're looked up before the environment;
- **its other settings that this tool knows and doesn't set itself are used** (such as `model` or `owner`). This
  file's own value always wins. A base's `login_hint`, `tenant` and `msal_cache` are used to read the calendar when
  `[[share]]` rules post to meetings' chats (1.5.0; with no rule it signs in to nothing); `time_zone` is left out;
- **a `[review]` in the base applies only if this file has that section too** (even empty), and its keys fill in the
  ones this file leaves out: a shared queue `file` and `lock`, with `source` set here.

The loader is background-tools-lib's `PyCompat.BaseSettings`. A missing or unreadable base is a settings error.

## Settings

`%APPDATA%\meeting-summaries\config.toml` by default (`--config` or `MEETING_SUMMARIES_CONFIG`).

```toml
base = "base-config.toml"            # optional: settings shared with other tools
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

[review]                             # optional: note each draft in a review queue (see below)
file = "%LOCALAPPDATA%/my-tools/review.json"
source = "transcripts"
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
  notes, prep, waiting, chase). Every candidate line goes in a **[owner] Dictated instructions?** bullet for you to judge.
  Set `dictation` to the names you use and their manglings, or `[]` to turn it off;
  `meeting-summaries dictation <transcript>` runs it alone.
- **Claude only reads.** It runs headless in your workspace with Read, Grep and Glob, and no
  connectors; the tool writes the file. **A summary that exists is never overwritten.**
- **A meeting split across recordings is one meeting:** `x-part1.md` and `x-part2.md` get one
  summary, `summaries/x.md`, from every part.
- **The usage limit doesn't lose drafts.** They're queued with the reset time and notified once;
  `autostart on` works through the queue every 15 minutes, and so does the hook whenever a new
  transcript arrives.

## Review queue: `[review]`

If something else (a transcript tool's hook, say) puts each new transcript in a **review queue**, with its path,
`[review]` notes each draft against it: once a draft is written, every item under `source` whose `path` is one of the
draft's transcripts (relative to the workspace, with forward slashes) gets `summary_draft`, the draft's path. So
whatever reads the queue at the start of your day can say which transcripts have a draft waiting. It's
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib)'s `ReviewQueue` (one JSON file, written
under a lock between processes and replaced atomically), so the tools share the file.

- A transcript with no item is no error, and nothing is added. A draft written with `--out` isn't noted.

### The hook can raise the items itself (1.4.0)

The hook can also be what puts each new transcript in the queue, so nothing else needs to. Point doc-filer's (or any
tool's) after-run report at it, and say which files to raise:

```toml
raise_transcripts = ["meetings/*.md", "team/*.md"]   # raised as "transcript" items: swept for dictation, and whether a draft is wanted
hook_drafts = true                                    # false: raise them, but queue no drafts

[[raise]]                                             # these instead, as items of their own (first match wins)
match = "*/performance-reviews/????-??-review-form.md"
type = "review-paperwork"
[[raise]]
match = "*/performance-reviews/????-??-review-transcript*.md"
type = "review-transcript"
sweep = true                                          # swept for dictation too
note = "If the review goes on in another session, refresh its prep from this part first."
```

Each item has `arrived`, `path` (relative to the workspace), `summary_needed` (the hook drafts it), `dictation`
(`[{line, text}]`, the candidate lines) and `title`, plus `note` from its rule. Globs are fnmatch's: `*` matches
across folders. The hook is meant for a report of what's new or changed (doc-filer reports a file only when its
content changed); given the same file twice, it raises it twice. Raising needs `[review]`.
- It happens before `after_draft`; the two are independent. If the queue can't be written, the log says why and the
  draft is kept. The log has only a count.

## Posting a meeting's transcript and summary to its chat: `[[share]]` (1.5.0)

Attendees who aren't a meeting's organiser can't download its transcript, and what most of them want is what was
agreed and who's doing what. So an opted-in meeting gets two posts in its own Teams chat, each independent of the
other, through [teams-post](https://github.com/gareth-jones-zen/teams-post) (0.5.0 or later):

| | Transcript | Summary |
|---|---|---|
| **When** | As soon as the hook is given it (minutes after the meeting) | When you run `share summary` on the finalised summary |
| **What** | The transcript file, attached, with one line: "Transcript of *meeting*, *date*: speech-to-text, so names and terms may be wrong" | The attendee version: Purpose, Decisions, Actions and Open questions (Key points only if the rule says), copied from the summary, never rewritten; then "Summary of *meeting*, *date*, by *share_name*'s assistant, checked by *share_name*." |
| **Who says yes** | The rule: it posts without asking | You, each time: a preview, then `--yes` |

**Nothing is posted unless a rule opts the meeting in.** With no `[[share]]` rule nothing happens, and nothing signs in.

```toml
teams_post = ['%LOCALAPPDATA%/Programs/teams-post/teams-post.exe', '--config', 'teams-post.toml']   # run from this file's folder
never_terms = ['\bproject\s+x\b']     # regular expressions (case ignored): anything containing one is never posted
org_domains = ["example.com"]         # an attendee at any other domain is external
share_name = "Alex"                   # the summary's footer (default: owner)
share_state = "..."                   # optional: what was posted (default %LOCALAPPDATA%\meeting-summaries\shared.json)
share_folder = "..."                  # optional: the message files posted (default %LOCALAPPDATA%\meeting-summaries\share)
graph_scopes = ["Calendars.Read", "Chat.Read"]   # what the calendar is read with (the defaults); a base gives the sign-in

[[share]]
series = '^weekly-sync$'              # the folder the transcript is filed in; or title = '...' (the meeting's title); or both
organiser = 'alex'                    # optional: the organiser's name or email must match too
transcript = true                     # post the transcript when the hook is given it
summary = true                        # `share summary` may post the summary
key_points = false                    # the attendee version includes Key points (default false)
file = "markdown"                     # the transcript as filed; or "docx", a .docx of the same name beside it
external = false                      # a meeting with anyone outside org_domains is held back unless true
why = "The team asked for a record of the weekly sync."   # required
```

The first rule that names the meeting (and its organiser, if it says) applies. A rule can name any meeting, yours or
someone else's: if someone else organises it, say so in its `why`, since a posted copy goes round who they let download
the transcript.

**Which meeting, and its chat.** The transcript's first line is the recording's name as Teams writes it
(`**Weekly sync-20261112_090017UTC-Meeting Recording**`): the title and when the recording started. The calendar event
with that subject (letters and digits compared, as Teams drops some punctuation) whose time contains that start gives
the organiser, the attendees (rooms left out) and the join link, whose "meetup-join" form carries the chat's id; a short
`teams.microsoft.com/meet/...` link is matched against your meeting chats' join links instead. It only reads: Graph
`GET /me/calendarView` (**Calendars.Read**) and, for a short link, `GET /me/chats` (**Chat.Read**), delegated, on the
sign-in the settings (or the base) name. The post itself is teams-post's: **ChatMessage.Send**, **Chat.Read**,
**User.Read**, and **Files.ReadWrite** for the transcript's attachment.

### Hard stops

Whatever a rule says, a post is held back when:

- **it's a 1:1**: two people in the calendar event, or a title such as "1-2-1", "1:1" or "one to one";
- **the transcript or summary is under `team/` or `me/`** (where the right-hand kit keeps personnel records);
- **a `never_terms` pattern matches** the transcript, or the summary or its attendee version;
- for the summary: **its `data-sensitivity:` line keeps it out of chats, broadcasts or pre-reads**, it's **still a
  draft** (`status: draft` or `[DRAFT]`), or it **still has `[owner]` questions**;
- **someone invited is external** (an email outside `org_domains`, or `org_domains` not set), unless `external = true`;
- **the meeting isn't in the calendar**, or **its chat can't be found**;
- for the transcript: **teams-post can't attach it** because the sign-in doesn't hold Files.ReadWrite (its exit 3).

A held transcript is raised as a **`share/held`** item in `[review]`'s queue (`path`, `title`, `kind`, `reasons`); with
no queue, a notification. A never_terms match is named by its index, never by the term. If teams-post fails for any
other reason, it's a **`share/failed`** item and a notification. A held summary is said there and then.

### The record: never twice

- **Each post is recorded** in `share_state` (transcript, kind, chat, time), and **as a `shared:` line in the summary's
  frontmatter**: `shared: [transcript 2026-11-12 09:42, summary 2026-11-12 16:05]`. A transcript posted before its
  summary exists is carried into the draft when it's written.
- **A transcript is posted once**: the state says it was. **A summary is posted once**: its frontmatter says it was,
  and `share summary` says how to correct it instead (`teams-post edit` on its message file in `share_folder`).
  teams-post also refuses a file twice in a chat.

```
meeting-summaries share summary meetings/weekly-sync/summaries/2026-11-12.md          # checks, then teams-post's preview
meeting-summaries share summary meetings/weekly-sync/summaries/2026-11-12.md --yes    # posts it, and records it
```

Exit codes: 0 previewed, posted, or not opted in (it says "Not opted in"); 1 held back, already posted, or teams-post
refused; 2 settings or arguments; otherwise teams-post's own. teams-post runs from this settings file's folder with its
input closed, so it never opens a browser to sign in.

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
