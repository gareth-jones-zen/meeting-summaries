"""Finding instructions dictated to an assistant in a meeting: "Claude, add an item for me to ...".

People who use an assistant learn to say things like that in meetings, and reasonably assume they're
picked up. In the EA repo they weren't, at first: an audit found nine dictated instructions in the
transcripts, of which three had reached the to-do list (September 2026).

**The sweep is deliberately wide, so a person judges every candidate.** Speech-to-text mangles both
halves: the name ("Claude" comes through as Clark, Cloud, Clawed, Clyde) and, worse, the verb ("Claude,
add a note" became "Claude had a note" and "Claude, I didn't add a note"), so a candidate can never be
judged on whether it parses as an instruction. Speaker headings ("**Clark Kent** 0:11") are skipped.
"""

from __future__ import annotations

import re

NAMES = ["claude", "clark", "cloud", "clawed", "claw", "clyde"]
ACTION = re.compile(r"\b(add|added|had|note|item|action|remind|inbox|please)\b", re.I)
# Where the item should go. The name plus a destination catches a mangled verb: on 10 Nov 2026, "Claude, I
# did items in my one to one with Dan" and "Claude, I don't have one to one with John" were missed by the
# verbs above. Across 326 transcripts it added those two and ten false positives.
DESTINATION = re.compile(r"\b(one[ -]to[ -]one|1[ :-]2[ :-]1|1:1|inbox|items|notes|actions|prep)\b", re.I)
PHRASE = re.compile(r"add an? (item|note|action)|note to my action|my inbox|just started doing this", re.I)


def find(transcript: str, names: list | None = None) -> list:
    """[(line number, text)] for each line that might be a dictated instruction. names=[] turns it off."""
    names = NAMES if names is None else names
    if not names:
        return []
    trigger = re.compile(r"\b(" + "|".join(re.escape(n) for n in names) + r")\b", re.I)
    found = []
    for number, line in enumerate(transcript.splitlines(), 1):
        if line.startswith("**"):
            continue
        if (trigger.search(line) and (ACTION.search(line) or DESTINATION.search(line))) or PHRASE.search(line):
            found.append((number, line.strip()[:200]))
    return found


def bullet(found: list, owner: str, names: list) -> str:
    """The "For review" bullet: a question for the owner, since only they know what they meant."""
    if not found:
        return ""
    lines = [f"- **[{owner}] Dictated instructions?** {len(found)} line{'s' if len(found) > 1 else ''} in the transcript "
             f"may be addressed to {names[0].title()}. Were they meant as instructions, and are they done?"]
    lines += [f"  - line {n}: *{text}*" for n, text in found]
    return "\n".join(lines) + "\n"
