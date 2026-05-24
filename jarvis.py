"""Jarvis — voice / text assistant entry point.

Usage:
    python jarvis.py                 # use ./config.yaml's mode (default voice)
    python jarvis.py --text          # force text mode (type, no mic needed)
    python jarvis.py --voice         # force voice mode (wake word + STT)
    python jarvis.py --no-tts        # disable spoken replies for this run
    python jarvis.py -c other.yaml   # custom config file
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
    p = argparse.ArgumentParser(description="Jarvis voice/text assistant")
    p.add_argument("-c", "--config", default="config.yaml",
                   help="Path to config.yaml (default: ./config.yaml)")
    p.add_argument("--debug", action="store_true",
                   help="Mirror logs to the terminal (default: file only)")
    mode = p.add_mutually_exclusive_group()
    mode.add_argument("--text", action="store_const", const="text", dest="mode",
                      help="Force text mode (type messages, mic not used)")
    mode.add_argument("--voice", action="store_const", const="voice", dest="mode",
                      help="Force voice mode (wake word + STT + TTS)")
    p.add_argument("--no-tts", action="store_true",
                   help="Disable spoken replies for this run "
                        "(text mode will be silent; voice mode keeps mic)")
    return p.parse_args()


def _make_tts(config: dict, force_disable: bool):
    """Construct a TextToSpeech instance from config. Returns None if TTS
    deps aren't installed (graceful: text mode will just print)."""
    tts_cfg = (config.get("voice") or {}).get("tts") or {}
    enabled = tts_cfg.get("enabled", True) and not force_disable
    try:
        from voice.tts import TextToSpeech
    except ImportError as e:
        logging.getLogger("jarvis").warning(
            "TTS deps not installed (%s) — replies will print only. "
            "Install with: pip install -r requirements-voice.txt", e,
        )
        return None
    return TextToSpeech(
        voice=tts_cfg.get("voice", "en-GB-RyanNeural"),
        enabled=enabled,
    )


async def run_voice_mode(config, llm_backend, log, tts):
    """Wake word → record → transcribe → query → TTS → loop."""
    try:
        from faster_whisper import WhisperModel
        from voice.stt import (
            VoiceListener, warm_up, check_microphone, list_input_devices,
        )
    except ImportError as e:
        print(
            f"\nCan't start voice mode — missing dependency: {e}\n"
            "Install with:  pip install -r requirements-voice.txt\n"
            "Or run in text mode:  python jarvis.py --text\n",
            file=sys.stderr, flush=True,
        )
        return

    voice_cfg = config.get("voice") or {}
    stt_cfg = voice_cfg.get("stt") or {}

    wake_model_size = stt_cfg.get("wake_model", "tiny.en")
    cmd_model_size = stt_cfg.get("model", "base.en")

    print(f"Loading Whisper command model ({cmd_model_size})...", flush=True)
    cmd_model = WhisperModel(cmd_model_size, device="cpu", compute_type="int8")
    print(f"Loading Whisper wake model ({wake_model_size})...", flush=True)
    wake_model = WhisperModel(wake_model_size, device="cpu", compute_type="int8")

    # Warm up CTranslate2's lazy init so the FIRST real transcription isn't slow
    # (otherwise saying "jarvis" the first time can stall 5-15 seconds while
    # CT2 compiles its kernels in the background).
    print("Warming up Whisper...", flush=True)
    await asyncio.to_thread(warm_up, wake_model)
    await asyncio.to_thread(warm_up, cmd_model)

    # Quick mic sanity check. If the default input device returns literal zeros,
    # the mic isn't capturing — surface this loudly with device-picker hints
    # instead of letting the wake-word loop sit silently forever.
    print("Checking microphone...", flush=True)
    try:
        peak, device_name = await asyncio.to_thread(check_microphone, 0.5)
    except Exception as e:
        print(f"⚠  Microphone check failed: {e}", flush=True)
        peak, device_name = 0.0, "(error)"
    if peak <= 0.0:
        print(f"⚠  Default input device '{device_name}' produced silence — "
              "voice mode won't hear you.", flush=True)
        print("   Available input devices on this machine:", flush=True)
        for idx, name in list_input_devices():
            print(f"     [{idx}] {name}", flush=True)
        print("   Fix in Windows: Settings → System → Sound → Input → pick a "
              "real mic, allow apps to use the microphone.", flush=True)
        print("   (Continuing anyway — fix the mic and the wake-word loop "
              "will pick it up.)\n", flush=True)
    else:
        print(f"✓ Microphone OK: '{device_name}' "
              f"(peak {peak:.3f})", flush=True)

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
            if tts:
                await tts.speak(full_reply)
        print()


async def run_text_mode(_config, llm_backend, log, tts):
    """Type → query → print → (optionally speak) → loop. No mic required."""
    speaking = bool(tts and tts.enabled)
    print(
        "Jarvis online (text mode). Type a message and press Enter. "
        "Blank line to quit.\n"
        + ("(replies will also be spoken aloud — use --no-tts to silence them)\n"
           if speaking else
           "(replies are text-only — TTS is disabled)\n"),
        flush=True,
    )

    # Some tools (windows_state) start a background task that wants the event
    # loop running. Spin it up here too so text mode supports "minimize this".
    try:
        from tools.windows_state import start_foreground_tracker
        start_foreground_tracker()
    except ImportError:
        pass

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
        print()

        full_reply = "".join(reply_parts).strip()
        if full_reply:
            log.info("JARVIS: %s", full_reply)
            if tts:
                await tts.speak(full_reply)
        print()


async def main():
    args = parse_args()
    config = load_config(args.config)
    setup_logging(config, terminal_override=args.debug)
    log = logging.getLogger("jarvis")

    tools = collect_tools(config)
    log.info("loaded %d tools total: %s", len(tools), [t.name for t in tools])

    config.setdefault("persona", {})["system_prompt"] = _build_system_prompt(config, log)

    from llm import make_backend
    backend = make_backend(config, tools)

    # CLI override beats config; default is "voice".
    mode = (args.mode or (config.get("mode") or "voice")).lower()
    log.info("mode: %s", mode)

    tts = _make_tts(config, force_disable=args.no_tts)

    async with backend:
        if mode == "text":
            await run_text_mode(config, backend, log, tts)
        else:
            await run_voice_mode(config, backend, log, tts)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
    except Exception:
        logging.getLogger("jarvis").exception("fatal error")
        raise
