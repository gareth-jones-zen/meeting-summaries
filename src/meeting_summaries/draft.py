"""Drafting one meeting's summary.

- **The model only reads.** Claude Code runs headless in the workspace with Read, Grep and Glob, and no
  connectors; this module writes the file.
- **A split meeting is one meeting.** `x-part1.md`, `x-part2.md` get one summary, `summaries/x.md`, from
  every part; a later part's draft waits for the first.
- **A summary that already exists is never overwritten**, draft or finished.
- **Every quote is checked against the transcript** (quote_check.py), and the result goes at the top of
  "For review", so the reviewer checks only the quotes that need it.
- **Lines that may be instructions dictated to an assistant** ("Claude, add an item...") are put to the
  owner at the top of "For review" (dictation.py): a person judges every one.
- **Questions for the owner go in the ledger**, if there is one, so the next draft doesn't ask them again.
- **At the usage limit the draft is queued** (claude-headless) and notified once per limit.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import re
import subprocess
import sys

from claude_headless import LimitReached, queue, run
from win_background import NO_WINDOW, notify

from . import dictation, prompt, quote_check
from .config import Config

PART = re.compile(r"^(?P<base>.+)-part(?P<n>\d+)$")


def parts(transcript: pathlib.Path) -> list:
    match = PART.match(transcript.stem)
    if not match:
        return [transcript]
    return sorted(transcript.parent.glob(f"{match['base']}-part*{transcript.suffix}"), key=lambda p: int(PART.match(p.stem)["n"]))


def summary_path(config: Config, transcript: pathlib.Path) -> pathlib.Path:
    match = PART.match(transcript.stem)
    return transcript.parent / config.summaries / (f"{match['base']}.md" if match else transcript.with_suffix(".md").name)


def with_quote_check(text: str, transcripts: list) -> str:
    try:
        source = "\n".join(p.read_text(encoding="utf-8") for p in transcripts)
        count, doubtful = quote_check.check(text, source)
    except Exception as error:
        bullet = f"- **[Check] Quotes weren't checked:** the quote check failed ({error}).\n"
    else:
        if not doubtful:
            bullet = f"- **[Check] Quotes:** all {count} match the transcript.\n"
        else:
            lines = [f"- **[Check] {len(doubtful)} of {count} quotes to verify against the transcript.** "
                     "Fix the wording, or take the quotation marks off a paraphrase:"]
            lines += [f"  - *\"{quote}\"*: {why}. The transcript has *{found}*." for quote, why, found in doubtful]
            bullet = "\n".join(lines) + "\n"
    heading = re.search(r"^## For review[ \t]*\n", text, re.M)
    if not heading:
        return text.replace("\n## Purpose", f"\n## For review\n\n{bullet}\n## Purpose", 1)
    after = re.search(r"^- \*\*\[Noted\]|^## ", text[heading.end():], re.M)   # after the owner's questions
    at = heading.end() + after.start() if after else len(text)
    if text[at:].startswith("## "):
        at = len(text[:at].rstrip("\n")) + 1
    return text[:at] + bullet + text[at:]


def with_dictation(text: str, transcripts: list, config: Config) -> tuple:
    """(the draft with a dictation bullet first in "For review", the candidates)."""
    names = dictation.NAMES if config.dictation is None else config.dictation
    found = [(f"{p.name}:{n}" if len(transcripts) > 1 else n, line)
             for p in transcripts for n, line in dictation.find(p.read_text(encoding="utf-8"), names)]
    bullet = dictation.bullet(found, config.owner, names)
    if not bullet:
        return text, found
    heading = re.search(r"^## For review[ \t]*\n\n?", text, re.M)
    if not heading:
        return text.replace("\n## Purpose", f"\n## For review\n\n{bullet}\n## Purpose", 1), found
    return text[:heading.end()] + bullet + text[heading.end():], found


def questions(text: str, owner: str) -> list:
    section = re.search(r"^## For review[ \t]*\n(.*?)(?=^## )", text, re.S | re.M)
    label = f"- **[{owner}]**"
    return [line[len(label):].strip() for line in (section.group(1).splitlines() if section else []) if line.startswith(label)]


def add_to_ledger(config: Config, text: str, summary: pathlib.Path) -> int:
    """The draft's owner questions under Open in the ledger (it must have a "## Settled" heading after Open)."""
    asked = questions(text, config.owner)
    if not asked or not config.ledger or not config.ledger.exists():
        return 0
    ledger = config.ledger.read_text(encoding="utf-8")
    if "\n## Settled" not in ledger:
        return 0
    rel = (summary.relative_to(config.ledger.parent).as_posix() if summary.is_relative_to(config.ledger.parent)
           else config.relative(summary))
    name = f"{summary.parent.parent.name} {summary.stem}"
    lines = "".join(f"- {dt.date.today().isoformat()} · [{name}]({rel}) · {q}\n" for q in asked if q not in ledger)
    if not lines:
        return 0
    head, tail = ledger.split("\n## Settled", 1)
    config.ledger.write_text(head.rstrip("\n") + "\n\n" + lines + "\n## Settled" + tail, encoding="utf-8")
    return lines.count("\n")


def write_draft(config: Config, transcript: pathlib.Path, log, out: pathlib.Path | None = None) -> str:
    """Draft and write. Returns "written", "exists", "waiting" (a later part), "queued" or "limit" (usage limit).
    out: write there instead, for comparing and testing: no ledger, no after_draft, never queued."""
    transcript = transcript.resolve()
    every = parts(transcript)
    if out is None and every[0] != transcript and every[0].exists():
        queue.add([sys.executable, "-m", "meeting_summaries", "--config", str(config.path), "draft", str(every[0])], config.workspace)
        log(f"{config.relative(transcript)}: part of a split meeting; drafted with {every[0].name}")
        return "waiting"
    target = out or summary_path(config, transcript)
    if out is None and target.exists():
        log(f"{config.relative(transcript)}: a summary already exists ({config.relative(target)}), not overwriting")
        return "exists"
    rel = [config.relative(p) for p in every]
    folder = summary_path(config, transcript).parent
    previous = folder if folder.exists() else None
    text_prompt = prompt.build(config, rel, config.relative(previous) + "/" if previous else None)
    command = ["-p", "--output-format", "json", "--model", config.model,
               "--tools", "Read,Grep,Glob", "--strict-mcp-config", "--permission-mode", "dontAsk"]
    try:
        result = run(command, text_prompt, config.workspace, timeout=config.timeout_minutes * 60)
    except LimitReached as limit:
        if out is not None:
            log(f"{rel[0]}: Claude's usage limit, resets {limit.reset:%H:%M}")
            return "limit"
        argv = [sys.executable, "-m", "meeting_summaries", "--config", str(config.path), "draft", str(transcript)]
        if queue.retry(argv, config.workspace, limit.reset):
            notify("Claude usage limit reached", f"Summary drafts will carry on after {limit.reset:%H:%M}.")
        log(f"{rel[0]}: Claude's usage limit; queued for after {limit.reset:%a %H:%M}")
        return "queued"
    text = re.sub(r"^```(?:markdown|md)?\s*\n|\n```\s*$", "", result["result"].strip())
    start = text.find("---")
    if start < 0 or "# [DRAFT]" not in text:
        raise RuntimeError("the reply wasn't a draft summary: " + text[:200])
    text = with_quote_check(text[start:].rstrip() + "\n", every)
    text, dictated = with_dictation(text, every, config)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8")
    added = 0 if out else add_to_ledger(config, text, target)
    log(f"{rel[0]}: draft written to {target if out else config.relative(target)} ({result.get('num_turns')} turns"
        + (f"; {added} question(s) added to the ledger" if added else "") + ")")
    if config.after_draft and out is None:
        report = {"transcript": rel[0], "transcripts": rel, "summary": config.relative(target),
                  "questions": questions(text, config.owner), "session": result.get("session_id"),
                  "dictation": [{"line": n, "text": line} for n, line in dictated]}
        try:
            done = subprocess.run(config.after_draft, input=json.dumps(report), text=True, cwd=config.path.parent,
                                  capture_output=True, timeout=300, creationflags=NO_WINDOW)
            if done.returncode:
                log(f"  after_draft exited {done.returncode}: {(done.stderr or done.stdout)[-300:]}")
        except Exception as error:
            log(f"  after_draft failed: {error}")
    return "written"
