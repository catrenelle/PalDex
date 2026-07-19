"""Pulls fresh Palworld save data off the AMP server, reusing a scoped
NOPASSWD sudo rule for the configured account (see runbooks/proxmox.md in
DiscordBot for the cluster context). Host/user/save-path are all env-var
(or backend/secrets.py) config, not hardcoded — see backend/config.py; this
is committed to a public repo, so nothing infra-identifying belongs in the
source itself. Two transport backends depending on platform:

- Windows (local dev box): the Bitvise SSH Client CLI tools
  (sexec.exe/sftpc.exe), matching the account's existing Bitvise-managed key.
- Linux (the deployed container): plain OpenSSH + `rsync -e ssh`, since
  Bitvise isn't available there — also simpler, one rsync pull replaces the
  old SFTP `get *.sav` step. Needs its own dedicated keypair (not the
  Bitvise one, which isn't exportable in a container-friendly form) added to
  the configured account's ~/.ssh/authorized_keys on the AMP host; see
  deploy/README.md.
"""

import os
import subprocess
import sys
from pathlib import Path

from config import get_config

HOST = get_config("AMP_HOST")
USER = get_config("AMP_USER")

# Full path to the AMP instance's SaveGames dir, e.g.
# "/home/amp/.ampdata/instances/<instance>/palworld/<steam-app-id>/Pal/Saved/SaveGames/"
REMOTE_SAVE_ROOT = get_config("AMP_SAVE_ROOT")
REMOTE_CACHE = "/tmp/palworld-saves/"
LIVE_WORLD_GUID = get_config("AMP_WORLD_GUID")

_IS_WINDOWS = sys.platform.startswith("win")


# ============ Windows dev: Bitvise SSH Client ============

SEXEC = r"C:\Program Files (x86)\Bitvise SSH Client\sexec.exe"
SFTPC = r"C:\Program Files (x86)\Bitvise SSH Client\sftpc.exe"


def _bitvise_ssh_args() -> list[str]:
    return [SEXEC, f"-host={HOST}", f"-user={USER}", "-pk=a", "-unat=y"]


def _check_sexec_result(result: subprocess.CompletedProcess, action: str) -> None:
    # sexec encodes the remote process's own exit code as 1000 + code
    # (see `sexec -help-codes`); anything else is a connection/auth failure.
    if result.returncode != 1000:
        raise RuntimeError(
            f"{action} failed (sexec exit {result.returncode}): "
            f"{result.stdout}\n{result.stderr}"
        )


def _refresh_remote_cache_bitvise() -> None:
    cmd = _bitvise_ssh_args() + [
        "-cmd=sudo -n /usr/bin/rsync -a "
        f"{REMOTE_SAVE_ROOT} {REMOTE_CACHE}"
    ]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    _check_sexec_result(result, "remote rsync")


def _pull_saves_bitvise(local_dir: Path) -> None:
    """Pulls Level.sav and all player .sav files down via SFTP."""
    local_dir.mkdir(parents=True, exist_ok=True)
    players_dir = local_dir / "Players"
    players_dir.mkdir(exist_ok=True)

    world_dir = f"{REMOTE_CACHE}0/{LIVE_WORLD_GUID}"
    # sftpc's -cmd parses commands separated by "; " on a single line, not
    # newlines (newlines get treated as garbage extra options).
    script = "; ".join(
        [
            f"lcd {local_dir}",
            f"cd {world_dir}",
            "get -o Level.sav",
            f"lcd {players_dir}",
            f"cd {world_dir}/Players",
            "get -o *.sav",
        ]
    )
    cmd = [SFTPC, f"-host={HOST}", f"-user={USER}", "-pk=a", "-unat=y", f"-cmd={script}"]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    # sftpc uses plain exit codes (0 = success), unlike sexec's 1000+N scheme.
    if result.returncode != 0:
        raise RuntimeError(
            f"sftp pull failed (exit {result.returncode}): {result.stdout}\n{result.stderr}"
        )


# ============ Container: OpenSSH + rsync ============

# Docker secret convention: a file mounted at /run/secrets/<name>. Override
# with the PALDEX_SSH_KEY env var if mounted somewhere else.
_DEFAULT_SSH_KEY_PATH = "/run/secrets/paldex_ssh_key"


def _ssh_key_path() -> str:
    return os.environ.get("PALDEX_SSH_KEY", _DEFAULT_SSH_KEY_PATH)


def _ssh_base_args() -> list[str]:
    return [
        "ssh",
        "-i", _ssh_key_path(),
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "BatchMode=yes",
        f"{USER}@{HOST}",
    ]


def _refresh_remote_cache_openssh() -> None:
    cmd = _ssh_base_args() + [f"sudo -n /usr/bin/rsync -a {REMOTE_SAVE_ROOT} {REMOTE_CACHE}"]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    if result.returncode != 0:
        raise RuntimeError(
            f"remote rsync failed (ssh exit {result.returncode}): "
            f"{result.stdout}\n{result.stderr}"
        )


def _pull_saves_openssh(local_dir: Path) -> None:
    """Pulls Level.sav and all player .sav files down via rsync-over-ssh —
    one incremental transfer, replacing the Windows path's two-tool SFTP
    script above."""
    local_dir.mkdir(parents=True, exist_ok=True)
    (local_dir / "Players").mkdir(exist_ok=True)

    world_dir = f"{REMOTE_CACHE}0/{LIVE_WORLD_GUID}"
    ssh_cmd = " ".join(
        ["ssh", "-i", _ssh_key_path(), "-o", "StrictHostKeyChecking=accept-new", "-o", "BatchMode=yes"]
    )
    # rsync can't take two differently-shaped remote sources (a file and a
    # dir) into one destination layout in a single invocation cleanly, so
    # pull them separately rather than trying to be clever with one command.
    level_cmd = ["rsync", "-a", "-e", ssh_cmd, f"{USER}@{HOST}:{world_dir}/Level.sav", str(local_dir) + "/"]
    players_cmd = [
        "rsync", "-a", "-e", ssh_cmd,
        f"{USER}@{HOST}:{world_dir}/Players/",
        str(local_dir / "Players") + "/",
    ]
    for pull_cmd in (level_cmd, players_cmd):
        result = subprocess.run(pull_cmd, capture_output=True, text=True, timeout=60)
        if result.returncode != 0:
            raise RuntimeError(
                f"rsync pull failed (exit {result.returncode}): {result.stdout}\n{result.stderr}"
            )


# ============ Dispatch ============


def refresh_remote_cache() -> None:
    """Runs the scoped NOPASSWD sudo rsync that mirrors live save data into
    /tmp/palworld-saves/ (world-readable afterwards, no further sudo needed)."""
    if _IS_WINDOWS:
        _refresh_remote_cache_bitvise()
    else:
        _refresh_remote_cache_openssh()


def pull_saves(local_dir: Path) -> None:
    if _IS_WINDOWS:
        _pull_saves_bitvise(local_dir)
    else:
        _pull_saves_openssh(local_dir)


def refresh_and_pull(local_dir: Path) -> None:
    refresh_remote_cache()
    pull_saves(local_dir)
