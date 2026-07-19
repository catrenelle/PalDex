"""Live online/offline status via Palworld's RCON (Source RCON protocol),
since the save files only ever show a player's last-known-at-logout
position, with no online flag at all.

Hand-rolled Source RCON client (the protocol is ~40 lines — auth packet,
then a command packet, length-prefixed) rather than the `mcrcon` PyPI
package: that library times reads out via `signal.signal(SIGALRM, ...)`
gated on `platform.system() != "Windows"` — silently a no-op on local
Windows dev (where this ran fine), but `signal.signal()` only works in a
process's main thread, and the container's refresh loop runs on a
background thread (`server.py`'s `_refresh_loop`), so every call raised
`ValueError: signal only works in main thread of the main interpreter` once
deployed. A plain `socket.settimeout()` sidesteps the whole thread
restriction and works identically on both platforms.
"""

import re
import select
import socket
import struct

from config import get_config

HOST = get_config("AMP_HOST")
PORT = 25575
TIMEOUT_SECONDS = 5

SERVERDATA_AUTH = 3
SERVERDATA_EXECCOMMAND = 2


class RconError(Exception):
    pass


RCON_PASSWORD = get_config("RCON_PASSWORD")


def _recv_exact(sock: socket.socket, n: int) -> bytes:
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise RconError("connection closed while reading a packet")
        buf += chunk
    return buf


def _send_packet(sock: socket.socket, pkt_id: int, pkt_type: int, payload: str) -> None:
    body = struct.pack("<ii", pkt_id, pkt_type) + payload.encode("utf8") + b"\x00\x00"
    sock.sendall(struct.pack("<i", len(body)) + body)


def _read_packet(sock: socket.socket) -> tuple[int, int, bytes]:
    (length,) = struct.unpack("<i", _recv_exact(sock, 4))
    body = _recv_exact(sock, length)
    resp_id, resp_type = struct.unpack("<ii", body[:8])
    return resp_id, resp_type, body[8:-2]


def _normalize_uid(raw: str) -> str:
    raw = raw.strip().lower()
    if len(raw) == 32:
        return f"{raw[0:8]}-{raw[8:12]}-{raw[12:16]}-{raw[16:20]}-{raw[20:32]}"
    return raw


def _rcon_command(command: str) -> str:
    with socket.create_connection((HOST, PORT), timeout=TIMEOUT_SECONDS) as sock:
        sock.settimeout(TIMEOUT_SECONDS)
        _send_packet(sock, 1, SERVERDATA_AUTH, RCON_PASSWORD)
        resp_id, _, _ = _read_packet(sock)
        if resp_id == -1:
            raise RconError("RCON authentication failed")

        _send_packet(sock, 2, SERVERDATA_EXECCOMMAND, command)
        # A single command response can span multiple packets — keep reading
        # until nothing more is immediately pending (same approach mcrcon
        # used, just without its signal-based read timeout).
        chunks = []
        while True:
            _, _, payload = _read_packet(sock)
            chunks.append(payload.decode("utf8"))
            if not select.select([sock], [], [], 0)[0]:
                break
        return "".join(chunks)


def get_online_uids() -> set[str]:
    resp = _rcon_command("ShowPlayers")
    uids = set()
    lines = resp.strip().splitlines()
    for line in lines[1:]:  # skip "name,playeruid,steamid" header
        parts = line.split(",")
        if len(parts) >= 2 and parts[1].strip():
            uids.add(_normalize_uid(parts[1]))
    return uids


def get_server_version() -> str | None:
    """RCON's "Info" command replies e.g. "Welcome to Pal Server[v1.0.1.100619]
    Pallet Town" — no separate version-only command exists."""
    resp = _rcon_command("Info").strip()
    match = re.search(r"\[v([\d.]+)\]", resp)
    return match.group(1) if match else None
