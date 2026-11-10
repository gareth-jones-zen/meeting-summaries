"""The drafting prompt: the tool's own rules, then the settings' house rules, which win where they differ."""

from __future__ import annotations

from .config import Config

CORE = """Write a summary of this meeting from its transcript, as a draft for {owner} to review.

Transcript: {paths}

Read the whole transcript, in several Read calls if it's long; never summarise from part of it.
{previous}{template}{context}Read anything else in this folder you need to make sense of what's said, but stay with what was said in this meeting.

The shape:
- Frontmatter: date, title (as in the transcript's first line, without any recording suffix), attendees,
  topics (lowercase keywords that searches will hit), duration, transcript: ../{name}, and status: draft.
- Then "# [DRAFT] <title>", then a "## For review" section, then these headings: Purpose, Key points
  (with ### subheadings by topic), Decisions, Actions, Open questions, Transcript notes.
- "## For review" is short bullets. It covers anything you deliberately left out and why, names or terms you
  couldn't resolve, and any statement whose speaker you're unsure of. If there's nothing, say so.
  Start every bullet with a label, and put all the [{owner}] bullets first:
  - **[{owner}]** for what only {owner} can answer: what they meant where their own words are garbled, their
    own plans or commitments, and calls that are theirs to make. Ask it as a question they can answer in a
    word or a line.
  - **[Noted]** for everything else: a reading you chose and recorded, a name you inferred, what you left out,
    an unresolved name nobody in the meeting could settle. The reviewer accepts these unless they look wrong,
    so don't dress a guess up as a question.
  A script adds a [Check] bullet on the quotes afterwards; don't write one yourself.
{ledger}- Keep the numbers, and the names of systems, vendors and people. Record disagreements and who held which
  position. Quote short verbatim phrases in italics where they carry the point; a quotation must be the
  speaker's words, never a paraphrase in quotation marks.
- Actions name their owner.
- Transcript notes list speech-to-text manglings and their likely correct form.
- Plain English, short sentences, bold for the point of each bullet.
{style}
Output only the finished markdown file, starting with the opening "---" of the frontmatter. No preamble,
no code fences, no commentary after it."""

LEDGER = """- Before writing any [{owner}] question, read {ledger}. Its Settled answers are {owner}'s own, so use them
  as fact and never ask them again. A question already under Open has been put to them: don't repeat it as
  [{owner}], but say in a [Noted] bullet that it's open in the ledger.
"""


def build(config: Config, transcripts: list, previous_folder: str | None) -> str:
    names = [p.rsplit("/", 1)[-1] for p in transcripts]
    text = CORE.format(
        owner=config.owner, paths=", ".join(transcripts), name=names[0] if len(names) == 1 else names,
        previous=(f"Also read the two most recent summaries in {previous_folder}, if any, for format and context.\n"
                  if previous_folder else ""),
        template=(f"Follow the template {config.relative(config.template)} for the summary's shape.\n"
                  if config.template else ""),
        context=(f"Also read, for who people are: {', '.join(config.context)}.\n" if config.context else ""),
        ledger=LEDGER.format(owner=config.owner, ledger=config.relative(config.ledger)) if config.ledger else "",
        style=(f"\nHouse rules. Where they differ from the above, they win:\n\n{config.style.strip()}\n" if config.style else ""))
    if len(transcripts) > 1:
        text += ("\n\nThis meeting was split across recordings. Read every part; write one summary for the whole "
                 "meeting, with transcript: [" + ", ".join(f"../{n}" for n in names) + "] in the frontmatter.")
    return text
