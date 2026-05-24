"""Config loader.

Reads config.yaml (or whatever path is passed). Supports ${ENV_VAR} substitution
anywhere in string values, and ${ENV_VAR:-default} for fallback values.
"""

from __future__ import annotations

import os
import re
from pathlib import Path
from typing import Any

import yaml


_ENV_PATTERN = re.compile(r"\$\{([A-Z_][A-Z0-9_]*)(?::-([^}]*))?\}")


def _substitute_env(value: Any) -> Any:
    """Recursively expand ${VAR} and ${VAR:-default} in string values."""
    if isinstance(value, str):
        def replace(match: re.Match) -> str:
            name, default = match.group(1), match.group(2)
            return os.environ.get(name, default if default is not None else "")
        return _ENV_PATTERN.sub(replace, value)
    if isinstance(value, dict):
        return {k: _substitute_env(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_substitute_env(v) for v in value]
    return value


def load_config(path: str | Path = "config.yaml") -> dict:
    """Load and parse config.yaml from disk, with env var substitution."""
    p = Path(path)
    if not p.exists():
        raise FileNotFoundError(
            f"Config file not found: {p.resolve()}\n"
            "Copy config.example.yaml to config.yaml and edit it."
        )
    with p.open("r", encoding="utf-8") as f:
        raw = yaml.safe_load(f) or {}
    return _substitute_env(raw)
