"""The settings file.

    workspace = "."                   # the folder Claude works in, read-only: your notes, or a repo
    model = "opus"
    owner = "You"                     # the label on "For review" questions only you can answer
    template = "templates/summary.md" # optional: the summary's shape (relative to workspace)
    style = "summary-style.md"        # optional: your house rules, read into the prompt (relative to this file)
    context = ["people.md"]           # optional: files Claude reads for who people are (relative to workspace)
    ledger = "review-ledger.md"       # optional: questions asked and answers given, so none is asked twice
    include = ["meetings/**"]         # optional: which transcripts `hook` drafts (globs, relative to workspace)
    exclude = ["**/one-to-ones/**"]   # optional: and which it never does
    summaries = "summaries"           # the folder beside each transcript that its summary goes in
    log_file = "..."                  # optional
    after_draft = ["python", "x.py"]  # optional: run after each draft, with a JSON report on stdin
    timeout_minutes = 30

Paths may use ~ and %VARIABLES%.
"""

from __future__ import annotations

import fnmatch
import os
import pathlib
import tomllib
from dataclasses import dataclass, field

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "meeting-summaries"
DEFAULT_CONFIG = APPDATA / "meeting-summaries" / "config.toml"

TEMPLATE = """\
# meeting-summaries settings. See the README.
# The folder holding your transcripts (teams-transcripts' output folder, say).
workspace = "~/Documents/Meetings"
owner = "You"
"""


@dataclass
class Config:
    path: pathlib.Path
    workspace: pathlib.Path
    model: str = "opus"
    owner: str = "You"
    template: pathlib.Path | None = None
    style: str = ""
    context: list = field(default_factory=list)
    ledger: pathlib.Path | None = None
    include: list = field(default_factory=lambda: ["**"])
    exclude: list = field(default_factory=list)
    summaries: str = "summaries"
    log_file: pathlib.Path = LOCAL / "meeting-summaries.log"
    after_draft: list = field(default_factory=list)
    timeout_minutes: int = 30

    def relative(self, path: pathlib.Path) -> str:
        return path.resolve().relative_to(self.workspace).as_posix()

    def wanted(self, transcript: pathlib.Path) -> bool:
        """Whether `hook` drafts this transcript: inside the workspace, matching include and not exclude,
        and not itself a summary."""
        try:
            rel = self.relative(transcript)
        except ValueError:
            return False
        if f"/{self.summaries}/" in f"/{rel}" or transcript.suffix.lower() not in (".md", ".txt"):
            return False
        return any(fnmatch.fnmatch(rel, g) for g in self.include) and not any(fnmatch.fnmatch(rel, g) for g in self.exclude)


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(value)))
    return (path if path.is_absolute() else base / path).resolve()


def load(path: pathlib.Path | str | None = None) -> Config:
    path = pathlib.Path(path or os.environ.get("MEETING_SUMMARIES_CONFIG") or DEFAULT_CONFIG).resolve()
    if not path.exists():
        raise FileNotFoundError(f"No settings file at {path}. Run `meeting-summaries config` to create one.")
    raw = tomllib.loads(path.read_text(encoding="utf-8"))
    base = path.parent
    workspace = _path(raw.get("workspace", "."), base)
    if not workspace.is_dir():
        raise ValueError(f"workspace in {path} isn't a folder: {workspace}")
    style = ""
    if raw.get("style"):
        style_path = _path(raw["style"], base)
        if not style_path.exists():
            raise ValueError(f"style in {path} names a file that isn't there: {style_path}")
        style = style_path.read_text(encoding="utf-8")
    config = Config(
        path=path, workspace=workspace, model=raw.get("model", "opus"), owner=raw.get("owner", "You"),
        template=_path(raw["template"], workspace) if raw.get("template") else None, style=style,
        context=list(raw.get("context", [])), ledger=_path(raw["ledger"], workspace) if raw.get("ledger") else None,
        include=list(raw.get("include", ["**"])), exclude=list(raw.get("exclude", [])),
        summaries=raw.get("summaries", "summaries"),
        log_file=_path(raw["log_file"], base) if raw.get("log_file") else LOCAL / "meeting-summaries.log",
        after_draft=list(raw.get("after_draft", [])), timeout_minutes=int(raw.get("timeout_minutes", 30)))
    for name in ("template",):
        value = getattr(config, name)
        if value and not value.exists():
            raise ValueError(f"{name} in {path} names a file that isn't there: {value}")
    return config


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
