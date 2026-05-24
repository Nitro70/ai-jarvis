"""Time, date, weather. No external setup required."""

from __future__ import annotations

from datetime import datetime
from urllib.parse import quote_plus

import httpx

from core.tools import Tool


async def _current_time(_args: dict) -> str:
    now = datetime.now()
    hour_12 = now.hour % 12 or 12
    return (
        f"{now.strftime('%A, %B')} {now.day}, {now.year} "
        f"at {hour_12}:{now.minute:02d} {now.strftime('%p')}"
    )


async def _get_weather(args: dict) -> str:
    location = (args.get("location") or "").strip()
    fmt = "%l:+%c+%t,+feels+%f,+wind+%w,+humidity+%h"
    url = f"https://wttr.in/{quote_plus(location)}?format={fmt}"
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            resp = await client.get(url, headers={"User-Agent": "curl/8"})
            resp.raise_for_status()
    except httpx.HTTPError as e:
        return f"Weather lookup failed: {e}"
    return resp.text.strip().replace("+", " ")


def get_tools(_config: dict) -> list[Tool]:
    return [
        Tool(
            name="current_time",
            description="Get the current local time and date.",
            input_schema={"type": "object", "properties": {}},
            handler=_current_time,
        ),
        Tool(
            name="get_weather",
            description=(
                "Get current weather for a location. Pass an empty string to "
                "use the user's approximate location (by IP). Powered by wttr.in."
            ),
            input_schema={
                "type": "object",
                "properties": {
                    "location": {"type": "string", "description": "City, region, or empty for auto"},
                },
                "required": ["location"],
            },
            handler=_get_weather,
        ),
    ]
