"""Open URLs and play YouTube videos (regular YouTube, not Music)."""

from __future__ import annotations

import asyncio
import logging
import webbrowser
from urllib.parse import urlparse

from core.tools import Tool

log = logging.getLogger("jarvis.tools.web_browser")

_SAFE_SCHEMES = {"http", "https"}


async def _open_url(args: dict) -> str:
    url = (args.get("url") or "").strip()
    if not url:
        return "No URL provided."
    if not url.startswith(("http://", "https://")):
        url = "https://" + url
    parsed = urlparse(url)
    if parsed.scheme not in _SAFE_SCHEMES:
        return f"Refusing to open URL with scheme '{parsed.scheme}'."
    if not parsed.netloc:
        return f"Invalid URL: {url}"
    log.info("open_url: %s", url)
    webbrowser.open(url)
    return f"Opened {parsed.netloc}."


async def _play_youtube_video(args: dict) -> str:
    query = (args.get("query") or "").strip()
    if not query:
        return "No query provided."
    try:
        from yt_dlp import YoutubeDL
    except ImportError:
        return ("YouTube video search needs yt-dlp installed: "
                "pip install -r requirements-web.txt")

    try:
        with YoutubeDL({
            "quiet": True, "no_warnings": True,
            "extract_flat": True, "skip_download": True,
        }) as ydl:
            info = await asyncio.to_thread(
                ydl.extract_info, f"ytsearch1:{query}", download=False,
            )
    except Exception as e:  # noqa: BLE001
        return f"YouTube search failed: {e}"

    entries = (info or {}).get("entries") or []
    if not entries:
        return f"No YouTube results for '{query}'."
    entry = entries[0]
    video_id = entry.get("id")
    title = entry.get("title", "video")
    if not video_id:
        return "Top result has no video ID."
    url = f"https://www.youtube.com/watch?v={video_id}"
    log.info("play_youtube_video: %r → %r (%s)", query, title, video_id)
    webbrowser.open(url)
    return f"Playing '{title}' on YouTube."


def get_tools(_config: dict) -> list[Tool]:
    return [
        Tool(
            name="open_url",
            description=(
                "Open a website in the user's default browser. Accepts a URL "
                "or bare domain ('github.com'). http/https only."
            ),
            input_schema={
                "type": "object",
                "properties": {"url": {"type": "string"}},
                "required": ["url"],
            },
            handler=_open_url,
        ),
        Tool(
            name="play_youtube_video",
            description=(
                "Search YouTube (NOT YouTube Music) and play the top result "
                "in the browser. Use for non-music videos: tutorials, clips, "
                "reviews. For music, prefer the play_music tool."
            ),
            input_schema={
                "type": "object",
                "properties": {"query": {"type": "string"}},
                "required": ["query"],
            },
            handler=_play_youtube_video,
        ),
    ]
