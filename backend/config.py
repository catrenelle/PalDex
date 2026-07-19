"""Shared env-var-first, backend/secrets.py-fallback config loader — the
same pattern rcon.py's RCON_PASSWORD already used, now shared since
AMP_HOST/AMP_USER/AMP_SAVE_ROOT need it too. Previously these were hardcoded
directly in rcon.py/remote.py, which leaked real internal IPs/usernames once
this repo went public — nothing infra-identifying belongs in committed
source, only in the gitignored backend/secrets.py (local dev) or a
deployment's env vars (container)."""

import os


def get_config(name: str) -> str:
    env_val = os.environ.get(name)
    if env_val:
        return env_val
    try:
        module = __import__("secrets")
        return getattr(module, name)
    except (ImportError, AttributeError) as e:
        raise RuntimeError(
            f"{name} not set — provide it via the {name} env var (container) "
            "or backend/secrets.py (local dev)."
        ) from e
