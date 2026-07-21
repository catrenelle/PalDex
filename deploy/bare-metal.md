# Running PalDex without Docker (bare LXC/VM)

Docker isn't required — `backend/server.py` is a plain Flask app. This guide
covers running it directly on a Linux LXC/VM (or any non-Windows box), which
uses the same OpenSSH/rsync code path `backend/remote.py` uses inside the
Docker image, just without the container around it.

If you want the Docker/Portainer path instead, see [`README.md`](README.md).

## Prerequisites

```
apt install python3-venv python3-dev build-essential git openssh-client rsync
```

- `build-essential` (or equivalent `g++`/`make`) — `palooz` (Oodle
  decompression) compiles a native C++ extension on install; without a
  compiler `pip install` fails with `g++: No such file or directory`.
- `openssh-client` + `rsync` — `backend/remote.py`'s Linux code path shells
  out to both directly; they're not Python dependencies, so `pip` won't
  install them for you.

## Setup

```
git clone <this repo> paldex
cd paldex
python3 -m venv .venv
.venv/bin/pip install -r backend/requirements.txt
```

## Config: two landmines to know about before you start

1. **The config module is genuinely named `backend/secrets.py`, which
   collides with Python's own standard-library `secrets` module.**
   `backend/config.py` resolves it via `__import__("secrets")`, which finds
   *whichever* `secrets` module is first on `sys.path` — that's only
   guaranteed to be this project's file if `backend/` itself is the
   directory Python was launched from (Python always prepends a launched
   script's own directory to `sys.path[0]`). Concretely:

   - **Works:** `cd backend && ../.venv/bin/python server.py`
   - **Works:** `/path/to/paldex/.venv/bin/python /path/to/paldex/backend/server.py`
     (any invocation where `server.py` itself, not a package/module name, is
     the thing being executed)
   - **Breaks silently:** `python -m backend.server` from the repo root, or
     pointing a WSGI server (gunicorn/uwsgi) at `backend.server:app`. Neither
     puts `backend/` on `sys.path[0]`, so `secrets` resolves to Python's own
     stdlib module instead — which has no `RCON_PASSWORD` attribute, so you
     get `RuntimeError: RCON_PASSWORD not set — provide it via env var or
     backend/secrets.py`, which reads exactly like a missing-config error
     even though the real problem is import shadowing. (There's also no
     `backend/__init__.py`, so `-m backend.server` doesn't work as a package
     import to begin with — you'd hit an import error before even reaching
     this.)

   **If you're wiring this into systemd/gunicorn, always target `server.py`
   directly as a script, never as a module.** See the systemd unit below.

2. **`PALDEX_SSH_KEY` has no default outside a container.** `remote.py`'s
   Linux path defaults to reading the SSH private key from
   `/run/secrets/paldex_ssh_key` — that's a Docker-secrets mount convention,
   not a real path on a bare LXC/VM. Outside Docker, you must set the
   `PALDEX_SSH_KEY` env var yourself to point at wherever you put the key
   (below), or every save-pull will fail trying to open a file that doesn't
   exist. This is separate from `PALDEX_SSH_KEY_HOST_PATH` in
   `deploy/.env.example`, which is a `docker-compose.yml`-only bind-mount
   variable — it does nothing outside Compose.

## Required config

Same as the Docker path — env vars first, falling back to `backend/secrets.py`
(gitignored, see `backend/config.py`):

**Despite the `AMP_` prefix, none of this actually requires AMP.** You just
need SSH access to whatever host runs the Palworld dedicated server process,
plus a sudo-scoped `rsync` against wherever it writes `Level.sav`/
`Players/*.sav` — a bare `PalServer` binary run under systemd/screen with no
panel at all works exactly the same way. AMP is just what the reference
setup and the examples below happen to use.

| Variable | What it is |
|---|---|
| `RCON_PASSWORD` | Palworld server's RCON `AdminPassword` (from `PalWorldSettings.ini`) |
| `AMP_HOST` | IP/hostname of the game server host (AMP or otherwise) |
| `AMP_USER` | SSH account on that host with the scoped sudo rsync rule below |
| `AMP_SAVE_ROOT` | Full path to the `SaveGames` dir wherever the dedicated server process writes it, **trailing slash matters** (rsync source semantics). AMP example: `/home/<amp-user>/.ampdata/instances/<instance>/palworld/<steam-app-id>/Pal/Saved/SaveGames/`. A bare (non-AMP) install typically uses `/home/<user>/Steam/steamapps/common/PalServer/Pal/Saved/SaveGames/` instead — check your own install. |
| `AMP_WORLD_GUID` | The live world's save GUID (subdirectory under SaveGames) |
| `PALDEX_SSH_KEY` | **Not optional outside Docker.** Absolute path to the private key from the step below. |

Either `export` these (e.g. in the systemd unit's `Environment=`/
`EnvironmentFile=`) or write a local `backend/secrets.py` — same format as
`deploy/.env.example` but as Python assignments, not `KEY=value` lines.
`PALDEX_SSH_KEY` specifically should be an env var either way — it's a
filesystem path, not a secret value, and `backend/secrets.py`'s fallback
exists for local dev convenience, not deployment.

## One-time setup on the game server host

1. **Generate a dedicated keypair** (don't reuse a personal one):
   ```
   ssh-keygen -t ed25519 -f ./paldex_deploy_key -N ""
   ```
2. **Add the public key** to `<AMP_USER>`'s `~/.ssh/authorized_keys` on that
   host (AMP or otherwise).
3. **Add a scoped NOPASSWD sudoers rule.** `remote.py` only ever runs one
   sudo command — `rsync -a <AMP_SAVE_ROOT> /tmp/palworld-saves/` — so that's
   all the rule needs to allow. Write it with your **literal, concrete**
   `AMP_SAVE_ROOT` value, not a wildcard: sudoers glob wildcards inside a
   `Cmnd_Alias`/command spec are easy to get subtly wrong (a quoted glob
   silently fails to match at all, so NOPASSWD quietly stops applying and
   every pull starts prompting for a password that never comes), and since
   this is normally one fixed instance path, there's no need to risk it.

   ```
   # /etc/sudoers.d/paldex-readonly — validate with `visudo -cf` before installing
   <amp-user> ALL=(root) NOPASSWD: /usr/bin/rsync -a /home/<amp-user>/.ampdata/instances/<instance>/palworld/<steam-app-id>/Pal/Saved/SaveGames/ /tmp/palworld-saves/
   ```

   Always validate before installing: `visudo -cf /etc/sudoers.d/paldex-readonly`
   — a broken sudoers file can lock out `sudo` system-wide.

4. **Copy the private key** onto the machine running PalDex, `chmod 600` it,
   and point `PALDEX_SSH_KEY` at its path.

## Known gotcha: `/tmp/palworld-saves/` grows forever on the game server host

The rsync in `remote.py` is a one-way mirror **without `--delete`** (on
purpose — deleting requires a wider sudoers grant, see the code comment in
`remote.py`). That means every save-backup snapshot the game itself has ever
written gets copied into `/tmp/palworld-saves/` and never removed, even after
the game's own server-side rotation clears its backups out (AMP or not — this
is the dedicated server's own autosave-backup behavior, unrelated to any
panel). Left alone for months, this can fill that host's root disk (this
happened for real — tens of GB accumulated silently). **Add a cron job on
the game server host** to prune it independently of the app:

```
# /etc/cron.d/paldex-saves-cleanup
0 3 * * * root find /tmp/palworld-saves -mtime +2 -delete
```

This only removes files untouched for 2+ days — the live save files get
their mtimes refreshed by the game's own autosave, so this won't touch
anything actually in use.

## Running it

```
cd backend
../.venv/bin/python server.py
```

Then open `http://<host>:5151`. It binds `0.0.0.0`, so it's reachable from
other machines on your network by default — put it behind a reverse proxy
(nginx/Caddy) if you want TLS or access control in front of it.

## Running as a systemd service

```ini
# /etc/systemd/system/paldex.service
[Unit]
Description=PalDex
After=network-online.target

[Service]
Type=simple
User=paldex
WorkingDirectory=/opt/paldex/backend
ExecStart=/opt/paldex/.venv/bin/python server.py
EnvironmentFile=/opt/paldex/paldex.env
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

`WorkingDirectory` + pointing `ExecStart` at `server.py` directly (not
`-m backend.server`) is what keeps the `secrets.py` import landmine above
from biting you. `paldex.env` is a plain `KEY=value` file (`systemd`'s
`EnvironmentFile` format, same keys as the table above, including
`PALDEX_SSH_KEY`) — keep it outside the git checkout and not world-readable
(`chmod 600`), since it holds `RCON_PASSWORD`.

```
sudo systemctl daemon-reload
sudo systemctl enable --now paldex
journalctl -u paldex -f
```

## Verifying

The journal should show the 30s refresh loop pulling saves with no SSH/rsync
errors, and `/api/players` should return real data within a few seconds of
startup.
