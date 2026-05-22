# Disaster Recovery Runbook

**What this is:** Step-by-step procedure for rebuilding ClanGuard Bot from
the most recent encrypted Cloudflare R2 backup when the DigitalOcean droplet
is unrecoverable.

**When to use:** Droplet is destroyed, corrupted, or compromised; DO snapshot
restore is unavailable or insufficient; you need to move to a new droplet.

**Estimated time:** 30–45 minutes if you have all the prerequisites in hand.

---

## Prerequisites

Before you start, confirm you have access to all of these. If any one is
missing, **stop and recover that access first** — the runbook will fail
partway through otherwise.

- [ ] **Proton Pass vault** containing:
    - `DISCORD_BOT_TOKEN`
    - `ANTHROPIC_API_KEY`
    - `CLANGUARD_BACKUP_PASSPHRASE`
    - `CLANGUARD_HEARTBEAT_URL`
    - `CLANGUARD_R2_ACCESS_KEY_ID`
    - `CLANGUARD_R2_SECRET_ACCESS_KEY`
- [ ] **GitHub account** with admin access to `dklaver15/189th-clanbot`
  (needed to trigger a manual deploy from the Actions tab)
- [ ] **DigitalOcean account** with billing in good standing
- [ ] **Reserved IP `159.89.243.21`** currently assigned to the bot
  droplet — recovery moves it to the new droplet. Verify in DO Cloud →
  Networking → Reserved IPs.
- [ ] **Cloudflare account** with R2 read access to the `clanguard-backups`
  bucket. If you've lost dashboard access entirely, the R2 access key +
  secret in Proton Pass are enough to download via CLI (see Phase D).
- [ ] **Mac laptop** with Python 3 installed (`python3 --version` should
  return ≥ 3.9)
- [ ] **A working SSH keypair** you can add to the new droplet on creation

If the bot is currently in a degraded-but-not-dead state, you also have
the option of triggering a fresh backup before starting recovery — SSH
into the existing droplet and restart the container to skip ahead to the
next scheduled cycle, or extend `BackupHourUtc` temporarily.

---

## Procedure

### Phase A — Provision a new droplet

1. DigitalOcean Cloud → **Create** → **Droplets**.
2. Choose **Ubuntu 24.04 LTS**, **Basic** plan, same size as the current
   bot (or larger), region **NYC3** (matches the current setup).
3. Authentication: add your SSH public key.
4. Hostname: `ubuntu-clanbot-nyc3-recovery` (rename to drop the suffix
   later once verified).
5. Click **Create Droplet**. Note the auto-assigned IPv4 address —
   you'll use it for SSH and `scp` during Phases B–E. The Reserved IP
   (`159.89.243.21`) stays attached to the dead droplet until Phase F.
6. **Enable weekly backups** on the new droplet (Backups tab) so you don't
   recreate the original gap.

### Phase B — Install Docker on the new droplet

```bash
ssh root@<new-droplet-ip>

# Update and install Docker via the official convenience script
apt update && apt upgrade -y
curl -fsSL https://get.docker.com | sh

# Verify
docker --version
docker compose version
```

### Phase C — Restore secrets and clone the repo

```bash
# Same path the existing deploy workflow uses
mkdir -p /opt/clanguard
cd /opt/clanguard

# Initial clone (the deploy will refresh it later)
git clone https://github.com/dklaver15/189th-clanbot.git .
```

Now create `.env` with the secrets from Proton Pass. The safest way is
to open the file in `nano` and paste each value one at a time (rather
than echo'ing into the file, which leaves secrets in shell history):

```bash
nano .env
```

Contents (replace each `<...>` with the value from Proton Pass — the
exact secret names match what's expected by `docker-compose.yml`):

```
DISCORD_BOT_TOKEN=<from Proton Pass>
ANTHROPIC_API_KEY=<from Proton Pass>
CLANGUARD_BACKUP_PASSPHRASE=<from Proton Pass>
CLANGUARD_HEARTBEAT_URL=<from Proton Pass>
CLANGUARD_R2_ACCESS_KEY_ID=<from Proton Pass>
CLANGUARD_R2_SECRET_ACCESS_KEY=<from Proton Pass>
```

Save and lock it down:

```bash
chmod 600 .env
```

> **Note:** `google-credentials.json` does NOT need to be restored manually
> — the GitHub Actions deploy workflow recreates it from the
> `GOOGLE_CREDENTIALS_BASE64` secret on every push. (It's still required
> for Sheets and Calendar even though backups now go to R2.)

### Phase D — Download and decrypt the latest backup (on Mac laptop)

The encrypted backups in R2 can't be decrypted on the droplet without
first installing the Python `cryptography` package. The faster path is
to decrypt on your Mac and `scp` the result.

On your Mac, from a terminal:

```bash
# Make sure you have a local checkout of the repo (use existing if you have it)
cd ~/projects   # or wherever you keep code
git clone https://github.com/dklaver15/189th-clanbot.git 189th-clanbot-recovery
cd 189th-clanbot-recovery/Tools

# Set up an isolated Python environment for the decrypt script
python3 -m venv .venv
source .venv/bin/activate
pip install cryptography
```

**Download the most recent backup.** Two ways — pick whichever is faster
for you in the moment:

**Option 1 — Cloudflare dashboard (browser).** Sign in to Cloudflare →
**R2** → `clanguard-backups`. Sort by "Last modified" descending. Click
the most recent `clanguard-backup-*.db.gz.enc` row → **Download**. Move
the file into the `Tools` directory.

**Option 2 — AWS CLI (terminal).** Useful if you've lost dashboard access
or want to script the download. The R2 access key/secret in Proton Pass
work against the S3 API:

```bash
# One-time setup of a named profile (or use env vars per-command)
aws configure --profile clanguard-r2
# AWS Access Key ID: <CLANGUARD_R2_ACCESS_KEY_ID from Proton Pass>
# AWS Secret Access Key: <CLANGUARD_R2_SECRET_ACCESS_KEY from Proton Pass>
# Default region name: auto
# Default output format: json

# Endpoint is in appsettings.json → BackupR2.AccountEndpoint
ENDPOINT="https://f161644886fb569c5fef90f821f2b685.r2.cloudflarestorage.com"

# List backups, newest last
aws s3 ls s3://clanguard-backups/ \
  --endpoint-url "$ENDPOINT" \
  --profile clanguard-r2 \
  | sort

# Grab the most recent one
LATEST=$(aws s3 ls s3://clanguard-backups/ \
           --endpoint-url "$ENDPOINT" \
           --profile clanguard-r2 \
           | sort | tail -1 | awk '{print $4}')

aws s3 cp "s3://clanguard-backups/$LATEST" "./$LATEST" \
  --endpoint-url "$ENDPOINT" \
  --profile clanguard-r2
```

Then decrypt (passphrase via env var, not flag, to keep it out of shell
history):

```bash
export CLANGUARD_BACKUP_PASSPHRASE='<paste from Proton Pass>'

./decrypt-backup.py \
  --in  clanguard-backup-2026-05-19T05-00-00Z.db.gz.enc \
  --out clanguard-backup.db.gz

gunzip clanguard-backup.db.gz
# You now have clanguard-backup.db

# Sanity-check it's a valid SQLite file
file clanguard-backup.db
# Expected output: clanguard-backup.db: SQLite 3.x database, ...

# Optional: peek at row counts to confirm it's not empty
sqlite3 clanguard-backup.db 'SELECT COUNT(*) FROM RankHistory; SELECT COUNT(*) FROM EventAttendances;'

# Clear the passphrase from the environment as soon as you're done
unset CLANGUARD_BACKUP_PASSPHRASE
```

### Phase E — Copy the restored DB to the new droplet

```bash
scp clanguard-backup.db root@<new-droplet-ip>:/tmp/clanguard.db
```

Then on the new droplet, pre-populate the named Docker volume **before**
the first deploy runs (this avoids the bot ever starting up against an
empty DB):

```bash
ssh root@<new-droplet-ip>
cd /opt/clanguard

# Create the named volume that docker-compose will adopt.
# The "clanguard_" prefix is the compose project name (directory name).
docker volume create clanguard_bot-data

# Find where the volume lives on the host
MOUNT=$(docker volume inspect clanguard_bot-data --format '{{.Mountpoint}}')
echo "Volume mountpoint: $MOUNT"

# Place the restored DB inside it
cp /tmp/clanguard.db "$MOUNT/clanguard.db"
rm /tmp/clanguard.db  # don't leave a copy lying around

# Verify
ls -lh "$MOUNT/clanguard.db"
```

### Phase F — Reassign the Reserved IP and trigger a deploy

`DROPLET_HOST` in GitHub Actions points at the Reserved IP
(`159.89.243.21`), so the only thing standing between you and a working
deploy is moving that IP to the new droplet.

1. **DO Cloud → Networking → Reserved IPs**
2. Find `159.89.243.21` → click the **⋯** menu → **Reassign Reserved IP**
3. Select the new droplet → confirm
4. Reassignment is instant. Verify from your Mac:
   ```bash
   ssh root@159.89.243.21 hostname
   ```
   Should print the new droplet's hostname (e.g. `ubuntu-clanbot-nyc3-recovery`).
   If it still prints the dead droplet's hostname, the DO dashboard
   hasn't finished propagating — wait 30 seconds and retry.

Then trigger a deploy:

- GitHub → **Actions** → **Deploy to Droplet** → **Run workflow** →
  branch `main` → **Run workflow**.

Watch it green. The deploy will:
1. SSH into the new droplet (via the Reserved IP, which now points
   there)
2. `git fetch && git reset --hard` (a no-op since you just cloned,
   but harmless)
3. Recreate `google-credentials.json` from the `GOOGLE_CREDENTIALS_BASE64`
   secret
4. `docker compose build --no-cache`
5. `docker compose up -d --force-recreate`

Step 5 will adopt the volume you pre-populated in Phase E, so the
container starts directly against the restored database.

---

## Verification

After the deploy completes, run through this checklist:

```bash
ssh root@<new-droplet-ip>

# 1. Container is running
docker ps | grep clanguard-bot
# Expected: STATUS shows "Up X seconds"

# 2. Discord login and heartbeat both came up
docker logs clanguard-bot 2>&1 | grep -E "Logged in|HeartbeatService started"
# Expected: both lines present

# 3. Backup service picked up R2 as the provider (and not the legacy Drive client)
docker logs clanguard-bot 2>&1 | grep "SqliteBackupService started"
# Expected: line contains "Provider=CloudflareR2"

# 4. The restored DB is actually being used (not a freshly created empty one)
docker exec clanguard-bot sqlite3 /app/data/clanguard.db \
  'SELECT COUNT(*) AS rank_history, (SELECT COUNT(*) FROM EventAttendances) AS event_attendances FROM RankHistory;'
# Expected: numbers matching what you saw on the Mac during Phase D
```

Then in Discord:

- [ ] Run `/health` — embed renders cleanly, shows recent backup
  timestamp, all subsystems look healthy.
- [ ] Check Healthchecks.io dashboard — `clanguard-bot-heartbeat` is
  **green** with "Last Ping" < 60s ago. Your phone should have stopped
  receiving Pushover alerts.
- [ ] Spot-check a few features: AWOL list, recent rank changes,
  attendance for the latest event. Numbers should match what they were
  before the disaster (within the up-to-24h backup window).
- [ ] At the next scheduled backup window (05:00 UTC by default), confirm
  a fresh `clanguard-backup-*.db.gz.enc` lands in the R2 bucket. This
  proves the new droplet has working write access — important to verify
  before destroying the old one.

If anything fails verification, **do not destroy the old droplet yet**
— investigate first.

---

## Post-recovery cleanup

Once verified, in this order:

1. **Rename** the new droplet from `*-recovery` back to
   `ubuntu-clanbot-nyc3` in the DO dashboard.
2. **Destroy** the old droplet (if it still exists).
3. **Rotate every secret** that may have been exposed during the
   incident — at minimum the Discord token, since the old droplet's
   `.env` could be readable by whoever caused the failure. The R2 API
   token is also in that file; rotate it from the Cloudflare dashboard
   (R2 → Manage R2 API Tokens → Roll) and update both Proton Pass and
   the new droplet's `.env`. Update Proton Pass with each new value and
   run `docker compose up -d --force-recreate` on the droplet after.
4. **Update this runbook** with anything you learned that wasn't
   accurate. The next person to use it (or future-you) will thank you.

---

## Improvements worth making after recovery

Suggestions that would shorten future recovery, in rough order of value:

- **Back up the `.env` as a single Proton Pass note** — rather than
  six separate entries, store the full `.env` file contents as one
  encrypted note. Reduces "did I forget a variable?" risk during
  recovery.
- **Automated backup integrity check** — extend `SqliteBackupService`
  to decrypt + `gunzip` + `PRAGMA integrity_check` the previous day's
  backup as part of its cycle, stamping a `BotState.LastBackupVerifiedUtc`
  column. Surface it on `/health`. Turns "I should test the runbook
  quarterly" into "the bot proves its backups are restorable every
  night." Probably 100 lines of code.
- **Schedule a quarterly fire drill** — even with the integrity check
  above, the full Phases A–F procedure has steps that aren't covered
  (droplet provisioning, secret restoration, volume pre-population).
  Run the whole runbook against a throwaway droplet with a dev bot
  token once a quarter. Tier 2 (warm standby) is the natural next
  investment if you find yourself wanting faster RTO.
- **R2 bucket versioning + lifecycle rule** — Cloudflare R2 supports
  object versioning. Enabling it on `clanguard-backups` means an
  accidental delete (or a bug in the retention prune) doesn't burn
  the historical record. Pair with a lifecycle rule to expire old
  versions after, say, 60 days so storage doesn't grow unbounded.

---

## Path reference

For quick lookup when the runbook says "the path":

| Thing                          | Location                                                  |
|--------------------------------|-----------------------------------------------------------|
| Bot deploy root                | `/opt/clanguard` (on droplet)                             |
| Reserved IP (long-term address)| `159.89.243.21` — set as `DROPLET_HOST` in GH Actions     |
| Bot's `.env`                   | `/opt/clanguard/.env` (on droplet, mode 0600)             |
| Bot's `docker-compose.yml`     | `/opt/clanguard/docker-compose.yml` (on droplet)          |
| SQLite DB (inside container)   | `/app/data/clanguard.db`                                  |
| SQLite DB (host)               | Named volume `clanguard_bot-data` — find with `docker volume inspect` |
| Decrypt tool                   | `Tools/decrypt-backup.py` (in repo)                       |
| Backup encryption format       | See `SqliteBackupCrypto.cs` class comment                 |
| R2 bucket for backups          | `appsettings.json` → `BackupR2.Bucket`                    |
| R2 account endpoint            | `appsettings.json` → `BackupR2.AccountEndpoint`           |
| Storage provider selector      | `appsettings.json` → `BackupStorageProvider` (R2 or GoogleDrive) |
| GH Actions deploy workflow     | `.github/workflows/deploy.yml`                            |