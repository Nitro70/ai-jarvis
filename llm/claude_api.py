"""Anthropic API backend. Uses an API key from console.anthropic.com.

Maintains conversation history client-side and runs the tool loop manually."""

from __future__ import annotations

import json
import logging
from typing import AsyncIterator

from core.tools import Tool

from .base import LLMBackend

log = logging.getLogger("jarvis.llm.claude_api")


class ClaudeApiBackend(LLMBackend):
    def __init__(self, llm_cfg: dict, system_prompt: str, tools: list[Tool]):
        import anthropic

        api_key = llm_cfg.get("api_key") or ""
        if not api_key:
            raise ValueError(
                "claude_api backend requires llm.api_key in config "
                "(use ${ANTHROPIC_API_KEY} to read from env)"
            )

        self._client = anthropic.AsyncAnthropic(api_key=api_key)
        self._model = llm_cfg.get("model", "claude-sonnet-4-6")
        self._max_tokens = int(llm_cfg.get("max_tokens", 1024))
        self._system = system_prompt
        self._tools_by_name: dict[str, Tool] = {t.name: t for t in tools}
        self._tool_defs = [t.to_anthropic() for t in tools]
        self._history: list[dict] = []

    async def __aenter__(self) -> "ClaudeApiBackend":
        log.info("Anthropic API backend ready (model=%s)", self._model)
        return self

    async def __aexit__(self, *exc) -> None:
        await self._client.close()

    async def send(self, user_text: str) -> AsyncIterator[str]:
        self._history.append({"role": "user", "content": user_text})

        while True:
            response = await self._client.messages.create(
                model=self._model,
                max_tokens=self._max_tokens,
                system=self._system,
                tools=self._tool_defs or anthropic_NOT_GIVEN(),
                messages=self._history,
            )

            # Echo assistant content (text + tool_use) back into history.
            self._history.append({"role": "assistant", "content": response.content})

            # Yield any text blocks immediately so the caller can stream them.
            text_yielded = False
            tool_uses = []
            for block in response.content:
                if block.type == "text":
                    yield block.text
                    text_yielded = True
                elif block.type == "tool_use":
                    tool_uses.append(block)

            if response.stop_reason != "tool_use" or not tool_uses:
                return  # done — no more tools to run

            # Execute every tool the model asked for, collect results.
            tool_results = []
            for use in tool_uses:
                tool = self._tools_by_name.get(use.name)
                if tool is None:
                    result = f"Unknown tool: {use.name}"
                    is_error = True
                else:
                    try:
                        result = await tool.handler(use.input or {})
                        is_error = False
                    except Exception as e:  # noqa: BLE001
                        log.exception("tool %s failed", use.name)
                        result = f"Tool error: {e}"
                        is_error = True
                tool_results.append({
                    "type": "tool_result",
                    "tool_use_id": use.id,
                    "content": str(result),
                    "is_error": is_error,
                })
            self._history.append({"role": "user", "content": tool_results})
            # Loop again to let the model react to the tool results.


def anthropic_NOT_GIVEN():
    """Anthropic SDK's NOT_GIVEN sentinel — used to omit optional fields."""
    import anthropic
    return anthropic.NOT_GIVEN
