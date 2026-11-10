"""Check a summary's quotes against its transcript.

    meeting-summaries check summaries/2026-11-06.md

Every quotation in the summary (outside the frontmatter and the Transcript notes, which quote
manglings on purpose) is looked for in the transcript, ignoring case, punctuation, timestamps and
speaker labels. One that isn't there word for word is reported, with the nearest wording the
transcript has, if under 75% of its words appear there in order, or if it has a negation ("not",
"never", "doesn't"...) the transcript's wording doesn't.

Built 9 Nov 2026, after a review of 13 drafts: about 750 quotes, of which four didn't say
what the transcript says (a "not" added, two paraphrases in quotation marks, a bleeped word
cleaned up). Every draft is checked, and the result goes at the top of "For review".
"""

import difflib
import pathlib
import re
import sys

THRESHOLD = 0.75   # 3 of 4 words is a misheard word, not a misquote
QUOTE = re.compile(r"[\"“]([^\"”\n*]{15,}?)[\"”]")
SPLIT = re.compile(r"…|\.\.\.|\[[^\]]*\]")   # an ellipsis or [inserted words] joins fragments


NEGATIONS = {"not", "no", "never", "nobody", "nobodys", "none", "nothing", "noone", "nor", "cant", "cannot",
             "dont", "doesnt", "didnt", "wont", "wouldnt", "isnt", "arent", "wasnt", "werent", "havent",
             "hasnt", "hadnt", "shouldnt", "couldnt", "unrelated"}


def norm(text: str) -> str:
    """Lower case, apostrophes dropped (so "em's" and "EMs" agree), punctuation to spaces."""
    text = text.lower().replace("’", "").replace("‘", "").replace("'", "")
    text = re.sub(r"[^a-z0-9 ]+", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def flatten(transcript: str) -> str:
    """The transcript as one stream of words: timestamps and the bold speaker labels go."""
    text = re.sub(r"\*\*[^*\n]+\*\*\s+\d+:\d+(?::\d+)?", " ", transcript)
    return norm(re.sub(r"\d+:\d+(?::\d+)?", " ", text))


def body(summary: str) -> str:
    """The part of a summary whose quotes should be the speakers' words."""
    text = re.sub(r"\A---\n.*?\n---\n", "", summary, flags=re.S)
    return re.split(r"^## Transcript notes", text, flags=re.M)[0]


def nearest(fragment: list, words: list) -> tuple:
    """The transcript passage that best contains the fragment, as (the share of the fragment's words
    found there in order, the fragment's words that weren't, the word just before the passage, the
    passage). The passage may have a few extra words, so a stutter, a filler or the other speaker's
    "yeah" doesn't count against the quote."""
    n, first = len(fragment), set(fragment[:3])
    best, missing, before, span = 0.0, fragment, "", []
    if not words:   # an empty transcript: nothing is found (it used to raise IndexError)
        return best, missing, before, span
    for i in range(max(1, len(words) - n + 1)):
        if words[i] not in first and (i + 1 >= len(words) or words[i + 1] not in first):
            continue
        window = words[i:i + n + 4]
        blocks = [b for b in difflib.SequenceMatcher(None, fragment, window, autojunk=False).get_matching_blocks()
                  if b.size]
        recall = sum(b.size for b in blocks) / n
        if recall > best:
            matched = {k for b in blocks for k in range(b.a, b.a + b.size)}
            lo, hi = i + blocks[0].b, i + blocks[-1].b + blocks[-1].size
            best, span = recall, words[lo:hi]
            missing = [w for k, w in enumerate(fragment) if k not in matched]
            before = words[lo - 1] if lo else ""
    return best, missing, before, span


def check(summary: str, transcript: str) -> tuple:
    """(how many quotes were checked, [(quote, why, the transcript's nearest wording)]).

    A quote is reported if under 75% of its words are found in order, or if it has a negation
    ("not", "never", "nobody", "doesn't"...) that the transcript's wording doesn't: an added
    "not" reads fine and inverts the meaning (9 Nov 2026: "It's that complex. I'm not
    worried" became "It's not that complex")."""
    flat = flatten(transcript)
    words = flat.split()
    quotes = QUOTE.findall(body(summary))
    doubtful = []
    for quote in quotes:
        for piece in SPLIT.split(quote):
            fragment = norm(piece).split()
            if len(fragment) < 4 or " ".join(fragment) in flat:
                continue
            recall, missing, before, span = nearest(fragment, words)
            found = " ".join(span)
            # A negation in the quote that the transcript doesn't have, unless the word just before
            # the passage is one ("not the whole" for "wasn't the whole").
            if any(w in NEGATIONS for w in missing) and before not in NEGATIONS:
                doubtful.append((quote, "it has a negation the transcript doesn't", found))
                break
            if recall < THRESHOLD:
                doubtful.append((quote, f"{recall:.0%} of its words found", found))
                break
    return len(quotes), doubtful


def transcripts_for(summary_path: pathlib.Path, summary: str) -> str:
    field = re.search(r"^transcript:\s*(.+)$", summary, re.M)
    if not field:
        raise ValueError(f"{summary_path} has no transcript: field")
    text = ""
    for name in re.findall(r"[^\[\],]+\.md", field.group(1)):
        path = (summary_path.parent / name.strip().strip("'\"")).resolve()
        text += path.read_text(encoding="utf-8") + "\n"
    return text


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    sys.stdout.reconfigure(encoding="utf-8")
    worst = 0
    for arg in sys.argv[1:]:
        path = pathlib.Path(arg)
        summary = path.read_text(encoding="utf-8")
        count, doubtful = check(summary, transcripts_for(path, summary))
        print(f"{path}: {count} quotes, {len(doubtful)} to verify")
        for quote, why, found in doubtful:
            print(f"  \"{quote}\" ({why})\n      transcript: {found}")
        worst = max(worst, len(doubtful))
    return 1 if worst else 0


if __name__ == "__main__":
    sys.exit(main())
