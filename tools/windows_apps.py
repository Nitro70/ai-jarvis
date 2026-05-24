"""Launch any app installed in the Windows Start Menu. Windows-only."""

from __future__ import annotations

import difflib
import json
import logging
import os
import subprocess

from core.tools import Tool

log = logging.getLogger("jarvis.tools.windows_apps")

# Cache of {display_name: AppID} from Get-StartApps.
_start_apps: dict[str, str] | None = None


def _load_start_apps() -> dict[str, str]:
    global _start_apps
    if _start_apps is not None:
        return _start_apps
    if os.name != "nt":
        _start_apps = {}
        return _start_apps
    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command",
             "Get-StartApps | ConvertTo-Json -Compress"],
            capture_output=True, text=True, timeout=20,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        if result.returncode != 0:
            log.warning("Get-StartApps failed: %s", result.stderr.strip())
            _start_apps = {}
            return _start_apps
        data = json.loads(result.stdout or "[]")
        if isinstance(data, dict):
            data = [data]
        _start_apps = {
            a["Name"]: a["AppID"]
            for a in data
            if isinstance(a, dict) and a.get("Name") and a.get("AppID")
        }
        log.info("loaded %d Start Menu apps", len(_start_apps))
    except Exception:  # noqa: BLE001
        log.exception("failed to enumerate Start Menu apps")
        _start_apps = {}
    return _start_apps


def _match_app(name: str, apps: dict[str, str]) -> str | None:
    name_lower = name.lower()
    names = list(apps.keys())
    for n in names:
        if n.lower() == name_lower:
            return n
    starts = [n for n in names if n.lower().startswith(name_lower)]
    if starts:
        return min(starts, key=len)
    subs = [n for n in names if name_lower in n.lower()]
    if subs:
        return min(subs, key=len)
    fuzzy = difflib.get_close_matches(name, names, n=1, cutoff=0.6)
    return fuzzy[0] if fuzzy else None


async def _open_app(args: dict) -> str:
    if os.name != "nt":
        return "App launching by name is currently Windows-only."
    name = (args.get("name") or "").strip()
    if not name:
        return "No app name provided."
    apps = _load_start_apps()
    if not apps:
        return "Can't read the Start Menu — app launching is unavailable."
    matched = _match_app(name, apps)
    if not matched:
        return f"No installed app matching '{name}'."
    appid = apps[matched]
    log.info("open_app: %r → %s (%s)", name, matched, appid)
    try:
        os.startfile(f"shell:AppsFolder\\{appid}")
    except OSError as e:
        return f"Failed to launch {matched}: {e}"
    return f"Opened {matched}."


def get_tools(_config: dict) -> list[Tool]:
    if os.name != "nt":
        log.info("windows_apps disabled (non-Windows OS)")
        return []
    return [
        Tool(
            name="open_app",
            description=(
                "Launch an app installed on the user's PC by name. Uses fuzzy "
                "matching against the Windows Start Menu, so partial or "
                "approximate names work ('chrome', 'discord', 'visual studio')."
            ),
            input_schema={
                "type": "object",
                "properties": {"name": {"type": "string"}},
                "required": ["name"],
            },
            handler=_open_app,
        ),
    ]
