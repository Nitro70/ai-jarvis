"""YouTube Music control via the th-ch/youtube-music desktop app's HTTP API.

Requires the app installed separately — see README for setup. The first run
triggers a pairing dialog in the app; click Allow once and the JWT is saved
to .ytmd_token next to this file."""

from __future__ import annotations

import asyncio
import json
import logging
import os
import subprocess
import time
from pathlib import Path
from typing import Any

import httpx

from core.tools import Tool

try:
    from ytmusicapi import YTMusic
except ImportError:
    YTMusic = None  # type: ignore

log = logging.getLogger("jarvis.tools.music_ytmd")

# Set by get_tools() from config.
_HOST = "127.0.0.1"
_PORT = 26538
_BASE_URL = f"http://{_HOST}:{_PORT}"
_EXE_PATH: Path | None = None
_TOKEN_PATH = Path(__file__).parent.parent / ".ytmd_token"
_CLIENT_ID = "jarvis"

_yt: "YTMusic | None" = None
_token: str | None = None
_http: httpx.AsyncClient | None = None


def _ytmusic() -> "YTMusic":
    global _yt
    if _yt is None:
        if YTMusic is None:
            raise RuntimeError(
                "ytmusicapi not installed — run: pip install -r requirements-music.txt"
            )
        _yt = YTMusic()
    return _yt


# ===== Auto-launch + pairing =====

async def _api_alive(timeout: float = 1.5) -> bool:
    try:
        async with httpx.AsyncClient(base_url=_BASE_URL, timeout=timeout) as c:
            await c.get("/")
        return True
    except httpx.RequestError:
        return False


async def _wait_for_api(timeout: float = 45.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if await _api_alive(timeout=2.0):
            return True
        await asyncio.sleep(0.5)
    return False


async def _ensure_app_running() -> None:
    if await _api_alive():
        return
    if _EXE_PATH is None or not _EXE_PATH.exists():
        raise RuntimeError(
            f"YT Music app isn't reachable at {_BASE_URL}. Either start it "
            "manually, or set tools.music_ytmd.exe_path in config.yaml to "
            "youtube-music.exe so I can auto-launch it."
        )
    log.info("YTMD not running — launching %s", _EXE_PATH)
    print("⏳ Starting YouTube Music app...", flush=True)
    flags = 0
    if os.name == "nt":
        flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
    subprocess.Popen(
        [str(_EXE_PATH)],
        cwd=str(_EXE_PATH.parent),
        creationflags=flags,
        close_fds=True,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    if not await _wait_for_api(timeout=45.0):
        raise RuntimeError(
            "YT Music started but the API Server didn't come up. Open the "
            "app → ≡ menu → Plugins → enable 'API Server', fully quit "
            "(right-click tray → Quit), and try again."
        )


async def _ensure_paired() -> str:
    global _token
    if _token:
        return _token
    if _TOKEN_PATH.exists():
        _token = _TOKEN_PATH.read_text(encoding="utf-8").strip()
        if _token:
            await _ensure_app_running()
            return _token
    await _ensure_app_running()
    print(
        f"\n🔐 A dialog in the YouTube Music app should ask to authorize "
        f"'{_CLIENT_ID}'. Click Allow.\n",
        flush=True,
    )
    async with httpx.AsyncClient(base_url=_BASE_URL, timeout=120.0) as client:
        resp = await client.post(f"/auth/{_CLIENT_ID}")
    if resp.status_code == 403:
        raise RuntimeError("Pairing denied in the YT Music app.")
    resp.raise_for_status()
    _token = resp.json()["accessToken"]
    _TOKEN_PATH.write_text(_token, encoding="utf-8")
    log.info("paired; saved token to %s", _TOKEN_PATH)
    return _token


async def _client() -> httpx.AsyncClient:
    global _http
    if _http is None:
        token = await _ensure_paired()
        _http = httpx.AsyncClient(
            base_url=_BASE_URL,
            headers={"Authorization": f"Bearer {token}"},
            timeout=10.0,
        )
    return _http


async def _api(method: str, path: str, *, json_body: Any = None) -> httpx.Response:
    global _token, _http
    c = await _client()
    resp = await c.request(method, f"/api/v1{path}", json=json_body)
    if resp.status_code == 401:
        log.warning("YTMD token rejected; deleting cache")
        _TOKEN_PATH.unlink(missing_ok=True)
        _token = None
        await _http.aclose()
        _http = None
    resp.raise_for_status()
    return resp


# ===== Queue introspection =====

def _unwrap_renderer(item: Any) -> dict | None:
    if not isinstance(item, dict):
        return None
    wrapper = item.get("playlistPanelVideoWrapperRenderer")
    if isinstance(wrapper, dict):
        primary = wrapper.get("primaryRenderer")
        if isinstance(primary, dict):
            inner = primary.get("playlistPanelVideoRenderer")
            if isinstance(inner, dict):
                return inner
    inner = item.get("playlistPanelVideoRenderer")
    if isinstance(inner, dict):
        return inner
    if "videoId" in item:
        return item
    return None


def _item_video_id(item: Any) -> str | None:
    inner = _unwrap_renderer(item)
    if not inner:
        return None
    v = inner.get("videoId")
    return v if isinstance(v, str) else None


def _item_is_selected(item: Any) -> bool:
    inner = _unwrap_renderer(item)
    return bool(inner and (inner.get("selected") or inner.get("playing")))


def _find_current_index(data: dict) -> int | None:
    if not isinstance(data, dict):
        return None
    for k in ("currentIndex", "selectedIndex", "current_index", "playingIndex"):
        v = data.get(k)
        if isinstance(v, int):
            return v
    cur = data.get("current")
    if isinstance(cur, dict):
        for k in ("index", "currentIndex"):
            v = cur.get(k)
            if isinstance(v, int):
                return v
    items = data.get("items")
    if isinstance(items, list):
        for i, item in enumerate(items):
            if _item_is_selected(item):
                return i
    return None


async def _clear_queue_around_current() -> None:
    """Delete every queue item except the currently playing one."""
    try:
        data = (await _api("GET", "/queue")).json() or {}
    except Exception as e:
        log.warning("GET /queue failed: %s", e)
        return
    items = data.get("items") if isinstance(data, dict) else None
    if not isinstance(items, list) or not items:
        return
    current_idx = _find_current_index(data)
    if current_idx is None:
        log.warning("can't find current index; queue keys=%s",
                    list(data.keys()) if isinstance(data, dict) else "?")
        return
    log.info("queue clear: current_idx=%d, total=%d", current_idx, len(items))
    for i in range(len(items) - 1, -1, -1):
        if i == current_idx:
            continue
        try:
            await _api("DELETE", f"/queue/{i}")
        except httpx.HTTPError as e:
            log.warning("DELETE /queue/%d failed: %s", i, e)
            break


_TITLE_BLOCKLIST = (
    "karaoke", "instrumental", "tutorial", "lyrics video",
    "8 hour", "10 hour", "1 hour", "sped up", "slowed",
    "nightcore", "reverb",
)


def _filter_results(results: list[dict], query: str) -> list[dict]:
    q = query.lower()
    keep = [
        r for r in results
        if not any(term in (r.get("title") or "").lower() and term not in q
                   for term in _TITLE_BLOCKLIST)
    ]
    return keep or results


# ===== Tool handlers =====

async def _play_music(args: dict) -> str:
    query = (args.get("query") or "").strip()
    if not query:
        return "No query provided."
    try:
        results = _ytmusic().search(query, filter="songs", limit=8)
        if not results:
            results = _ytmusic().search(query, limit=8)
    except Exception as e:  # noqa: BLE001
        return f"YT Music search failed: {e}"
    if not results:
        return f"Nothing found for '{query}'."

    results = _filter_results(results, query)
    top = results[0]
    video_id = top.get("videoId")
    title = top.get("title", "track")
    artists = top.get("artists") or []
    artist_names = ", ".join(a.get("name", "") for a in artists if a.get("name"))
    label = f"{title} by {artist_names}" if artist_names else title
    if not video_id:
        return f"Top result is not playable: {title}"

    log.info("play_music: query=%r → %s (videoId=%s)", query, label, video_id)
    try:
        await _api("POST", "/queue", json_body={
            "videoId": video_id,
            "insertPosition": "INSERT_AFTER_CURRENT_VIDEO",
        })
        await asyncio.sleep(0.2)
        # Find the index of the song we just queued and jump to it.
        target_idx = None
        try:
            data = (await _api("GET", "/queue")).json() or {}
            items = data.get("items") if isinstance(data, dict) else None
            if isinstance(items, list):
                for i, item in enumerate(items):
                    if _item_video_id(item) == video_id:
                        target_idx = i
                        break
        except Exception:
            pass
        if target_idx is not None:
            await _api("PATCH", "/queue", json_body={"index": target_idx})
        else:
            await _api("POST", "/next")
        await _clear_queue_around_current()
    except httpx.HTTPError as e:
        return f"YTMD playback control failed: {e}"
    return f"Playing {label}."


async def _play_pause(_args: dict) -> str:
    await _api("POST", "/toggle-play")
    return "Toggled play/pause."


async def _next_track(_args: dict) -> str:
    await _api("POST", "/next")
    return "Skipped to next track."


async def _previous_track(_args: dict) -> str:
    await _api("POST", "/previous")
    return "Went back to previous track."


async def _volume(args: dict) -> str:
    direction = (args.get("direction") or "").lower().strip()
    amount = args.get("amount")
    if direction == "mute":
        await _api("POST", "/toggle-mute")
        return "Toggled mute."
    try:
        if direction in {"up", "down"}:
            step = int(amount) if amount else 10
            current = (await _api("GET", "/volume")).json().get("state", 50)
            new = max(0, min(100, current + step if direction == "up" else current - step))
            await _api("POST", "/volume", json_body={"volume": new})
            return f"Volume {direction} to {new}."
        if direction == "set":
            new = max(0, min(100, int(amount or 50)))
            await _api("POST", "/volume", json_body={"volume": new})
            return f"Volume set to {new}."
        return f"Unknown direction '{direction}' — use up/down/set/mute."
    except httpx.HTTPError as e:
        return f"Volume change failed: {e}"


async def _now_playing(_args: dict) -> str:
    try:
        data = (await _api("GET", "/song")).json() or {}
    except httpx.HTTPError as e:
        return f"Couldn't read current song: {e}"
    title = data.get("title") or "(unknown title)"
    artist = data.get("artist") or "(unknown artist)"
    return f"{title} by {artist}"


async def _clear_queue(_args: dict) -> str:
    try:
        await _clear_queue_around_current()
    except httpx.HTTPError as e:
        return f"Couldn't clear queue: {e}"
    return "Queue cleared — just the current song left."


def get_tools(config: dict) -> list[Tool]:
    global _HOST, _PORT, _BASE_URL, _EXE_PATH
    _PORT = int(config.get("port", 26538))
    _BASE_URL = f"http://{_HOST}:{_PORT}"
    exe = config.get("exe_path")
    _EXE_PATH = Path(exe) if exe else None

    if YTMusic is None:
        log.warning("ytmusicapi not installed — music_ytmd tools disabled")
        return []

    return [
        Tool(
            name="play_music",
            description=(
                "Play a song, artist, or album on YouTube Music. Plays the top "
                "filtered search result, replaces whatever's playing, and clears "
                "the rest of the queue so only the new song is queued. Call this "
                "every time the user asks to play something — even if the same "
                "song appears to already be playing."
            ),
            input_schema={
                "type": "object",
                "properties": {"query": {"type": "string"}},
                "required": ["query"],
            },
            handler=_play_music,
        ),
        Tool(
            name="play_pause",
            description="Toggle pause/resume on YouTube Music.",
            input_schema={"type": "object", "properties": {}},
            handler=_play_pause,
        ),
        Tool(
            name="next_track",
            description="Skip to the next track on YouTube Music.",
            input_schema={"type": "object", "properties": {}},
            handler=_next_track,
        ),
        Tool(
            name="previous_track",
            description="Go back to the previous track on YouTube Music.",
            input_schema={"type": "object", "properties": {}},
            handler=_previous_track,
        ),
        Tool(
            name="volume",
            description=(
                "Adjust YouTube Music volume. direction is 'up', 'down', "
                "'set', or 'mute'. For up/down, amount is the step (default 10). "
                "For set, amount is the target volume 0-100."
            ),
            input_schema={
                "type": "object",
                "properties": {
                    "direction": {"type": "string", "enum": ["up", "down", "set", "mute"]},
                    "amount": {"type": "integer"},
                },
                "required": ["direction"],
            },
            handler=_volume,
        ),
        Tool(
            name="now_playing",
            description="Get the title and artist of the song currently playing.",
            input_schema={"type": "object", "properties": {}},
            handler=_now_playing,
        ),
        Tool(
            name="clear_queue",
            description=(
                "Empty the YouTube Music queue but keep the current song playing. "
                "Use when the user asks to 'clear the queue' or 'start fresh'."
            ),
            input_schema={"type": "object", "properties": {}},
            handler=_clear_queue,
        ),
    ]
