"""Minimize / maximize / restore / focus / close windows. Windows-only.

Uses raw user32.dll via ctypes — no pywin32 required.

Includes a background tracker that remembers the most recent foreground
window that *isn't* our own Python process, so 'minimize this window' refers
to whatever the user was looking at before alt-tabbing to the JARVIS terminal."""

from __future__ import annotations

import asyncio
import ctypes
import difflib
import logging
import os

from core.tools import Tool

log = logging.getLogger("jarvis.tools.windows_state")

_user32 = None
if os.name == "nt":
    _user32 = ctypes.windll.user32
    _user32.GetWindowTextLengthW.restype = ctypes.c_int
    _user32.GetWindowTextW.restype = ctypes.c_int
    _user32.IsWindowVisible.restype = ctypes.c_bool
    _user32.IsWindow.restype = ctypes.c_bool

_SW_MINIMIZE = 6
_SW_MAXIMIZE = 3
_SW_RESTORE = 9
_WM_CLOSE = 0x0010

_last_user_window: int | None = None
_tracker_started = False


def _enum_visible_windows() -> list[tuple[int, str]]:
    if _user32 is None:
        return []
    results: list[tuple[int, str]] = []
    EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def _cb(hwnd, _lparam):
        if not _user32.IsWindowVisible(hwnd):
            return True
        length = _user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        _user32.GetWindowTextW(hwnd, buf, length + 1)
        title = buf.value.strip()
        if title:
            results.append((hwnd, title))
        return True

    _user32.EnumWindows(EnumProc(_cb), 0)
    return results


def _window_title(hwnd: int) -> str:
    if _user32 is None or not _user32.IsWindow(hwnd):
        return ""
    length = _user32.GetWindowTextLengthW(hwnd)
    if length <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(length + 1)
    _user32.GetWindowTextW(hwnd, buf, length + 1)
    return buf.value.strip()


_THIS_KEYWORDS = {
    "this", "this window", "current", "current window",
    "active", "active window", "the active window", "it",
}


def _find_window(name: str) -> tuple[int, str] | None:
    if _user32 is None:
        return None
    name = name.strip()
    if name.lower() in _THIS_KEYWORDS:
        if _last_user_window and _user32.IsWindow(_last_user_window):
            return (_last_user_window, _window_title(_last_user_window))
        return None
    windows = _enum_visible_windows()
    name_lower = name.lower()
    for hwnd, title in windows:
        if title.lower() == name_lower:
            return (hwnd, title)
    subs = [(h, t) for h, t in windows if name_lower in t.lower()]
    if subs:
        return min(subs, key=lambda ht: len(ht[1]))
    titles = [t for _, t in windows]
    close = difflib.get_close_matches(name, titles, n=1, cutoff=0.55)
    if close:
        for hwnd, title in windows:
            if title == close[0]:
                return (hwnd, title)
    return None


async def _foreground_tracker() -> None:
    """Background loop that records the user's last non-JARVIS foreground window."""
    global _last_user_window
    if _user32 is None:
        return
    own_pid = os.getpid()
    log.info("foreground tracker started (own pid=%d)", own_pid)
    while True:
        try:
            hwnd = _user32.GetForegroundWindow()
            if hwnd:
                pid = ctypes.c_ulong()
                _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                if pid.value != own_pid and _window_title(hwnd):
                    _last_user_window = hwnd
        except Exception:  # noqa: BLE001
            log.debug("foreground tracker poll failed", exc_info=True)
        await asyncio.sleep(0.3)


def start_foreground_tracker() -> None:
    """Call once from inside the event loop to begin tracking."""
    global _tracker_started
    if _tracker_started or _user32 is None:
        return
    _tracker_started = True
    asyncio.create_task(_foreground_tracker())


async def _minimize(args: dict) -> str:
    name = (args.get("name") or "this").strip()
    found = _find_window(name)
    if not found:
        return f"Can't find a window matching '{name}'."
    hwnd, title = found
    _user32.ShowWindow(hwnd, _SW_MINIMIZE)
    return f"Minimized {title}."


async def _maximize(args: dict) -> str:
    name = (args.get("name") or "this").strip()
    found = _find_window(name)
    if not found:
        return f"Can't find a window matching '{name}'."
    hwnd, title = found
    _user32.ShowWindow(hwnd, _SW_MAXIMIZE)
    return f"Maximized {title}."


async def _restore(args: dict) -> str:
    name = (args.get("name") or "this").strip()
    found = _find_window(name)
    if not found:
        return f"Can't find a window matching '{name}'."
    hwnd, title = found
    _user32.ShowWindow(hwnd, _SW_RESTORE)
    return f"Restored {title}."


async def _focus(args: dict) -> str:
    name = (args.get("name") or "").strip()
    if not name or name.lower() in _THIS_KEYWORDS:
        return "Need a specific window name to focus."
    found = _find_window(name)
    if not found:
        return f"Can't find a window matching '{name}'."
    hwnd, title = found
    _user32.ShowWindow(hwnd, _SW_RESTORE)
    _user32.SetForegroundWindow(hwnd)
    return f"Switched to {title}."


async def _close(args: dict) -> str:
    name = (args.get("name") or "this").strip()
    found = _find_window(name)
    if not found:
        return f"Can't find a window matching '{name}'."
    hwnd, title = found
    _user32.PostMessageW(hwnd, _WM_CLOSE, 0, 0)
    return f"Closed {title}."


async def _list_windows(_args: dict) -> str:
    windows = _enum_visible_windows()
    if not windows:
        return "No visible windows."
    titles = sorted({t for _, t in windows})
    return "Open windows: " + ", ".join(titles)


def get_tools(_config: dict) -> list[Tool]:
    if os.name != "nt":
        log.info("windows_state disabled (non-Windows OS)")
        return []

    _name_schema = {
        "type": "object",
        "properties": {"name": {"type": "string"}},
        "required": ["name"],
    }
    return [
        Tool("minimize_window",
             "Minimize a window. Pass 'this' or 'current' to minimize whatever "
             "window the user was last looking at (excludes the JARVIS terminal). "
             "Otherwise a fuzzy app/window name like 'Discord' or 'Chrome'.",
             _name_schema, _minimize),
        Tool("maximize_window",
             "Maximize a window (fullscreen the app). 'this'/'current' targets "
             "the user's last-focused window.",
             _name_schema, _maximize),
        Tool("restore_window",
             "Restore a minimized or maximized window to its normal size.",
             _name_schema, _restore),
        Tool("focus_window",
             "Bring a window to the foreground (alt-tab to it). Use for 'switch "
             "to X' or 'bring up X'.",
             _name_schema, _focus),
        Tool("close_window",
             "Close a window — equivalent to clicking the X. Apps with unsaved "
             "work will still prompt to save.",
             _name_schema, _close),
        Tool("list_open_windows",
             "List the titles of all currently visible top-level windows.",
             {"type": "object", "properties": {}},
             _list_windows),
    ]
