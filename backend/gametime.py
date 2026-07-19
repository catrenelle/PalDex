"""Decodes Palworld's live in-game clock from Level.sav's
worldSaveData.GameTimeSaveData.GameDateTimeTicks (see parse.load_game_time_ticks).

Confirmed by direct calibration (two live Level.sav snapshots ~5 min apart,
2026-07-19): this is a monotonically-increasing clock in standard .NET-tick
units (10,000,000 ticks/second — cross-checked against the sibling
RealDateTimeTicks field, which advances in lockstep with real elapsed time).
Decoding "seconds into the current in-game day" as
`(ticks / 10_000_000) % 86400` matched a real observation (computed 00:34,
confirmed actually nighttime in-game at that moment).

NIGHT_START_HOUR/NIGHT_END_HOUR below are an UNVERIFIED placeholder —
no public documentation of Palworld's exact sunrise/sunset clock hour was
found. The only grounding: at default 1.0/1.0 DayTimeSpeedRate/
NightTimeSpeedRate, real-world day length is ~27 minutes and night ~5
minutes (community-documented) — if that 27:5 ratio also holds for the
in-game-clock hours themselves (unconfirmed assumption), night would span
roughly 3.75 of the 24 in-game hours. The window below is a round-number
placeholder consistent with that estimate and with the one confirmed
observation (00:34 = night). Needs real production observation across an
actual day/night transition to confirm or correct — see the PalDex repo
history for the investigation this came from. If it proves wrong, either
adjust these two constants or drop the (Day)/(Night) label entirely and
keep just the numeric clock (which is independently confirmed, unlike the
boundary hours).
"""

TICKS_PER_SECOND = 10_000_000
SECONDS_PER_DAY = 86400

NIGHT_START_HOUR = 21.0
NIGHT_END_HOUR = 1.0  # wraps past midnight


def decode_game_time(game_date_time_ticks: int) -> dict:
    seconds_of_day = (game_date_time_ticks / TICKS_PER_SECOND) % SECONDS_PER_DAY
    hour = seconds_of_day / 3600
    is_night = hour >= NIGHT_START_HOUR or hour < NIGHT_END_HOUR
    return {
        "hour": int(seconds_of_day // 3600),
        "minute": int((seconds_of_day % 3600) // 60),
        "is_night": is_night,
    }
