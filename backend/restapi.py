"""Optional fast live-position polling via Palworld's own built-in REST API
(https://docs.palworldgame.com/api/rest-api/players) — plain HTTP, HTTP Basic
Auth as "admin"/the server's `AdminPassword`, default port 8212.

This exists purely as a speed layer on top of the save-file position already
parsed in parse.py/refresh.py: the REST API's `location_x`/`location_y`
report straight from the live server process's own memory (not disk state),
so unlike Level.sav — which a vanilla dedicated server only rewrites every
`AutoSaveSpan` seconds (default 30) — it's accurate at whatever rate you
actually poll it. Confirmed to use the same raw Unreal world-unit coordinate
space as parse.py's `raw_x`/`raw_y` (both read off the same engine's live
Translation, just via two different transports), so no separate scale/offset
is needed before `coord.locate()` — only verified against the docs' field
schema, not yet cross-checked against a real live player on an actual
server; if a position renders offset once tested live, an X/Y axis swap is
the first thing to check.

Entirely opt-in: `fetch_positions()` raises RestApiError (not configured,
connection refused, wrong password, disabled, timeout — anything) whenever
this can't be used, and every caller is expected to treat that as "fall back
to the save-file position already in players.json," never as fatal. A
deploy that never sets REST_API_PASSWORD gets exactly today's behavior with
zero behavior change — see server.py's _fast_position_loop.

Uses stdlib `urllib` rather than the `requests` package, matching rcon.py's
own "hand-roll it instead of adding a dependency for one small client"
precedent.
"""

import base64
import json
import urllib.error
import urllib.request

from config import get_optional_config

HOST = get_optional_config("AMP_HOST")
PORT = get_optional_config("REST_API_PORT") or "8212"
PASSWORD = get_optional_config("REST_API_PASSWORD")

TIMEOUT_SECONDS = 3


class RestApiError(Exception):
    pass


def is_configured() -> bool:
    return bool(PASSWORD)


def _normalize_uid(player_id: str) -> str:
    # The REST API's playerId comes back as a bare 32-hex string, same as a
    # save file's PlayerUId without dashes — reinsert the standard 8-4-4-4-12
    # layout so it matches the uid key used everywhere else in this project
    # (same convention as rcon.py's own _normalize_uid).
    raw = player_id.strip().lower()
    if len(raw) == 32:
        return f"{raw[0:8]}-{raw[8:12]}-{raw[12:16]}-{raw[16:20]}-{raw[20:32]}"
    return raw


def fetch_positions() -> dict[str, tuple[float, float]]:
    """Returns {uid (dashed, lowercase): (raw_x, raw_y)} for every player the
    live server process currently knows about. Raises RestApiError on any
    failure — see module docstring, callers must not treat this as fatal."""
    if not PASSWORD:
        raise RestApiError("REST_API_PASSWORD not configured")

    url = f"http://{HOST}:{PORT}/v1/api/players"
    token = base64.b64encode(f"admin:{PASSWORD}".encode("utf-8")).decode("ascii")
    req = urllib.request.Request(url, headers={"Authorization": f"Basic {token}"})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT_SECONDS) as resp:
            payload = json.loads(resp.read())
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as e:
        raise RestApiError(f"REST API request failed: {e}") from e

    positions = {}
    for p in payload.get("players", []):
        player_id = p.get("playerId")
        x, y = p.get("location_x"), p.get("location_y")
        if not player_id or x is None or y is None:
            continue
        positions[_normalize_uid(player_id)] = (x, y)
    return positions
