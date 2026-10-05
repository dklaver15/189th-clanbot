# ClanGuard Bot

The Discord bot for the 189th. It started as an AWOL tracker and now runs most of the clan's day-to-day operations: activity and AWOL tracking, ranks and auto-promotion, events and attendance, XP seasons, polls, tickets, server protection, game-server integrations and the weekly officer briefing.

Built with C# on .NET 10 ([Discord.Net](https://github.com/discord-net/Discord.Net), EF Core on SQLite), deployed with Docker Compose to a DigitalOcean droplet.

## Features

**Membership and activity**
- Tracks messages and voice time per member (the AFK channel doesn't count).
- Flags members with fewer than 5 messages **and** under 1 hour of voice in a 28-day window with the AWOL role, then lists them in `#awol-list` after 2 days. Officers can kick members who have been listed for 7+ days with `/kick-awols`.
- Exempt roles (`ExemptRoles`, plus the Reserve role) are never flagged.
- Records joins, departures (left, kicked or banned) and invite attribution, and shows a member's full history with `/timeline`.

**Ranks and promotion**
- Tracks every rank change. `/promote` and `/demote` update the member's nickname rank prefix, and new recruits get the RCT prefix automatically.
- Promotes eligible members nightly: RCT → CPL on messages or voice time, CPL and above on event attendance at their current rank. SMA is assigned by hand.
- `/promote`, `/demote` and `/setnick` for officers.

**Events**
- `/event` opens a DM wizard for one-off or recurring events, with RSVP buttons, a waitlist, templates, banner images and reminders.
- Events sync to Google Calendar through a retrying outbox; `/comp-event` adds competitive-division events.
- Attendance is snapshotted from the events voice channels and feeds auto-promotion.
- Monthly meetings can be recorded and transcribed into minutes (Recorder and Transcriber sidecars).

**Engagement**
- XP seasons: XP from events, meetings, voice and chat, with daily caps that reset at Central midnight, a leaderboard and level-up DMs.
- `/poll` (native or anonymous), Question and Joke of the Day, scheduled reminders, UFC schedules and results, a Sleeper fantasy football league, and THE FINALS leaderboard lookups.
- Gamertag roster in Google Sheets (`/gamertags`, `/lookup`).

**Game servers**
- Satisfactory (via the Ficsit Remote Monitoring mod): status, playtime, production reports, power and rail charts, and factory maps.
- Valheim: status and playtime.

**Server protection**
- Account-age gate for brand-new accounts, a honeypot channel and a cross-channel spam trap, a nickname impersonation check, an invite-link filter, a phishing and token-grabber scanner, and audit-log and webhook monitoring.
- Alerts go to the security alerts channel. Ban and timeout alerts carry buttons to undo or escalate.
- Every action is logged and searchable with `/security-audit`.

**Officer tools**
- Weekly officer briefing written by Claude, covering activity, retention, recruiting, polls and invites.
- Support tickets with anonymous reporting, officer applications, Reddit recruiting leads, `/health`, and a Discord status monitor.

## Slash commands

`/command-catalog` shows each member the commands they can use. The table below uses the current `appsettings.json` values.

- **Officer+** means Manage Roles, Administrator, or an officer role (`ExemptRoles` minus the Reserve role).
- **HQ** means the feature's configured HQ role, or Administrator.
- Server Administrators pass every rank gate.

| Access | Commands |
|---|---|
| Everyone | `/awol-status` `/calendar` `/command-catalog` `/gamertags` `/lookup` `/my-invites` `/invite` (list and stats) `/promo-eligibility` `/timezone` `/xp` `/xp-leaderboard` `/xp-season` `/xp-dms` `/finals-rank` `/finals-club` `/sleeper-standings` `/sleeper-matchups` `/sleeper-link` `/ufc-schedule` `/ufc-results` `/satisfactory-status` `/satisfactory-mods` `/satisfactory-report` `/satisfactory-playtime` `/satisfactory-leaderboard` `/satisfactory-graph` `/satisfactory-production` `/satisfactory-trains` `/satisfactory-map` `/satisfactory-link` `/valheim-status` `/valheim-playtime` `/valheim-leaderboard` `/valheim-link` |
| SGT+ | `/poll` `/qotd` `/jotd` |
| 2ndLT+ | `/event` (create; members can also edit their own events), `/reminder`, `/invite` management |
| CPT+ | `/comp-event` `/say` `/add-event-credit` `/remove-event-credit` `/fix-rank-date` |
| MAJ+ | `/promote` `/demote` `/seed-promotion-credit` `/kick-awols` `/clear-awol-list` `/attendance` `/late-check` `/leads` `/usage-stats` |
| BG+ | `/security-audit` `/webhook-audit` `/briefing-now` |
| Officer+ | `/awol-check` `/clear-awol` `/banhammer` `/health` `/roster-export` `/setnick` `/squads` `/timeline` |
| HQ | `/xp-adjust` `/ticket-panel` `/setup-gamertags` `/gamertag-backfill-ids` `/setup-officer-app` |
| Administrator | `/purge-user` `/allow-new-account` `/bot-fix-channel-perms` |

When adding a command, register it in `DiscordBotService` and add it to the catalog in `CommandsCommandHandler.BuildCatalog`.

## Setup

### Discord application

1. Create an application in the [Discord Developer Portal](https://discord.com/developers/applications) and add a bot.
2. Enable the privileged intents **Presence**, **Server Members** and **Message Content**.
3. Invite the bot with the `bot` and `applications.commands` scopes.
4. Place the bot's role **above** every rank, AWOL and HQ role it manages. Otherwise it can't assign roles, rename, kick, ban or time those members out.

The features use these permissions:
- **Manage Roles**, **Manage Nicknames**, **Kick Members**, **Ban Members** and **Moderate Members** (timeouts).
- **Manage Messages**, **Manage Channels**, **Manage Webhooks**, **Manage Server** (invite tracking), **Create Invite** and **View Audit Log**.
- The usual send, embed, attach and read-history permissions.

The meeting recorder uses its own bot account (`RECORDER_BOT_TOKEN`).

### Configuration

Non-secret settings live in `appsettings.json` (the `BotConfig` section; every property is documented in `Core/BotConfig.cs`). Any setting can be overridden with an environment variable such as `BotConfig__WindowDays=28`.

Secrets are never committed. `docker-compose.yml` reads them from the environment and from `/opt/clanguard/.env` on the droplet:

| Variable | Used for |
|---|---|
| `DISCORD_BOT_TOKEN` | The bot's token |
| `ANTHROPIC_API_KEY` | Weekly briefing and meeting minutes (Claude) |
| `CLANGUARD_GOOGLE_SPREADSHEET_ID`, `CLANGUARD_ROSTER_SPREADSHEET_ID`, `CLANGUARD_RECRUIT_SPREADSHEET_ID`, `CLANGUARD_GOOGLE_CALENDAR_ID` | Google Sheets and Calendar |
| `CLANGUARD_BACKUP_PASSPHRASE`, `CLANGUARD_R2_*`, `CLANGUARD_BACKUP_DRIVE_FOLDER_ID` | Encrypted database backups |
| `CLANGUARD_HEARTBEAT_URL` | External uptime heartbeat |
| `SATISFACTORY_ADMIN_PASSWORD` or `SATISFACTORY_API_TOKEN`, `FRM_AUTH_TOKEN` | Satisfactory server API and the FRM mod |
| `RECORDER_BOT_TOKEN`, `RECORDER_SHARED_SECRET`, `TRANSCRIBER_SHARED_SECRET` | Meeting recorder and transcriber sidecars |
| `YOUTUBE_CLIENT_ID`, `YOUTUBE_CLIENT_SECRET`, `YOUTUBE_REFRESH_TOKEN` | Clip uploads to YouTube |

Google access uses a service account. The deploy writes its key to `google-credentials.json`, which is mounted into the container read-only.

## Deployment

Pushing to `main` deploys automatically (`.github/workflows/deploy.yml`):

1. The build workflow compiles the bot in Release, checks that every model change has an EF migration, and syntax-checks the recorder and transcriber.
2. The deploy job SSHes to the droplet, resets `/opt/clanguard` to `main`, rebuilds the images with Docker Compose and restarts the bot.
3. The deploy fails if the bot hasn't logged "Bot is connected as" within 5 minutes.

Pushes to other branches run the build plus a Docker image check, without deploying.

Repository secrets used by the workflows: `DROPLET_HOST`, `DROPLET_SSH_KEY`, `GH_PAT`, `GOOGLE_CREDENTIALS_BASE64`, `ANTHROPIC_API_KEY` and `SIXLABORS_LICENSE_KEY`.

The bot applies pending database migrations itself at startup.

## Local development

```bash
dotnet build -c Debug
dotnet run
```

A local run needs a bot token (`BotConfig__Token`), a `google-credentials.json` in the working directory, and an Anthropic key (`Claude__ApiKey`); the bot doesn't start without them. Use a test server and your own bot application, not the production one.

To add a migration after changing an entity:

```bash
dotnet tool install --global dotnet-ef --version 10.0.12
dotnet ef migrations add <Name>
```

Debug builds work without the ImageSharp license key; Release builds need it (see [Third-party licenses](#third-party-licenses)).

## Data and backups

- The SQLite database, logs and meeting audio live in the Docker volumes `bot-data`, `bot-logs` and `meeting-audio`.
- A nightly job snapshots the database with `VACUUM INTO`, encrypts it with `CLANGUARD_BACKUP_PASSPHRASE`, and uploads it to Cloudflare R2 (Google Drive is also supported), keeping 14 days.
- `Tools/decrypt-backup.py` decrypts a backup.
- `RUNBOOK_DISASTER_RECOVERY.md` covers restoring the bot and its data.
- `SERVER_OWNER_RECOVERY.md` covers a compromised server-owner account.

## Project layout

C# files are grouped into folders by feature. Most namespaces don't match the folders: they follow a type-based split (`ClanGuardBot.Handlers`, `ClanGuardBot.Services`, `ClanGuardBot.Models`).

| Path | What's there |
|---|---|
| `Program.cs` | Startup and dependency injection |
| `Core/` | Gateway connection (`DiscordBotService.cs`), settings (`BotConfig.cs`), slash-command routing, health and watchdog services, Google Sheets client |
| `Data/`, `Migrations/` | Database context, shared entities and migrations |
| `Activity/` | Message and voice tracking, command usage stats, `/timeline` |
| `Awol/` | AWOL checks and kicks |
| `Members/` | Roster, departures and onboarding reminders |
| `Moderation/` | Server protection, security audits and webhook audits |
| `Ranks/` | Rank tracking and promotions |
| `Xp/` | XP ladder and seasons |
| `Events/`, `Polls/`, `Reminders/` | Clan events and calendar sync, polls, scheduled reminders |
| `Tickets/`, `OfficerApplications/` | Support tickets and officer applications |
| `Invites/`, `Gamertags/`, `Community/` | Invite tracking, gamertag roster, `/qotd`, `/jotd`, `/say` and bump reminders |
| `Meetings/`, `Videos/`, `Backup/` | Meeting recording and minutes, clip reposts and YouTube uploads, database backups |
| `Satisfactory/`, `Valheim/`, `Finals/`, `Sleeper/`, `Ufc/` | Game and league integrations |
| `Briefing/`, `AI/` | Weekly officer briefing and the Claude client |
| `RedditLeads/` | Reddit recruiting leads |
| `Recorder/` (Node.js), `Transcriber/` (Python) | Meeting recording sidecars |
| `Tools/` | Backup decryption script |

## Third-party licenses

- **SixLabors.ImageSharp** is used under the Six Labors Split License, granted to the 189th under its Apache License 2.0 terms. A Release build needs the license key: CI and the deploy read it from the `SIXLABORS_LICENSE_KEY` repository secret. For a local Release build, set `SixLaborsLicenseKey` or put the key in a `sixlabors.lic` file in the repo root (git-ignored). Never commit the key.
