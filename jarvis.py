"""Jarvis — voice assistant entry point.

Usage:
    python jarvis.py                 # use ./config.yaml
    python jarvis.py -c other.yaml   # custom config
    python jarvis.py --debug         # mirror logs to terminal
"""

from __future__ import annotations

import argparse
import asyncio
import logging
import sys
from pathlib import Path

# Make our local packages importable when run from this directory.
sys.path.insert(0, str(Path(__file__).parent))

from core.config import load_config
from core.logging import setup_logging
from core.memory import load_memory
from core.tools import collect_tools


def _build_system_prompt(config: dict, log: logging.Logger) -> str:
    """Combine persona.system_prompt with the contents of memory.md (if any)."""
    persona = config.get("persona") or {}
    base = persona.get("system_prompt", "").strip()
    mem_path = persona.get("memory_file")
    if not mem_path:
        return base
    mem = load_memory(mem_path)
    if not mem:
        return base
    return (
        f"{base}\n\n"
        f"--- Long-term memory about the user (from {mem_path}) ---\n"
        f"{mem}\n"
        f"--- end memory ---\n\n"
        "If the user shares new personal info, preferences, routines, or other "
        "things worth remembering across sessions, call the `remember` tool to "
        "save them. The user can edit or delete entries directly in the file."
    )


def parse_args():
    p = argparse.ArgumentParser(description="Jarvis voice assistant")
    p.add_argument("-c", "--config", default="config.yaml",
                   help="Path to config.yaml (default: ./config.yaml)")
    p.add_argument("--debug", action="store_true",
                   help="Mirror logs to the terminal")
    return p.parse_args()


async def run_voice_mode(config, llm_backend, log):
    """Wake word → record → transcribe → query → TTS → loop."""
    from faster_whisper import WhisperModel
    from voice.stt import VoiceListener
    from voice.tts import TextToSpeech

    voice_cfg = config.get("voice") or {}
    stt_cfg = voice_cfg.get("stt") or {}
    tts_cfg = voice_cfg.get("tts") or {}

    wake_model_size = stt_cfg.get("wake_model", "tiny.en")
    cmd_model_size = stt_cfg.get("model", "base.en")

    print(f"Loading Whisper command model ({cmd_model_size})...", flush=True)
    cmd_model = WhisperModel(cmd_model_size, device="cpu", compute_type="int8")
    print(f"Loading Whisper wake model ({wake_model_size})...", flush=True)
    wake_model = WhisperModel(wake_model_size, device="cpu", compute_type="int8")

    wake_word = voice_cfg.get("wake_word", "jarvis")
    variants = tuple(voice_cfg.get("wake_word_variants") or [wake_word])

    listener = VoiceListener(
        wake_model=wake_model,
        command_model=cmd_model,
        wake_words=variants,
        silence_end_seconds=voice_cfg.get("silence_end_seconds", 2.5),
        max_command_seconds=voice_cfg.get("max_command_seconds", 30),
        command_giveup_seconds=voice_cfg.get("command_giveup_seconds", 6),
    )
    tts = TextToSpeech(
        voice=tts_cfg.get("voice", "en-GB-RyanNeural"),
        enabled=tts_cfg.get("enabled", True),
    )

    # If the windows_state tool is loaded, start its foreground tracker so
    # "minimize this window" works.
    try:
        from tools.windows_state import start_foreground_tracker
        start_foreground_tracker()
    except ImportError:
        pass

    print(
        f"\nJarvis online. Press Enter to start listening for '{wake_word}'.\n"
        "Once active, just say the wake word followed by your request — "
        "pause when done.\nCtrl+C to quit at any time.\n",
        flush=True,
    )

    try:
        await asyncio.to_thread(input, "▶ Press Enter to start ")
    except (EOFError, KeyboardInterrupt):
        return

    while True:
        try:
            await listener.wait_for_wake()
        except (EOFError, KeyboardInterrupt):
            return

        print("✨ Yes, sir?", flush=True)
        print("🎙️  Listening for your command...", flush=True)
        try:
            audio = await listener.record_command()
        except (EOFError, KeyboardInterrupt):
            return

        if audio.size == 0:
            print("(I didn't catch anything — going back to listening.)\n")
            continue

        print("⌛ Transcribing...", flush=True)
        user_text = listener.transcribe_command(audio)
        if not user_text:
            print("(silence — try again)\n")
            continue

        print(f"You:    {user_text}")
        log.info("USER: %s", user_text)

        reply_parts: list[str] = []
        print("JARVIS: ", end="", flush=True)
        async for chunk in llm_backend.send(user_text):
            print(chunk, end="", flush=True)
            reply_parts.append(chunk)
        print()

        full_reply = "".join(reply_parts).strip()
        if full_reply:
            log.info("JARVIS: %s", full_reply)
            await tts.speak(full_reply)
        print()


async def run_text_mode(_config, llm_backend, log):
    """Type → query → print → loop. No voice."""
    print("Jarvis online (text mode). Type messages, blank line to quit.\n", flush=True)
    while True:
        try:
            user_text = await asyncio.to_thread(input, "You: ")
        except (EOFError, KeyboardInterrupt):
            return
        user_text = user_text.strip()
        if not user_text:
            return
        log.info("USER: %s", user_text)

        reply_parts: list[str] = []
        print("JARVIS: ", end="", flush=True)
        async for chunk in llm_backend.send(user_text):
            print(chunk, end="", flush=True)
            reply_parts.append(chunk)
        print("\n")
        if reply_parts:
            log.info("JARVIS: %s", "".join(reply_parts).strip())


async def main():
    args = parse_args()
    config = load_config(args.config)
    setup_logging(config, terminal_override=args.debug)
    log = logging.getLogger("jarvis")

    tools = collect_tools(config)
    log.info("loaded %d tools total: %s", len(tools), [t.name for t in tools])

    # Merge memory.md into the system prompt before constructing the backend.
    config.setdefault("persona", {})["system_prompt"] = _build_system_prompt(config, log)

    from llm import make_backend
    backend = make_backend(config, tools)

    mode = (config.get("mode") or "voice").lower()
    log.info("mode: %s", mode)

    async with backend:
        if mode == "text":
            await run_text_mode(config, backend, log)
        else:
            await run_voice_mode(config, backend, log)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
    except Exception:
        logging.getLogger("jarvis").exception("fatal error")
        raise
