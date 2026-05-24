"""OpenAI-compatible backend. Works with anything that speaks the OpenAI
Chat Completions API: OpenAI itself, xAI (Grok), Groq, OpenRouter, Ollama,
LM Studio, etc.

Configure base_url + api_key + model to point at your provider."""

from __future__ import annotations

import json
import logging
from typing import AsyncIterator

from core.tools import Tool

from .base import LLMBackend

log = logging.getLogger("jarvis.llm.openai_compat")


class OpenAICompatBackend(LLMBackend):
    def __init__(self, llm_cfg: dict, system_prompt: str, tools: list[Tool]):
        from openai import AsyncOpenAI

        base_url = llm_cfg.get("base_url", "https://api.openai.com/v1")
        api_key = llm_cfg.get("api_key") or "not-needed"

        self._client = AsyncOpenAI(base_url=base_url, api_key=api_key)
        self._model = llm_cfg.get("model", "gpt-4o-mini")
        self._max_tokens = int(llm_cfg.get("max_tokens", 1024))
        self._tools_by_name: dict[str, Tool] = {t.name: t for t in tools}
        self._tool_defs = [t.to_openai() for t in tools] or None
        self._history: list[dict] = [{"role": "system", "content": system_prompt}]
        self._base_url = base_url

    async def __aenter__(self) -> "OpenAICompatBackend":
        log.info("OpenAI-compat backend ready (base_url=%s, model=%s)",
                 self._base_url, self._model)
        return self

    async def __aexit__(self, *exc) -> None:
        await self._client.close()

    async def send(self, user_text: str) -> AsyncIterator[str]:
        self._history.append({"role": "user", "content": user_text})

        while True:
            kwargs = dict(
                model=self._model,
                messages=self._history,
                max_tokens=self._max_tokens,
            )
            if self._tool_defs:
                kwargs["tools"] = self._tool_defs

            response = await self._client.chat.completions.create(**kwargs)
            choice = response.choices[0]
            msg = choice.message

            # Echo the assistant message back into history. Need both `content`
            # and `tool_calls` preserved verbatim for tool_calls round-trip.
            assistant_entry: dict = {"role": "assistant", "content": msg.content or ""}
            if msg.tool_calls:
                assistant_entry["tool_calls"] = [
                    {
                        "id": tc.id,
                        "type": "function",
                        "function": {
                            "name": tc.function.name,
                            "arguments": tc.function.arguments,
                        },
                    }
                    for tc in msg.tool_calls
                ]
            self._history.append(assistant_entry)

            if msg.content:
                yield msg.content

            if not msg.tool_calls:
                return

            # Execute each tool call and append its result.
            for tc in msg.tool_calls:
                name = tc.function.name
                try:
                    args = json.loads(tc.function.arguments or "{}")
                except json.JSONDecodeError:
                    args = {}
                tool = self._tools_by_name.get(name)
                if tool is None:
                    result = f"Unknown tool: {name}"
                else:
                    try:
                        result = await tool.handler(args)
                    except Exception as e:  # noqa: BLE001
                        log.exception("tool %s failed", name)
                        result = f"Tool error: {e}"
                self._history.append({
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "content": str(result),
                })
            # Loop so the model can use the tool results.
