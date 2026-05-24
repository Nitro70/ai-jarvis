"""DANGER ZONE — opt-in shell + file access tools.

This module gives the LLM the ability to run arbitrary shell commands and
read/write any file. A mistranscription or prompt-injection can wipe data,
exfiltrate credentials, install software, or worse.

The tools refuse to load unless BOTH:
    tools.dangerous_shell.enabled: true
    tools.dangerous_shell.i_understand_the_risks: true
are set in config.yaml. This is intentional friction — accidentally turning
on shell access should be hard.

Admin / elevation:
    These tools run at WHATEVER privilege level JARVIS itself is running at.
    They do NOT request elevation per-command. To give JARVIS admin access,
    right-click your terminal / launcher → Run as administrator before
    starting jarvis.py. The admin status is logged at startup.
"""

from __future__ import annotations

import asyncio
import ctypes
import logging
import os
import shutil
from pathlib import Path

from core.tools import Tool

log = logging.getLogger("jarvis.tools.dangerous_shell")

# Defaults overridden by config.
_SHELL = "powershell" if os.name == "nt" else "bash"
_TIMEOUT = 60
_MAX_OUTPUT_CHARS = 4000


def _is_admin() -> bool:
    """Best-effort: are we running with elevated privileges?"""
    if os.name == "nt":
        try:
            return bool(ctypes.windll.shell32.IsUserAnAdmin())
        except Exception:  # noqa: BLE001
            return False
    return os.geteuid() == 0  # type: ignore[attr-defined]


def _truncate(text: str) -> str:
    if len(text) <= _MAX_OUTPUT_CHARS:
        return text
    return text[:_MAX_OUTPUT_CHARS] + f"\n... [truncated, total {len(text)} chars]"


# ---------------- Tool handlers ----------------

async def _run_shell(args: dict) -> str:
    command = (args.get("command") or "").strip()
    if not command:
        return "No command provided."
    log.warning("run_shell: %s", command)

    try:
        if _SHELL == "powershell":
            argv = ["powershell", "-NoProfile", "-NonInteractive", "-Command", command]
        elif _SHELL == "cmd":
            argv = ["cmd", "/c", command]
        else:
            argv = [_SHELL, "-c", command]

        proc = await asyncio.create_subprocess_exec(
            *argv,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        try:
            stdout, stderr = await asyncio.wait_for(
                proc.communicate(), timeout=_TIMEOUT,
            )
        except asyncio.TimeoutError:
            proc.kill()
            return f"Command timed out after {_TIMEOUT}s and was killed."
    except FileNotFoundError as e:
        return f"Shell not found: {e}"
    except Exception as e:  # noqa: BLE001
        return f"Failed to run command: {e}"

    out = stdout.decode("utf-8", errors="replace")
    err = stderr.decode("utf-8", errors="replace")
    parts = []
    if out.strip():
        parts.append(out.rstrip())
    if err.strip():
        parts.append(f"[stderr]\n{err.rstrip()}")
    combined = "\n\n".join(parts) if parts else f"(no output, exit code {proc.returncode})"
    if proc.returncode != 0:
        combined += f"\n\n[exit code {proc.returncode}]"
    return _truncate(combined)


async def _read_file(args: dict) -> str:
    path = (args.get("path") or "").strip()
    if not path:
        return "No path provided."
    p = Path(path).expanduser()
    log.warning("read_file: %s", p)
    if not p.exists():
        return f"File not found: {p}"
    if p.is_dir():
        return f"That's a directory, not a file: {p}"
    try:
        try:
            text = p.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            text = p.read_bytes().decode("utf-8", errors="replace")
    except Exception as e:  # noqa: BLE001
        return f"Failed to read: {e}"
    return _truncate(text) or "(empty file)"


async def _write_file(args: dict) -> str:
    path = (args.get("path") or "").strip()
    content = args.get("content")
    if content is None:
        return "No content provided."
    if not path:
        return "No path provided."
    p = Path(path).expanduser()
    log.warning("write_file: %s (%d chars)", p, len(content))
    try:
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(str(content), encoding="utf-8")
    except Exception as e:  # noqa: BLE001
        return f"Failed to write: {e}"
    return f"Wrote {len(str(content))} chars to {p}"


async def _list_directory(args: dict) -> str:
    path = (args.get("path") or ".").strip()
    p = Path(path).expanduser()
    log.warning("list_directory: %s", p)
    if not p.exists():
        return f"Path does not exist: {p}"
    if not p.is_dir():
        return f"Not a directory: {p}"
    try:
        entries = sorted(p.iterdir(), key=lambda e: (not e.is_dir(), e.name.lower()))
    except Exception as e:  # noqa: BLE001
        return f"Failed to list: {e}"
    lines = []
    for e in entries:
        marker = "/" if e.is_dir() else ""
        try:
            size = "" if e.is_dir() else f"  ({e.stat().st_size} bytes)"
        except OSError:
            size = ""
        lines.append(f"{e.name}{marker}{size}")
    if not lines:
        return f"(empty directory: {p})"
    return _truncate(f"{p}:\n" + "\n".join(lines))


# ---------------- Module entry ----------------

def get_tools(config: dict) -> list[Tool]:
    global _SHELL, _TIMEOUT, _MAX_OUTPUT_CHARS

    if not config.get("enabled"):
        return []
    if not config.get("i_understand_the_risks"):
        log.warning(
            "dangerous_shell is enabled but i_understand_the_risks is false — "
            "tools will NOT be loaded. Set both flags in config.yaml to use this."
        )
        return []

    _SHELL = config.get("shell", _SHELL).lower()
    _TIMEOUT = int(config.get("timeout_seconds", _TIMEOUT))
    _MAX_OUTPUT_CHARS = int(config.get("max_output_chars", _MAX_OUTPUT_CHARS))

    log.warning(
        "=" * 60 + "\n"
        " DANGEROUS_SHELL TOOLS ENABLED\n"
        "   shell:    %s\n"
        "   admin:    %s\n"
        "   timeout:  %ds\n"
        " The LLM can now run arbitrary commands and read/write files.\n"
        " " + ("All actions will be at ADMINISTRATOR privilege."
               if _is_admin() else
               "Actions run as the current (non-admin) user.") + "\n"
        + "=" * 60,
        _SHELL, _is_admin(), _TIMEOUT,
    )

    return [
        Tool(
            name="run_shell",
            description=(
                f"Run a shell command via {_SHELL} and return its stdout/stderr. "
                "Has full system access at the privilege level Jarvis is "
                "running with. Use sparingly and only when the user clearly "
                "wants a shell action. Commands have a "
                f"{_TIMEOUT}-second timeout."
            ),
            input_schema={
                "type": "object",
                "properties": {
                    "command": {
                        "type": "string",
                        "description": (
                            f"Single command line for {_SHELL}. For "
                            "PowerShell, semicolons separate statements; "
                            "for bash, use && / ;"
                        ),
                    },
                },
                "required": ["command"],
            },
            handler=_run_shell,
        ),
        Tool(
            name="read_file",
            description=(
                "Read the contents of any file on disk. Returns the file's "
                "text (truncated if very large). Use for inspecting configs, "
                "logs, source files, etc."
            ),
            input_schema={
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "Absolute or ~-relative path."},
                },
                "required": ["path"],
            },
            handler=_read_file,
        ),
        Tool(
            name="write_file",
            description=(
                "Write content to a file, creating parent directories as "
                "needed. OVERWRITES the existing file. Use deliberately — "
                "this can destroy data."
            ),
            input_schema={
                "type": "object",
                "properties": {
                    "path": {"type": "string"},
                    "content": {"type": "string"},
                },
                "required": ["path", "content"],
            },
            handler=_write_file,
        ),
        Tool(
            name="list_directory",
            description="List the contents of a directory.",
            input_schema={
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "Defaults to current directory."},
                },
            },
            handler=_list_directory,
        ),
    ]
