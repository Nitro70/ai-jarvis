"""Speech-to-text: wake-word listener + command recorder with auto-endpoint.

Uses faster-whisper for transcription and silero VAD (bundled with
faster-whisper) for cheap speech detection — only run Whisper on chunks
that actually contain speech, so CPU stays near idle when nothing's happening."""

from __future__ import annotations

import asyncio
import logging
import threading
import time

import numpy as np
import sounddevice as sd
from faster_whisper import WhisperModel

log = logging.getLogger("jarvis.voice.stt")

SAMPLE_RATE = 16000  # Whisper expects 16 kHz mono


def _has_speech(audio: np.ndarray) -> bool:
    if audio.size < SAMPLE_RATE // 4:
        return False
    try:
        from faster_whisper.vad import VadOptions, get_speech_timestamps
        opts = VadOptions(threshold=0.5, min_speech_duration_ms=200)
        return bool(get_speech_timestamps(audio, opts, sampling_rate=SAMPLE_RATE))
    except Exception:
        log.debug("VAD probe failed", exc_info=True)
        return True  # if VAD broken, assume speech (don't miss things)


def _last_speech_end_seconds(audio: np.ndarray) -> float | None:
    if audio.size < SAMPLE_RATE // 4:
        return None
    try:
        from faster_whisper.vad import VadOptions, get_speech_timestamps
        opts = VadOptions(threshold=0.5, min_speech_duration_ms=200)
        ts = get_speech_timestamps(audio, opts, sampling_rate=SAMPLE_RATE)
        if not ts:
            return None
        return ts[-1]["end"] / SAMPLE_RATE
    except Exception:
        return None


def transcribe(audio: np.ndarray, model: WhisperModel, with_vad: bool = True) -> str:
    if audio.size == 0:
        return ""
    t0 = time.perf_counter()
    segments, _ = model.transcribe(
        audio, language="en", beam_size=1, vad_filter=with_vad,
    )
    text = " ".join(seg.text.strip() for seg in segments).strip()
    log.debug("transcribed %.2fs in %.2fs: %r",
              audio.size / SAMPLE_RATE, time.perf_counter() - t0, text)
    return text


class VoiceListener:
    """Wake-word listener + command recorder.

    Wake-word phase runs a small Whisper model on rolling 2-second windows
    (only when VAD detects speech). Command phase records until ~silence_end
    seconds of silence after speech, then returns the buffer."""

    def __init__(
        self,
        wake_model: WhisperModel,
        command_model: WhisperModel,
        wake_words: tuple[str, ...],
        wake_listen_seconds: float = 2.0,
        wake_check_interval: float = 0.3,
        silence_end_seconds: float = 2.5,
        max_command_seconds: float = 30.0,
        command_giveup_seconds: float = 6.0,
    ):
        self.wake_model = wake_model
        self.command_model = command_model
        self.wake_words = tuple(w.lower() for w in wake_words)
        self.wake_listen_seconds = wake_listen_seconds
        self.wake_check_interval = wake_check_interval
        self.silence_end_seconds = silence_end_seconds
        self.max_command_seconds = max_command_seconds
        self.command_giveup_seconds = command_giveup_seconds

    async def wait_for_wake(self) -> None:
        """Block until the wake word is heard, then return."""
        log.info("entering wake-word listen mode")
        buf = np.zeros(0, dtype=np.float32)
        buf_lock = threading.Lock()
        max_buffer_samples = int(SAMPLE_RATE * (self.wake_listen_seconds + 1.0))

        def cb(indata, _frames, _time, _status):
            nonlocal buf
            with buf_lock:
                buf = np.concatenate([buf, indata.flatten()])
                if buf.size > max_buffer_samples:
                    buf = buf[-max_buffer_samples:]

        with sd.InputStream(
            samplerate=SAMPLE_RATE, channels=1, dtype="float32",
            callback=cb, blocksize=int(SAMPLE_RATE * 0.05),
        ):
            while True:
                await asyncio.sleep(self.wake_check_interval)
                with buf_lock:
                    snapshot = buf.copy()
                if snapshot.size < int(SAMPLE_RATE * 0.7):
                    continue
                window = snapshot[-int(SAMPLE_RATE * self.wake_listen_seconds):]
                if not _has_speech(window):
                    continue
                text = await asyncio.to_thread(
                    transcribe, window, self.wake_model, False,
                )
                if not text:
                    continue
                log.debug("wake-listen heard: %r", text)
                text_lower = text.lower()
                if any(w in text_lower for w in self.wake_words):
                    log.info("wake word detected in %r", text)
                    with buf_lock:
                        buf = np.zeros(0, dtype=np.float32)
                    return

    async def record_command(self) -> np.ndarray:
        """Record audio until silence-after-speech or hard cap."""
        log.info("command recording start")
        buf = np.zeros(0, dtype=np.float32)
        buf_lock = threading.Lock()

        def cb(indata, _frames, _time, _status):
            nonlocal buf
            with buf_lock:
                buf = np.concatenate([buf, indata.flatten()])

        start = time.monotonic()
        speech_ever = False

        with sd.InputStream(
            samplerate=SAMPLE_RATE, channels=1, dtype="float32",
            callback=cb, blocksize=int(SAMPLE_RATE * 0.05),
        ):
            while True:
                await asyncio.sleep(0.25)
                elapsed = time.monotonic() - start
                with buf_lock:
                    snapshot = buf.copy()

                if elapsed > self.max_command_seconds:
                    log.info("command recording: max duration")
                    break

                duration = snapshot.size / SAMPLE_RATE
                if duration < 0.5:
                    continue

                last = _last_speech_end_seconds(snapshot)
                if last is None:
                    if elapsed > self.command_giveup_seconds:
                        log.info("command recording: no speech in %.1fs", elapsed)
                        break
                    continue

                speech_ever = True
                silence = duration - last
                if silence >= self.silence_end_seconds:
                    log.info("command recording: silence %.2fs ≥ %.2fs",
                             silence, self.silence_end_seconds)
                    with buf_lock:
                        end_sample = int((last + 0.3) * SAMPLE_RATE)
                        return buf[:end_sample].copy()

        with buf_lock:
            if not speech_ever:
                return np.zeros(0, dtype=np.float32)
            return buf.copy()

    def transcribe_command(self, audio: np.ndarray) -> str:
        return transcribe(audio, self.command_model, with_vad=True)
