"""Abstract LLM backend interface.

Every backend implements `send(user_text)` as an async generator yielding
text chunks of the assistant's reply. Tool calls are handled internally —
callers only see assistant text."""

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator


class LLMBackend(ABC):
    """Stateful conversation with an LLM that can call tools."""

    @abstractmethod
    async def __aenter__(self) -> "LLMBackend":
        ...

    @abstractmethod
    async def __aexit__(self, *exc) -> None:
        ...

    @abstractmethod
    async def send(self, user_text: str) -> AsyncIterator[str]:
        """Send a user turn. Yields the assistant's text chunks as they
        become available. Handles all tool calls internally."""
        if False:  # pragma: no cover — make this an async generator
            yield ""
