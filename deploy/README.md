# Deploying PalDex to Docker / Portainer

This repo is public — nothing infra-identifying (real IPs, usernames,
hostnames) belongs in committed source or docs. Every deployment-specific
value below is a placeholder; fill in your own via `deploy/.env` (gitignored)
or your Portainer stack's environment variables.

Don't want Docker? See [`deploy/bare-metal.md`](bare-metal.md) for running
directly on a Linux LXC/VM instead — same config below applies, but a couple
of these variables behave differently (or need setting explicitly) once
there's no container providing their defaults.

## Required config

All of these are read env-var-first, falling back to `backend/secrets.py`
(gitignored, local dev only) — see `backend/config.py`:

| Variable | What it is |
|---|---|
| `RCON_PASSWORD` | Palworld server's RCON `AdminPassword` (from `PalWorldSettings.ini`) |
| `AMP_HOST` | IP/hostname of the AMP game server |
| `AMP_USER` | SSH account on the AMP host with the scoped sudo rsync rule below |
| `AMP_SAVE_ROOT` | Full path to the AMP instance's SaveGames dir, e.g. `/home/<amp-user>/.ampdata/instances/<instance>/palworld/<steam-app-id>/Pal/Saved/SaveGames/` |
| `AMP_WORLD_GUID` | The live world's save GUID (subdirectory under SaveGames) |
| `PALDEX_SSH_KEY` | *(Container-internal, usually left unset)* Path `backend/remote.py` reads the SSH private key from — defaults to `/run/secrets/paldex_ssh_key`, the Docker-secrets mount path `docker-compose.yml` already wires up via `PALDEX_SSH_KEY_HOST_PATH` below. You only need to set this directly if you're running outside that compose setup. |

Note the two similarly-named SSH key variables aren't interchangeable:
`PALDEX_SSH_KEY_HOST_PATH` (below) is a `docker-compose.yml` build-time
variable — the key's location *on the Docker host*, which Compose bind-mounts
into the container. `PALDEX_SSH_KEY` (above) is what `remote.py` actually
reads *inside* the container/process at runtime. For a standard Compose
deploy you only ever need to set `PALDEX_SSH_KEY_HOST_PATH`; `PALDEX_SSH_KEY`
exists for the non-Docker case where nothing does that mount for you.

## One-time setup

1. **Generate your own dedicated ed25519 keypair — do not reuse one across
   deployments, and never share a private key between installs.** Each
   self-hosted instance of this project should have its own unique keypair,
   generated on the machine that will run it:

   ```
   ssh-keygen -t ed25519 -f deploy/paldex_deploy_key -N ""
   ```

   This writes `deploy/paldex_deploy_key` (private) and
   `deploy/paldex_deploy_key.pub` (public) — both gitignored, never commit
   either. It's a separate keypair from whatever you use for your own
   interactive SSH access (don't point this at a personal key), scoped to
   nothing but the one sudo-limited rsync command below, so a compromise of
   this key alone can't do anything beyond re-reading that one save path.

   Add the **public** key to `<AMP_HOST>`'s `~/.ssh/authorized_keys` for
   `<AMP_USER>`:

   ```
   cat deploy/paldex_deploy_key.pub | ssh <amp-user>@<amp-host> 'cat >> ~/.ssh/authorized_keys'
   ```

   `<AMP_USER>` needs a scoped NOPASSWD sudoers rule limited to `rsync`
   against `AMP_SAVE_ROOT` — that rule is per-account, not per-key, so this
   new key inherits it automatically. `remote.py` only ever runs one sudo
   command (`rsync -a <AMP_SAVE_ROOT> /tmp/palworld-saves/`), so that's all
   the rule needs to cover:

   ```
   # /etc/sudoers.d/paldex-readonly — validate with `visudo -cf` before installing
   <amp-user> ALL=(root) NOPASSWD: /usr/bin/rsync -a <AMP_SAVE_ROOT> /tmp/palworld-saves/
   ```

   Use the **literal, concrete** `AMP_SAVE_ROOT` path here, not a wildcard —
   a quoted glob in a sudoers command spec can silently fail to match, which
   quietly disables NOPASSWD rather than erroring, and there's normally only
   one fixed instance path to cover anyway. See
   [`deploy/bare-metal.md`](bare-metal.md) for a related gotcha: this same
   rsync mirror has no `--delete`, so `/tmp/palworld-saves/` on the AMP host
   grows forever unless you separately cron-prune it.

2. **Copy the private key onto the Docker host**, e.g. into the same
   directory as `docker-compose.yml` at `deploy/paldex_deploy_key`. Keep
   permissions tight (`chmod 600`). Never commit it.

3. **Set the required config** (table above). Copy `deploy/.env.example` to
   `deploy/.env` and fill it in. `deploy/.env` is *not* next to
   `docker-compose.yml` (which sits at the repo root) — Compose only
   auto-loads a `.env` from the same directory as the compose file, so any
   direct `docker compose` invocation needs `--env-file deploy/.env`
   explicitly. Deploying via Portainer's own stack editor instead just needs
   these set in its environment variables section — no `--env-file` needed.

## Deploying

Portainer's **git-repository stack deploy** (Stacks → Add stack →
Repository, point it at this repo's URL, Dockerfile + docker-compose.yml at
the repo root) is the maintained path: push to the git remote, redeploy the
stack in Portainer to pick it up.

An earlier bootstrap deploy (before this repo had a remote) pushed the repo
straight to the Docker host over SSH/SFTP and ran Compose there directly —
no longer how this is deployed, but worth knowing Portainer picks up
anything on its managed Docker daemon automatically regardless of how it
got there, showing up as a running container and a
`com.docker.compose.project=paldex`-labeled stack either way.

**Gotcha if you ever do a manual SSH/SFTP push again: `sftpc`'s `put -r`
does not reliably recurse into nested subdirectories** — it silently
uploaded `frontend/index.html` but skipped `frontend/assets/` (227 files,
70MB of map textures + icons) entirely, no error, no partial directory even
created. Every image 404'd until this was caught by checking `find
frontend/assets -type f | wc -l` against the local count. **Fix:** tar the
directory locally (`tar -czf frontend_assets.tar.gz -C frontend assets`),
`put` the single archive file (single-file transfers are reliable), then
`tar -xzf` it on the remote host. Don't trust `put -r` on anything with
nested directories without verifying file counts match afterward.

The `paldex_data` named volume is seeded from the image's baked-in `data/`
(the static schematics/relics/bosses/etc. JSON from the Windows-only
CUE4Parse extractor) on first run, then accumulates the live-refreshed
`players.json`, `bases.json`, and the `data/saves/` save-file cache —
persists across container restarts/redeploys. **These live-refreshed files
contain real player names/positions — never commit `data/players.json` or
`data/bases.json`** (gitignored; they're regenerated by the refresh loop,
not meant to be static repo content like the other `data/*_static.json`
files).

## Updating static game data

The extractor (`extractor/PalExtract`) only runs on the Windows box against
the local Steam install — it's not part of the image. After rerunning it
(new schematics/effigies/etc. from a game patch), rebuild and redeploy the
image to pick up the refreshed `data/*_static.json` + `frontend/assets/`
icons; the named volume already has content by then, so the freshly-baked
static JSON won't automatically overwrite what's in the volume — either
`docker volume rm paldex_data` and let it re-seed cleanly (loses the
live-pulled save cache, which just re-pulls in 30s, harmless), or copy the
updated static JSON files into the volume manually.

## Issues the first deploy caught (already fixed in the code, noted for context)

- **`server.py` bound Flask to `127.0.0.1`**, fine for local Windows dev
  (browser on the same machine) but unreachable from outside the
  container's network namespace — Docker's port publish delivers traffic
  via the container's external interface, which a loopback-only bind never
  sees. Symptom: `curl` got a TCP connection but `Recv failure: Connection
  reset by peer` on every request. Fixed by binding `0.0.0.0`.
- **`backend/rcon.py` used the `mcrcon` PyPI package**, which times reads
  out via `signal.signal(SIGALRM, ...)` gated on `platform.system() !=
  "Windows"` — a silent no-op on local Windows dev (where this ran fine),
  but `signal.signal()` only works in a process's main thread, and the
  refresh loop runs on a background thread
  (`threading.Thread(target=_refresh_loop)` in `server.py`). Every RCON call
  raised `ValueError: signal only works in main thread of the main
  interpreter` once deployed — online/offline status silently degraded to
  "unknown" for every player (caught via `online_known: false` in
  `/api/players` with no visible error until the container logs were
  checked). Fixed by replacing it with a ~100-line hand-rolled Source RCON
  client using a plain `socket.settimeout()` instead — no external
  dependency, no thread restriction, identical behavior on both platforms.
- **`data/players.json`/`data/bases.json` (real player names/positions) got
  committed to the initial push** — they weren't gitignored (only
  `data/saves/` was), so a plain `git add -A` swept up the refresh loop's
  live output alongside the actually-static `data/*_static.json` files.
  Caught after the repo went public. Fixed by gitignoring both and rewriting
  git history to remove them from every commit, not just the latest — a new
  commit that deletes a file doesn't remove it from a public repo's
  reachable history, anyone could still fetch the earlier commit.

## Verifying

`docker logs -f <container>` should show the 30s refresh loop pulling
`Level.sav` + all player saves without SSH/rsync errors, and
`/api/schematics` (etc.) should return real data once a `view_as` player is
selected.
