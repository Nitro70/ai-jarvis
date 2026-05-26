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
import os
import sys
from pathlib import Path

# Suppress noisy huggingface_hub warnings that scare non-developers but are
# not actionable: the symlinks warning ("activate Developer Mode or run Python
# as administrator") is just saying the cache will copy files instead of
# symlinking — works fine, uses more disk. The telemetry/HF_TOKEN nags are
# also irrelevant for our use case.
os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
os.environ.setdefault("HF_HUB_DISABLE_IMPLICIT_TOKEN", "1")

# Make our local packages importable when run from this directory.
sys.path.insert(0, str(Path(__file__).parent))

from core.config import load_config
from core.logging import setup_logging
from core.memory import load_memory
from core.tools import collect_tools


_WHISPER_SIZE_HINTS = {
    "tiny":      "~40 MB",   "tiny.en":   "~40 MB",
    "base":      "~75 MB",   "base.en":   "~75 MB",
    "small":     "~250 MB",  "small.en":  "~250 MB",
    "medium":    "~770 MB",  "medium.en": "~770 MB",
    "large-v1":  "~1.6 GB",  "large-v2":  "~1.6 GB",  "large-v3": "~1.6 GB",
    "large":     "~1.6 GB",
}


import re as _re

# Whisper model names are tightly constrained — letters, digits, dot, dash.
# Anything else is either a typo or someone trying to coerce a path. We
# reject it loudly rather than letting it flow into a filesystem path.
_WHISPER_SIZE_RE = _re.compile(r"^[A-Za-z0-9._-]+$")


def _hf_hub_cache_dir() -> Path:
    """Return the actual HF Hub cache root, honoring HF_HUB_CACHE / HF_HOME.

    huggingface_hub computes this from a small precedence chain; replicating
    it by hand would drift. Import the constant; fall back to the documented
    default if huggingface_hub isn't installed for some reason."""
    try:
        from huggingface_hub import constants as hf_const
        return Path(hf_const.HF_HUB_CACHE)
    except ImportError:
        return Path.home() / ".cache" / "huggingface" / "hub"


def _load_whisper_with_recovery(size: str, role: str):
    """Load a faster-whisper model with friendly progress messaging and
    auto-recovery from corrupt-cache failures.

    On first use a model has to be downloaded from HuggingFace (Systran/
    faster-whisper-{size}). The download can be slow for medium / large
    (hundreds of MB) and HF prints scary-looking warnings on stderr (most
    notably the "symlinks not supported" one) that look like errors but
    aren't. We print our own clear status before the call so the user knows
    what's happening.

    If a previous run was Ctrl+C'd mid-download, the partial file in the HF
    Hub cache corrupts the next load attempt. When we detect a load
    failure, blow away the cached model dir for THIS size and retry once.

    Honors HF_HUB_CACHE / HF_HOME so the recovery actually deletes the
    right directory when the user has customized cache location."""
    from faster_whisper import WhisperModel

    # Defense: `size` flows into a path string below; reject anything that
    # could escape the cache dir (".." segments, slashes, NUL bytes, etc).
    if not _WHISPER_SIZE_RE.match(size):
        raise ValueError(
            f"Refusing to load Whisper model with non-canonical name {size!r}. "
            "Use a name like 'tiny.en' / 'base.en' / 'small' / 'medium.en' / "
            "'large-v3'."
        )

    hint = _WHISPER_SIZE_HINTS.get(size, "")
    size_str = f" ({hint}, downloads on first use)" if hint else ""
    print(f"Loading Whisper {role} model: {size}{size_str}...", flush=True)

    try:
        return WhisperModel(size, device="cpu", compute_type="int8")
    except Exception as e:
        # Could be a corrupt partial download from a previous Ctrl+C, or a
        # genuine network/disk problem. Try one cache wipe + retry.
        cache_root = _hf_hub_cache_dir()
        # HF cache layout: models--Systran--faster-whisper-{size}/
        bad_dir = cache_root / f"models--Systran--faster-whisper-{size}"
        if bad_dir.exists():
            print(f"  load failed ({e.__class__.__name__}: {e})", flush=True)
            print(f"  clearing possibly-corrupt cache at {bad_dir} and retrying...",
                  flush=True)
            import shutil
            try:
                shutil.rmtree(bad_dir)
            except OSError as rm_err:
                print(f"  couldn't clear cache: {rm_err}", flush=True)
                raise
            try:
                return WhisperModel(size, device="cpu", compute_type="int8")
            except Exception as e2:
                print(f"  retry also failed: {e2}", flush=True)
                raise
        # No cache to clear — propagate the original error.
        raise


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

    cmd_model = _load_whisper_with_recovery(cmd_model_size, "command")
    wake_model = _load_whisper_with_recovery(wake_model_size, "wake-word")

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

    # voice.always_on=true: skip the Press-Enter prompt AND enable hybrid
    # follow-up listening. After every reply Jarvis listens for ~follow_up_seconds
    # for another command WITHOUT requiring the wake word; if you stay silent
    # for that long, it falls back to wake-word mode. Designed for the
    # "Jarvis can I get..." conversational use case.
    raw_always_on = voice_cfg.get("always_on", False)
    always_on = (raw_always_on is True
                 or str(raw_always_on).strip().lower() in {"true", "yes", "on", "1"})
    follow_up_seconds = float(voice_cfg.get("follow_up_seconds", 30))
    log.info("voice.always_on = %r -> %s (follow_up_seconds=%.1f)",
             raw_always_on, always_on, follow_up_seconds)

    if always_on:
        print(
            f"\nJarvis online (always-on). Say '{wake_word}' to start. "
            f"After each reply you can keep talking for ~{int(follow_up_seconds)}s "
            f"without the wake word; silence drops back to wake-word listening.\n"
            "Ctrl+C to quit.\n",
            flush=True,
        )
    else:
        print(
            f"\nJarvis online. Press Enter to start listening for '{wake_word}'.\n"
            "Once active, just say the wake word followed by your request — "
            "pause when done.\nCtrl+C to quit at any time.\n"
            "(To skip this prompt AND get follow-up listening: tick 'Always-on' "
            "in Settings → Voice.)\n",
            flush=True,
        )
        try:
            await asyncio.to_thread(input, "▶ Press Enter to start ")
        except (EOFError, KeyboardInterrupt):
            return

    async def handle_one_command(audio) -> None:
        """Transcribe + send to LLM + speak the reply. Used by both the
        initial post-wake command and follow-up commands in always-on mode."""
        print("⌛ Transcribing...", flush=True)
        user_text = listener.transcribe_command(audio)
        if not user_text:
            print("(silence — try again)\n")
            return

        print(f"You:    {user_text}")
        log.info("USER: %s", user_text)

        reply_parts: list[str] = []
        print("JARVIS: ", end="", flush=True)
        try:
            async for chunk in llm_backend.send(user_text):
                print(chunk, end="", flush=True)
                reply_parts.append(chunk)
        except Exception as e:  # noqa: BLE001
            log.exception("backend send() raised in voice loop")
            err = f"\n[backend error: {e}]"
            print(err, end="", flush=True)
            reply_parts.append(err)
        print()

        full_reply = "".join(reply_parts).strip()
        if full_reply:
            log.info("JARVIS: %s", full_reply)
            if tts:
                try:
                    await tts.speak(full_reply)
                except Exception:  # noqa: BLE001
                    log.exception("TTS failed (continuing)")
        print()

    while True:
        try:
            await listener.wait_for_wake()
        except (EOFError, KeyboardInterrupt):
            return

        print("✨ Yes, sir?", flush=True)

        # Inner loop: first iteration is the wake-word's command. Subsequent
        # iterations only run in always-on mode and use a longer no-speech
        # timeout (follow_up_seconds) so the user has room to think between
        # turns of a real conversation.
        is_follow_up = False
        while True:
            if is_follow_up:
                print(f"🎙️  Listening for follow-up "
                      f"(~{int(follow_up_seconds)}s, or stay quiet to drop "
                      f"back to wake-word mode)...", flush=True)
                giveup_override = follow_up_seconds
            else:
                print("🎙️  Listening for your command...", flush=True)
                giveup_override = None  # use listener's default

            try:
                audio = await listener.record_command(giveup_seconds=giveup_override)
            except (EOFError, KeyboardInterrupt):
                return

            if audio.size == 0:
                if is_follow_up:
                    # Quiet for follow_up_seconds — conversation's over.
                    print(f"(quiet — back to listening for '{wake_word}'.)\n",
                          flush=True)
                else:
                    print("(I didn't catch anything — going back to listening.)\n",
                          flush=True)
                break  # exit inner loop, return to wait_for_wake

            await handle_one_command(audio)

            if always_on:
                # Stay in the inner loop and listen for a follow-up.
                is_follow_up = True
                continue
            # Default mode: one command per wake-word activation.
            break


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
        try:
            async for chunk in llm_backend.send(user_text):
                print(chunk, end="", flush=True)
                reply_parts.append(chunk)
        except Exception as e:  # noqa: BLE001
            log.exception("backend send() raised in text loop")
            err = f"\n[backend error: {e}]"
            print(err, end="", flush=True)
            reply_parts.append(err)
        print()

        full_reply = "".join(reply_parts).strip()
        if full_reply:
            log.info("JARVIS: %s", full_reply)
            if tts:
                try:
                    await tts.speak(full_reply)
                except Exception:  # noqa: BLE001
                    log.exception("TTS failed (continuing)")
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
