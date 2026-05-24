"""Claude Agent SDK backend. Uses your Claude subscription via Claude Code —
no API key needed.

Requires Claude Code installed and logged in on this machine."""

from __future__ import annotations

import logging
from typing import AsyncIterator

from core.tools import Tool

from .base import LLMBackend

log = logging.getLogger("jarvis.llm.claude_agent")


class ClaudeAgentBackend(LLMBackend):
    def __init__(self, llm_cfg: dict, system_prompt: str, tools: list[Tool]):
        from claude_agent_sdk import (
            AssistantMessage,
            ClaudeAgentOptions,
            ClaudeSDKClient,
            TextBlock,
            create_sdk_mcp_server,
        )
        self._AssistantMessage = AssistantMessage
        self._TextBlock = TextBlock
        self._ClaudeSDKClient = ClaudeSDKClient

        sdk_tools = [t.to_claude_agent_sdk() for t in tools]
        mcp_server = create_sdk_mcp_server(
            name="jarvis", version="1.0.0", tools=sdk_tools,
        )

        self._options = ClaudeAgentOptions(
            model=llm_cfg.get("model", "claude-sonnet-4-6"),
            system_prompt=system_prompt,
            mcp_servers={"jarvis": mcp_server},
            allowed_tools=["mcp__jarvis__*"],
            # Voice replies should be snappy — disable thinking, low effort.
            # NOTE: ThinkingConfigDisabled is a TypedDict, so calling it gives
            # an empty {} with no 'type' key, which crashes the SDK. Use the
            # literal dict instead.
            thinking={"type": "disabled"},
            effort="low",
        )
        self._client = None

    async def __aenter__(self) -> "ClaudeAgentBackend":
        self._client = self._ClaudeSDKClient(options=self._options)
        await self._client.__aenter__()
        log.info("Claude Agent backend connected")
        return self

    async def __aexit__(self, *exc) -> None:
        if self._client is not None:
            await self._client.__aexit__(*exc)
            self._client = None

    async def send(self, user_text: str) -> AsyncIterator[str]:
        assert self._client is not None
        await self._client.query(user_text)
        async for message in self._client.receive_response():
            if isinstance(message, self._AssistantMessage):
                for block in message.content:
                    if isinstance(block, self._TextBlock):
                        yield block.text
