"""Centralized logging setup. Always writes the full DEBUG log to a file;
optionally also mirrors to stderr."""

from __future__ import annotations

import logging
import sys
from pathlib import Path


def setup_logging(config: dict, terminal_override: bool = False) -> Path:
    """Initialize logging from the `logging` block in config.

    Returns the resolved log file path. Truncates the file each run.

    `terminal_override` forces terminal mirroring on (CLI --debug)."""
    log_cfg = config.get("logging") or {}
    log_path = Path(log_cfg.get("file", "jarvis.log"))
    mirror = bool(terminal_override or log_cfg.get("mirror_to_terminal", False))

    handlers: list[logging.Handler] = [
        logging.FileHandler(log_path, mode="w", encoding="utf-8")
    ]
    if mirror:
        handlers.append(logging.StreamHandler(sys.stderr))

    logging.basicConfig(
        level=logging.DEBUG,
        format="%(asctime)s.%(msecs)03d  %(levelname)-7s %(name)s  %(message)s",
        datefmt="%H:%M:%S",
        handlers=handlers,
    )
    # Quiet noisy third-party loggers but keep our own at DEBUG.
    for noisy in ("httpx", "httpcore", "urllib3", "asyncio",
                  "huggingface_hub", "filelock", "openai._base_client"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

    logging.getLogger("jarvis").info(
        "=== Jarvis starting — log: %s ===", log_path.resolve()
    )
    return log_path
