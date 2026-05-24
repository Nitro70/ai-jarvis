"""LLM backend implementations. Pick one in config.yaml via `llm.backend`."""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from core.tools import Tool
    from .base import LLMBackend


def make_backend(config: dict, tools: "list[Tool]") -> "LLMBackend":
    """Factory: instantiate the configured LLM backend."""
    llm_cfg = config.get("llm") or {}
    backend = llm_cfg.get("backend", "claude_agent")
    system_prompt = (config.get("persona") or {}).get("system_prompt", "")

    if backend == "claude_agent":
        from .claude_agent import ClaudeAgentBackend
        return ClaudeAgentBackend(llm_cfg, system_prompt, tools)
    if backend == "claude_api":
        from .claude_api import ClaudeApiBackend
        return ClaudeApiBackend(llm_cfg, system_prompt, tools)
    if backend == "openai_compat":
        from .openai_compat import OpenAICompatBackend
        return OpenAICompatBackend(llm_cfg, system_prompt, tools)

    raise ValueError(
        f"Unknown llm.backend: {backend!r}. "
        "Use 'claude_agent', 'claude_api', or 'openai_compat'."
    )
