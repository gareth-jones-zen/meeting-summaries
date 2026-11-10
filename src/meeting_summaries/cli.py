"""meeting-summaries: a draft summary of each meeting transcript, written by Claude Code for you to review.

    meeting-summaries config                 create the settings file (if missing) and say where it is
    meeting-summaries draft <transcript>     draft one, into summaries/ beside it (--out to write elsewhere)
    meeting-summaries hook                   for teams-transcripts' after_run: queue a draft of each new
                                             transcript, then start working through the queue
    meeting-summaries check <summary>...     check a summary's quotes against its transcript
    meeting-summaries autostart on           work through queued drafts every 15 minutes (off, status)
"""

from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys

from claude_headless import queue
from win_background import NO_WINDOW, logger, notify

from . import __version__, config as cfg, draft, quote_check


def main(argv: list | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="meeting-summaries", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\meeting-summaries\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    p = sub.add_parser("draft", help="draft one transcript's summary")
    p.add_argument("transcript", type=pathlib.Path)
    p.add_argument("--out", type=pathlib.Path, help="write the draft here instead (testing); nothing else is touched")
    sub.add_parser("hook", help="queue drafts of the transcripts in the JSON on stdin, then start the queue")
    p = sub.add_parser("check", help="check summaries' quotes against their transcripts")
    p.add_argument("summaries", nargs="+", type=pathlib.Path)
    p = sub.add_parser("autostart", help="work through queued drafts every 15 minutes")
    p.add_argument("action", choices=["on", "off", "status"])
    args = parser.parse_args(argv)

    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    if args.command == "check":
        return _check(args.summaries)
    if args.command == "autostart":   # the queue belongs to the seat, so it's claude-headless's task
        from claude_headless.cli import main as headless
        return headless(["autostart", args.action])
    try:
        config = cfg.load(args.config)
    except (FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 2
    log = logger(config.log_file, stamp_echo=False)

    if args.command == "hook":
        return _hook(config, log)
    transcript = args.transcript.resolve()
    if not transcript.exists():
        log(f"no such transcript: {transcript}")
        return 2
    try:
        status = draft.write_draft(config, transcript, log, out=args.out.resolve() if args.out else None)
        return 1 if status == "limit" else 0
    except Exception as error:
        log(f"{transcript.name}: FAILED: {error}")
        notify("Summary draft failed", [transcript.name, f"Log: {config.log_file}"])
        return 1


def _hook(config: cfg.Config, log) -> int:
    """stdin: teams-transcripts' after_run report (a list of results with "path"), doc-filer's ({"written": [...]}),
    or a plain list of paths."""
    raw = sys.stdin.read()
    try:
        data = json.loads(raw) if raw.strip() else []
    except json.JSONDecodeError:
        data = [line.strip() for line in raw.splitlines() if line.strip()]
    items = data.get("written", []) if isinstance(data, dict) else data
    paths = [pathlib.Path(i.get("path") or i.get("file") or "" if isinstance(i, dict) else i) for i in items]
    wanted = [p.resolve() for p in paths if str(p) and p.exists() and config.wanted(p)]
    for transcript in wanted:
        queue.add([sys.executable, "-m", "meeting_summaries", "--config", str(config.path), "draft", str(transcript)], config.workspace)
        log(f"queued a draft: {config.relative(transcript)}")
    if wanted or queue.pending():
        # Work through the queue in the background, so the hook returns at once; a second runner just exits.
        subprocess.Popen([sys.executable, "-m", "claude_headless", "run-queue"], creationflags=NO_WINDOW | 0x00000008,
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return 0


def _check(paths: list) -> int:
    worst = 0
    for path in paths:
        summary = path.read_text(encoding="utf-8")
        count, doubtful = quote_check.check(summary, quote_check.transcripts_for(path, summary))
        print(f"{path}: {count} quotes, {len(doubtful)} to verify")
        for quote, why, found in doubtful:
            print(f"  \"{quote}\" ({why})\n      transcript: {found}")
        worst = max(worst, len(doubtful))
    return 1 if worst else 0
