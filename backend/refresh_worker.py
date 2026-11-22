"""Standalone process for the heavy save-file refresh loop (refresh.run(),
every REFRESH_INTERVAL_SECONDS) — deliberately run as its own OS process,
spawned by server.py via subprocess.Popen, rather than a thread inside the
Flask process.

refresh.run() decompresses Level.sav + every player's .sav + scans a
23k+-entry item-container index every cycle — real CPU-bound work, not I/O
wait. A thread doing that holds the GIL and starves every other thread in
the same process for the duration, including server.py's own
_fast_position_loop (confirmed empirically 2026-11-22: configured for a 1s
tick, measured ~3s average while both ran as threads in one process) and
Flask's own request handling. A separate process has its own GIL, so the
heavy work here can never block anything in server.py.

No IPC beyond the filesystem: this writes the exact same data/live/*.json
files server.py already reads/serves and _fast_position_loop already
patches — nothing else changes.
"""

import time
import traceback

import refresh

REFRESH_INTERVAL_SECONDS = 30


def main() -> None:
    while True:
        try:
            refresh.run()
        except Exception:
            traceback.print_exc()
        time.sleep(REFRESH_INTERVAL_SECONDS)


if __name__ == "__main__":
    main()
