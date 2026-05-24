"""Backend-agnostic tool definition + format converters.

Every tool module declares its tools using the Tool dataclass below. Each LLM
backend converts the same Tool to its provider-specific shape — so a single
tool definition works for Claude Agent SDK, Anthropic API, and OpenAI.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Awaitable, Callable


# A tool handler is an async function: receives the parsed arguments dict,
# returns a string that will be shown to the LLM as the tool result.
ToolHandler = Callable[[dict[str, Any]], Awaitable[str]]


@dataclass
class Tool:
    """A single callable capability the LLM can invoke."""
    name: str
    description: str
    input_schema: dict  # Standard JSON Schema (type: object with properties)
    handler: ToolHandler

    # ----- Format converters for each LLM backend -----

    def to_anthropic(self) -> dict:
        """Anthropic API tool format."""
        return {
            "name": self.name,
            "description": self.description,
            "input_schema": self.input_schema,
        }

    def to_openai(self) -> dict:
        """OpenAI / OpenAI-compatible tool format (functions)."""
        return {
            "type": "function",
            "function": {
                "name": self.name,
                "description": self.description,
                "parameters": self.input_schema,
            },
        }

    def to_claude_agent_sdk(self):
        """Convert to a claude_agent_sdk @tool-decorated async function."""
        from claude_agent_sdk import tool as agent_tool

        handler = self.handler

        # The Claude Agent SDK's @tool decorator wraps a handler into an MCP
        # tool function. It expects a {param: type} schema or a full JSON Schema.
        @agent_tool(self.name, self.description, self.input_schema)
        async def _sdk_wrapper(args: dict[str, Any]) -> dict[str, Any]:
            try:
                result = await handler(args)
                return {
                    "content": [{"type": "text", "text": str(result)}],
                    "is_error": False,
                }
            except Exception as e:  # noqa: BLE001 — surface as tool error
                return {
                    "content": [{"type": "text", "text": f"Tool error: {e}"}],
                    "is_error": True,
                }

        return _sdk_wrapper


def collect_tools(config: dict) -> list[Tool]:
    """Walk the configured tool modules and gather all enabled Tool instances.

    Tool modules live under `tools/` and export a `get_tools(module_config)`
    function returning `list[Tool]`. The module is considered disabled if
    its config has `enabled: false`, or if its dependencies aren't importable.
    """
    import importlib
    import logging

    log = logging.getLogger("jarvis.tools")
    out: list[Tool] = []

    tools_cfg = config.get("tools") or {}
    for module_name, module_cfg in tools_cfg.items():
        module_cfg = module_cfg or {}
        if not module_cfg.get("enabled", True):
            log.info("tool module %r disabled in config", module_name)
            continue
        try:
            module = importlib.import_module(f"tools.{module_name}")
        except ImportError as e:
            log.warning(
                "tool module %r couldn't be imported (missing dep?): %s",
                module_name, e,
            )
            continue

        if not hasattr(module, "get_tools"):
            log.warning("tool module %r has no get_tools(config) function",
                        module_name)
            continue

        try:
            module_tools = module.get_tools(module_cfg)
        except Exception as e:  # noqa: BLE001
            log.exception("tool module %r failed to initialize: %s",
                          module_name, e)
            continue

        if module_tools:
            log.info("tool module %r loaded: %s",
                     module_name, [t.name for t in module_tools])
            out.extend(module_tools)

    return out
