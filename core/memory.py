"""Memory file loader + writer.

The memory file is plain markdown. The user owns it. Jarvis reads it at
startup (HTML comments stripped) and appends it to the system prompt.
Jarvis can also write new facts under a "Learned" section via the remember
tool — see tools/memory.py.
"""

from __future__ import annotations

import logging
import re
from pathlib import Path

log = logging.getLogger("jarvis.memory")

# Matches HTML comments — including multi-line ones — so commented-out
# example facts in memory.example.md never reach the LLM.
_HTML_COMMENT = re.compile(r"<!--.*?-->", re.DOTALL)

# Header we append new learned facts under. If the user removes the section,
# we recreate it.
_LEARNED_HEADER = "# Learned"


def load_memory(path: str | Path) -> str:
    """Read the memory file, strip HTML comments, and return the live content.
    Returns an empty string if the file doesn't exist (no error)."""
    p = Path(path)
    if not p.exists():
        log.info("no memory file at %s — skipping", p)
        return ""
    raw = p.read_text(encoding="utf-8")
    stripped = _HTML_COMMENT.sub("", raw)
    # Collapse the runs of blank lines that comment-stripping leaves behind.
    cleaned = re.sub(r"\n{3,}", "\n\n", stripped).strip()
    log.info("loaded memory from %s (%d chars after stripping comments)",
             p, len(cleaned))
    return cleaned


def append_fact(path: str | Path, fact: str) -> bool:
    """Append a fact under the "Learned" header. Creates the file if missing.
    Returns True if appended, False if it was already present (dedupe)."""
    p = Path(path)
    fact = fact.strip()
    if not fact:
        return False
    if not fact.endswith("."):
        fact = fact + "."

    existing = p.read_text(encoding="utf-8") if p.exists() else ""

    # Dedupe: case-insensitive substring check against the file contents (with
    # comments stripped — we don't want to count placeholders as matches).
    visible = _HTML_COMMENT.sub("", existing).lower()
    if fact.lower().rstrip(".") in visible:
        log.info("fact already known, not appending: %r", fact)
        return False

    if _LEARNED_HEADER in existing:
        # Append a new bullet at the end of the file (within the Learned section).
        if not existing.endswith("\n"):
            existing += "\n"
        new = existing + f"- {fact}\n"
    else:
        # Create the section.
        new = existing.rstrip() + f"\n\n{_LEARNED_HEADER}\n\n- {fact}\n"

    p.write_text(new, encoding="utf-8")
    log.info("appended fact to %s: %r", p, fact)
    return True


def remove_facts_matching(path: str | Path, substring: str) -> int:
    """Remove every bullet line under the Learned section whose text contains
    `substring` (case-insensitive). Returns the number removed."""
    p = Path(path)
    if not p.exists():
        return 0
    sub = substring.strip().lower()
    if not sub:
        return 0
    lines = p.read_text(encoding="utf-8").splitlines()
    in_learned = False
    new_lines: list[str] = []
    removed = 0
    for line in lines:
        if line.strip().startswith("# ") or line.strip().startswith("## "):
            in_learned = (line.strip().lstrip("# ").strip().lower() == "learned")
        if in_learned and line.lstrip().startswith("- ") and sub in line.lower():
            removed += 1
            continue
        new_lines.append(line)
    if removed:
        p.write_text("\n".join(new_lines) + "\n", encoding="utf-8")
        log.info("removed %d facts matching %r from %s", removed, substring, p)
    return removed
