"""Run with: python -m unittest discover -s tests"""

import datetime as dt
import io
import json
import os
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from claude_headless import LimitReached, Queue  # noqa: E402

from meeting_summaries import cli, config as cfg, draft, prompt  # noqa: E402

TRANSCRIPT = """# Catch-up 2026-11-06

**Alex Smith** 0:01
We agreed the rollout goes ahead on Monday, and I'm worried about the dates.

**Sam Jones** 0:20
I'll write up the plan by Friday.
"""

DRAFT = """---
date: 2026-11-06
title: Catch-up
transcript: ../2026-11-06.md
status: draft
---

# [DRAFT] Catch-up

## For review

- **[Owner]** Did you mean Monday the 9th?
- **[Noted]** "Sam" taken to be Sam Jones.

## Purpose

Weekly catch-up.

## Key points

- Alex: *"the rollout goes ahead on Monday"*, and *"I'm not worried about the dates"*.

## Decisions

## Actions

## Open questions

## Transcript notes
"""

LEDGER = "# Review ledger\n\n## Open\n\n- 2026-11-01 · [x](x) · an older question\n\n## Settled\n\n- leave: 16-31 Dec\n"


class Base(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self.temp.name).resolve()
        (self.dir / "work" / "meetings" / "alex").mkdir(parents=True)
        self.transcript = self.dir / "work" / "meetings" / "alex" / "2026-11-06.md"
        self.transcript.write_text(TRANSCRIPT, encoding="utf-8")
        (self.dir / "work" / "ledger.md").write_text(LEDGER, encoding="utf-8")
        (self.dir / "style.md").write_text("- British English.", encoding="utf-8")
        (self.dir / "config.toml").write_text(
            'workspace = "work"\nowner = "Owner"\nstyle = "style.md"\nledger = "ledger.md"\n'
            'include = ["meetings/**"]\nexclude = ["**/private/**"]\nlog_file = "log.txt"\n', encoding="utf-8")
        self.config = cfg.load(self.dir / "config.toml")
        self.queue = Queue(self.dir / "queue")
        self.lines = []
        for target in ("meeting_summaries.draft.queue", "meeting_summaries.cli.queue"):
            patcher = mock.patch(target, self.queue)
            patcher.start()
            self.addCleanup(patcher.stop)

    def tearDown(self):
        self.temp.cleanup()

    def draft_with(self, reply=DRAFT, **kwargs):
        with mock.patch("meeting_summaries.draft.run", return_value={"result": reply, "num_turns": 3}) as run:
            status = draft.write_draft(self.config, self.transcript, self.lines.append, **kwargs)
        return status, run


class Settings(Base):
    def test_which_transcripts_the_hook_takes(self):
        self.assertTrue(self.config.wanted(self.transcript))
        self.assertFalse(self.config.wanted(self.dir / "work" / "meetings" / "alex" / "summaries" / "2026-11-06.md"))
        self.assertFalse(self.config.wanted(self.dir / "work" / "meetings" / "private" / "a.md"))
        self.assertFalse(self.config.wanted(self.dir / "elsewhere.md"))

    def test_prompt_carries_the_house_rules_and_ledger(self):
        text = prompt.build(self.config, ["meetings/alex/2026-11-06.md"], "meetings/alex/summaries/")
        self.assertIn("**[Owner]**", text)
        self.assertIn("read ledger.md", text)
        self.assertTrue(text.index("House rules") > text.index("The shape:"))
        self.assertIn("British English", text)
        split = prompt.build(self.config, ["a/x-part1.md", "a/x-part2.md"], None)
        self.assertIn("transcript: [../x-part1.md, ../x-part2.md]", split)


class Drafting(Base):
    def test_writes_the_draft_with_quotes_checked_and_questions_in_the_ledger(self):
        status, run = self.draft_with()
        self.assertEqual(status, "written")
        self.assertEqual(run.call_args.args[0][run.call_args.args[0].index("--tools") + 1], "Read,Grep,Glob")
        text = (self.transcript.parent / "summaries" / "2026-11-06.md").read_text(encoding="utf-8")
        check = text.index("[Check] 1 of 2 quotes")
        self.assertTrue(text.index("[Owner]") < check < text.index("[Noted]"))
        self.assertIn("negation", text)   # an added "not"
        ledger = (self.dir / "work" / "ledger.md").read_text(encoding="utf-8")
        self.assertIn("[alex 2026-11-06](meetings/alex/summaries/2026-11-06.md) · Did you mean Monday the 9th?", ledger)
        self.assertTrue(ledger.index("Monday the 9th") < ledger.index("## Settled"))

    def test_never_overwrites_a_summary(self):
        (self.transcript.parent / "summaries").mkdir()
        (self.transcript.parent / "summaries" / "2026-11-06.md").write_text("finished", encoding="utf-8")
        status, run = self.draft_with()
        self.assertEqual(status, "exists")
        run.assert_not_called()

    def test_out_touches_nothing_else(self):
        out = self.dir / "compare.md"
        self.assertEqual(self.draft_with(out=out)[0], "written")
        self.assertTrue(out.exists())
        self.assertFalse((self.transcript.parent / "summaries").exists())
        self.assertNotIn("Monday the 9th", (self.dir / "work" / "ledger.md").read_text(encoding="utf-8"))

    def test_a_later_part_waits_for_the_first(self):
        part1 = self.transcript.with_name("2026-11-07-part1.md")
        part2 = self.transcript.with_name("2026-11-07-part2.md")
        part1.write_text(TRANSCRIPT, encoding="utf-8")
        part2.write_text(TRANSCRIPT, encoding="utf-8")
        self.assertEqual(draft.write_draft(self.config, part2, self.lines.append), "waiting")
        self.assertEqual(self.queue.pending()[0]["argv"][-1], str(part1))
        self.assertEqual(draft.summary_path(self.config, part1).name, "2026-11-07.md")

    def test_the_usage_limit_queues_and_notifies_once(self):
        reset = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=2)
        with mock.patch("meeting_summaries.draft.run", side_effect=LimitReached(reset, "limit")), \
             mock.patch("meeting_summaries.draft.notify") as notify:
            self.assertEqual(draft.write_draft(self.config, self.transcript, self.lines.append), "queued")
            other = self.transcript.with_name("2026-11-08.md")
            other.write_text(TRANSCRIPT, encoding="utf-8")
            draft.write_draft(self.config, other, self.lines.append)
        self.assertEqual(notify.call_count, 1)
        self.assertEqual(len(self.queue.pending()), 2)

    def test_a_reply_that_isnt_a_draft_is_an_error(self):
        with self.assertRaises(RuntimeError):
            self.draft_with(reply="Sorry, I couldn't read the file.")


class Dictation(Base):
    LINES = """**Clark Kent** 0:11
Morning all.
Claude had a note for me to check with Sam about the rollout.
Clark, please remind me about the budget.
Sorry, I've just started doing this now.
We had a note from the supplier.
Claude is good at this.
Claude, I did items in my one to one with Dan tomorrow."""

    def test_finds_mangled_instructions_and_skips_speaker_headings(self):
        from meeting_summaries import dictation
        found = dictation.find(self.LINES)
        self.assertEqual([n for n, _ in found], [3, 4, 5, 8])   # not the heading, the supplier note, or the remark
        self.assertEqual(dictation.find(self.LINES, []), [])
        self.assertEqual([n for n, _ in dictation.find(self.LINES, ["claude"])], [3, 5, 8])

    def test_the_draft_puts_candidates_to_the_owner_first(self):
        self.transcript.write_text(TRANSCRIPT + "\n" + self.LINES, encoding="utf-8")
        self.draft_with()
        text = (self.transcript.parent / "summaries" / "2026-11-06.md").read_text(encoding="utf-8")
        review = text.split("## For review", 1)[1]
        self.assertTrue(review.lstrip().startswith("- **[Owner] Dictated instructions?** 4 lines"))
        self.assertIn("Claude had a note for me to check with Sam", review)
        # A dictation question is about this meeting only, so it isn't added to the ledger.
        self.assertNotIn("Dictated", (self.dir / "work" / "ledger.md").read_text(encoding="utf-8"))

    def test_none_found_adds_nothing(self):
        self.draft_with()
        self.assertNotIn("Dictated", (self.transcript.parent / "summaries" / "2026-11-06.md").read_text(encoding="utf-8"))


class Hook(Base):
    def test_queues_wanted_transcripts_from_any_report_shape(self):
        private = self.dir / "work" / "meetings" / "private" / "b.md"
        private.parent.mkdir()
        private.write_text("x", encoding="utf-8")
        reports = [[{"status": "written", "path": str(self.transcript)}, {"path": str(private)}],
                   {"written": [str(self.transcript)]}]
        for report in reports:
            with mock.patch("sys.stdin", io.StringIO(json.dumps(report))), mock.patch("subprocess.Popen") as popen:
                cli._hook(self.config, self.lines.append)
            popen.assert_called_once()
        self.assertEqual([i["argv"][-1] for i in self.queue.pending()], [str(self.transcript)])


if __name__ == "__main__":
    unittest.main()
