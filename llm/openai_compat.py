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
        # Some local models (llama2 base, older Mistral, etc.) don't support
        # OpenAI-style function calling. Sending `tools=` to them via Ollama
        # either silently drops the calls or errors with "model does not
        # support tools". Setting `disable_tools: true` in config.yaml's `llm:`
        # section makes Jarvis run in chat-only mode — replies still work,
        # but it can't call play_music / open_app / etc.
        if llm_cfg.get("disable_tools"):
            log.info("disable_tools=true in config — running chat-only, no tool calls")
            self._tool_defs = None
        else:
            self._tool_defs = [t.to_openai() for t in tools] or None
        self._history: list[dict] = [{"role": "system", "content": system_prompt}]
        self._base_url = base_url

    async def __aenter__(self) -> "OpenAICompatBackend":
        log.info("OpenAI-compat backend ready (base_url=%s, model=%s)",
                 self._base_url, self._model)
        return self

    def _pop_user_turn(self) -> None:
        """Roll back the most recently appended user message.

        Used when the very first request of a send() fails. Without this,
        the failed user turn stays in history forever and every subsequent
        send() retransmits it, both wasting tokens and confusing the model
        (it sees a "user said X" with no assistant reply right before its
        own turn)."""
        if self._history and self._history[-1].get("role") == "user":
            self._history.pop()

    async def __aexit__(self, *exc) -> None:
        # Closing can itself raise if the underlying httpx client already tore
        # down (e.g. event-loop shutdown). Swallow - we're exiting anyway.
        try:
            await self._client.close()
        except Exception:  # noqa: BLE001
            log.debug("client.close() raised during shutdown; ignoring", exc_info=True)

    async def send(self, user_text: str) -> AsyncIterator[str]:
        # Lazy-import the openai exception classes so we don't tie this module
        # to a specific package layout at import time.
        from openai import (
            AuthenticationError,
            RateLimitError,
            APIConnectionError,
            APITimeoutError,
            BadRequestError,
            APIStatusError,
            OpenAIError,
        )

        self._history.append({"role": "user", "content": user_text})

        # Track whether we've made at least one successful round-trip. If the
        # FIRST attempt fails we roll back the user message we just appended,
        # so the next send() doesn't carry a stuck "user said X, assistant
        # never replied" turn that would confuse the model.
        iter_count = 0

        while True:
            kwargs = dict(
                model=self._model,
                messages=self._history,
                max_tokens=self._max_tokens,
            )
            if self._tool_defs:
                kwargs["tools"] = self._tool_defs

            try:
                response = await self._client.chat.completions.create(**kwargs)
            except AuthenticationError as e:
                log.error("LLM auth failed (%s): %s", self._base_url, e)
                if iter_count == 0:
                    self._pop_user_turn()
                yield ("My API key was rejected by the LLM. "
                       "Check api_key in config.yaml and restart me.")
                return
            except RateLimitError as e:
                log.error("LLM rate-limited: %s", e)
                if iter_count == 0:
                    self._pop_user_turn()
                yield "The LLM provider is rate-limiting me. Try again in a minute."
                return
            except APITimeoutError as e:
                log.error("LLM timeout: %s", e)
                if iter_count == 0:
                    self._pop_user_turn()
                yield "The LLM took too long to respond. Try again."
                return
            except APIConnectionError as e:
                log.error("LLM network error (%s): %s", self._base_url, e)
                if iter_count == 0:
                    self._pop_user_turn()
                yield ("I can't reach the LLM server. "
                       "Check your internet or the base_url in config.yaml.")
                return
            except BadRequestError as e:
                log.error("LLM rejected request: %s", e)
                if iter_count == 0:
                    self._pop_user_turn()
                yield ("The LLM rejected my request - the model name or "
                       "config is probably wrong. Check the log.")
                return
            except APIStatusError as e:
                code = getattr(e, "status_code", "?")
                log.error("LLM HTTP %s: %s", code, e)
                if iter_count == 0:
                    self._pop_user_turn()
                yield f"The LLM returned HTTP {code}. Check the log for details."
                return
            except OpenAIError as e:
                log.exception("LLM unexpected OpenAI error")
                if iter_count == 0:
                    self._pop_user_turn()
                yield f"Something went wrong talking to the LLM: {e}"
                return
            except Exception as e:  # noqa: BLE001
                # Non-OpenAI exception (e.g. asyncio.CancelledError won't get
                # here because it's BaseException, which is what we want -
                # cancellations still propagate). Anything else: friendly fail.
                log.exception("LLM unexpected non-OpenAI error")
                if iter_count == 0:
                    self._pop_user_turn()
                yield f"Something went wrong: {e}"
                return

            iter_count += 1
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
