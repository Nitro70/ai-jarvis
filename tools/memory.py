"""Memory tools: remember / forget / list. Write to the user's memory.md."""

from __future__ import annotations

import logging
from pathlib import Path

from core.memory import append_fact, load_memory, remove_facts_matching
from core.tools import Tool

log = logging.getLogger("jarvis.tools.memory")

# Set by get_tools() from config.
_MEMORY_PATH: Path = Path("memory.md")


async def _remember(args: dict) -> str:
    fact = (args.get("fact") or "").strip()
    if not fact:
        return "No fact provided."
    appended = append_fact(_MEMORY_PATH, fact)
    if appended:
        return f"Saved: {fact}"
    return f"I already know that — skipped."


async def _forget(args: dict) -> str:
    substring = (args.get("matching") or "").strip()
    if not substring:
        return "Need a substring to match against."
    removed = remove_facts_matching(_MEMORY_PATH, substring)
    if removed == 0:
        return f"Nothing in the Learned section matched '{substring}'."
    return f"Removed {removed} fact{'s' if removed != 1 else ''}."


async def _list_memories(_args: dict) -> str:
    content = load_memory(_MEMORY_PATH)
    if not content:
        return "Memory file is empty."
    return content


def get_tools(config: dict) -> list[Tool]:
    global _MEMORY_PATH
    _MEMORY_PATH = Path(config.get("path", "memory.md"))

    return [
        Tool(
            name="remember",
            description=(
                "Save a fact about the user (or about a preference, routine, "
                "or recurring detail) to long-term memory. Call this when the "
                "user shares something worth remembering across sessions — "
                "their name, preferences, recurring tasks, important dates, "
                "etc. — even if they don't explicitly say 'remember'. "
                "Idempotent: re-remembering a known fact is a no-op."
            ),
            input_schema={
                "type": "object",
                "properties": {
                    "fact": {
                        "type": "string",
                        "description": (
                            "A single concise fact, in plain English. Write "
                            "it from the user's perspective ('I prefer X') "
                            "or in the third person ('The user prefers X')."
                        ),
                    },
                },
                "required": ["fact"],
            },
            handler=_remember,
        ),
        Tool(
            name="forget",
            description=(
                "Remove facts from long-term memory whose text matches a "
                "substring (case-insensitive). Only removes from the Learned "
                "section — never touches the user's hand-written sections."
            ),
            input_schema={
                "type": "object",
                "properties": {
                    "matching": {
                        "type": "string",
                        "description": "Substring to match against memory entries.",
                    },
                },
                "required": ["matching"],
            },
            handler=_forget,
        ),
        Tool(
            name="list_memories",
            description=(
                "Read back the full contents of long-term memory. Use when "
                "the user asks 'what do you remember about me?' or similar."
            ),
            input_schema={"type": "object", "properties": {}},
            handler=_list_memories,
        ),
    ]
