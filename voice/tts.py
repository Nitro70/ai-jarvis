"""Text-to-speech via Edge TTS + pygame for playback.

Edge TTS uses Microsoft's free cloud voices — requires an internet connection."""

from __future__ import annotations

import asyncio
import logging
import tempfile
from pathlib import Path

log = logging.getLogger("jarvis.voice.tts")


class TextToSpeech:
    def __init__(self, voice: str, enabled: bool = True):
        self.voice = voice
        self.enabled = enabled
        self._initialized = False

    def _lazy_init(self):
        if self._initialized:
            return
        import pygame
        pygame.mixer.init()
        self._initialized = True

    async def speak(self, text: str) -> None:
        if not self.enabled or not text.strip():
            return
        try:
            import edge_tts
            import pygame
        except ImportError:
            log.warning("edge_tts/pygame not installed — TTS disabled")
            return

        self._lazy_init()

        tmp = tempfile.NamedTemporaryFile(suffix=".mp3", delete=False)
        tmp.close()
        tmp_path = Path(tmp.name)
        try:
            communicate = edge_tts.Communicate(text, self.voice)
            await communicate.save(str(tmp_path))
            pygame.mixer.music.load(str(tmp_path))
            pygame.mixer.music.play()
            while pygame.mixer.music.get_busy():
                await asyncio.sleep(0.05)
            pygame.mixer.music.unload()
        except Exception:  # noqa: BLE001
            log.exception("TTS failed")
        finally:
            try:
                tmp_path.unlink()
            except OSError:
                pass
