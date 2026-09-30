# ClanGuard Bot

A Discord bot that tracks clan member activity and flags inactive users for removal.

## What It Does

- **Tracks messages** — counts messages per user in a rolling 28-day window
- **Tracks voice time** — records time spent in voice channels
- **Assigns AWOL role** — users with <5 messages AND <1 hour voice get flagged
- **Notifies HQ** — 2 days after AWOL assignment, posts to your HQ channel with full user details
- **Slash commands** — members can check their own stats, officers can check others or clear AWOL status

## Requirements

### Discord Bot Setup

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications)
2. Create a **New Application** → name it whatever you want
3. Go to **Bot** → click **Add Bot**
4. Enable these **Privileged Gateway Intents**:
   - ✅ Presence Intent
   - ✅ Server Members Intent
   - ✅ Message Content Intent
5. Copy the **Bot Token** (you'll need this for deployment)
6. Go to **OAuth2 → URL Generator**:
   - Scopes: `bot`, `applications.commands`
   - Bot Permissions: `Manage Roles`, `Send Messages`, `Read Message History`, `View Channels`, `Connect`
7. Use the generated URL to invite the bot to your server

### Discord Server Setup

1. Create a role named `AWOL` (or whatever you configure)
2. **Important**: The bot's role must be **above** the AWOL role in the role hierarchy
3. Create a text channel named `hq` (or whatever you configure) for removal notifications
4. Optionally create roles named `Admin`, `Moderator`, `Officer` for exempt users

## Configuration

All settings are in `appsettings.json` and can be overridden via environment variables:

| Setting | Default | Env Var Override | Description |
|---------|---------|-----------------|-------------|
| `Token` | — | `BotConfig__Token` | Discord bot token |
| `AwolRoleName` | `AWOL` | `BotConfig__AwolRoleName` | Role to assign inactive users |
| `HqChannelName` | `hq` | `BotConfig__HqChannelName` | Channel for removal notifications |
| `WindowDays` | `28` | `BotConfig__WindowDays` | Activity tracking window |
| `MinMessages` | `5` | `BotConfig__MinMessages` | Min messages to stay active |
| `MinVoiceHours` | `1.0` | `BotConfig__MinVoiceHours` | Min voice hours to stay active |
| `AwolGraceDays` | `2` | `BotConfig__AwolGraceDays` | Days before HQ is notified |
| `CheckIntervalMinutes` | `60` | `BotConfig__CheckIntervalMinutes` | How often checks run |
| `ExemptRoles` | `Admin,Moderator,Officer,Bot` | `BotConfig__ExemptRoles` | Roles exempt from AWOL |

**Activity threshold logic**: A user is flagged AWOL when they have BOTH fewer than `MinMessages` messages AND less than `MinVoiceHours` voice time. Meeting either threshold keeps them safe.

## Slash Commands

| Command              | Who Can Use | Description |
|----------------------|------------|-------------|
| `/awol-status`       | Everyone | Check your own activity stats |
| `/awol-check @user`  | Officers+ | Check another user's activity |
| `/clear-awol @user`  | Officers+ | Clear a user's AWOL status (removes role and pending notifications) |
| `/poll`              | SGT+ | Create a poll — native Discord poll by default, or `anonymous:true` for a hidden-vote poll |

### Polls

`/poll` posts a poll in the channel it's run in. It's a hybrid:

- **Native (default):** a real Discord poll with the animated result UI. Votes are public.
- **Anonymous (`anonymous:true`):** a custom embed with vote buttons where only the running totals are shown — never who voted for what. Carries the waving 189th flag banner by default.

Options: `question`, `option1`–`option10` (a leading emoji like `🔥 Build a base` becomes the choice's emoji), `multiselect`, `hours` (1–768, default 24), `announce` (post a winner when it closes, default on), and `image` / `image_url` to override the banner (upload, or a Tenor/Giphy/direct link — same as event creation; anonymous polls only).

Every vote is persisted (anonymous votes from our buttons, native votes captured off the gateway), so polls feed the weekly officer briefing's participation section instead of evaporating like MEE6 polls. The poll auto-closes at its deadline; the creator or a moderator can also close an anonymous poll early with its **Close poll** button.

> Requires the **Guild Message Polls** gateway intent (already set in code) to capture native-poll votes.

## Deploy to Digital Ocean

### Option A: Docker on a Droplet (Recommended)

**1. Create a droplet**
- Image: Ubuntu 24.04
- Size: Basic $6/mo (1 vCPU, 1GB RAM) is plenty
- Add your SSH key

**2. SSH in and install Docker**
```bash
ssh root@your-droplet-ip

# Install Docker
curl -fsSL https://get.docker.com | sh

# Install Docker Compose plugin
apt install docker-compose-plugin
```

**3. Deploy the bot**
```bash
# Clone your repo (or scp the files)
git clone https://github.com/your-username/clanguard-bot.git
cd clanguard-bot

# Create your .env file
cp .env.example .env
nano .env  # paste your bot token

# Build and run
docker compose up -d

# Check logs
docker compose logs -f
```

**4. Updates**
```bash
git pull
docker compose up -d --build
```

### Option B: Bare Metal on a Droplet

```bash
# Install .NET 10 runtime
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0

# Build and publish
dotnet publish -c Release -o /opt/clanguard

# Create a systemd service
cat > /etc/systemd/system/clanguard.service << 'EOF'
[Unit]
Description=ClanGuard Discord Bot
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/clanguard
ExecStart=/root/.dotnet/dotnet /opt/clanguard/ClanGuardBot.dll
Restart=always
RestartSec=10
Environment=DOTNET_ENVIRONMENT=Production
Environment=BotConfig__Token=YOUR_TOKEN_HERE

[Install]
WantedBy=multi-user.target
EOF

systemctl enable clanguard
systemctl start clanguard

# Check status
systemctl status clanguard
journalctl -u clanguard -f
```

### Option C: Digital Ocean App Platform

1. Push your code to GitHub
2. Go to Digital Ocean → Apps → Create App
3. Select your repo
4. Set environment variable: `BotConfig__Token` = your bot token
5. Choose the $5/mo Basic plan
6. Deploy

## Local Development

```bash
# Clone
git clone https://github.com/your-username/clanguard-bot.git
cd clanguard-bot

# Set your token in appsettings.json (don't commit this!)
# Or use user secrets:
dotnet user-secrets set "BotConfig:Token" "your-token-here"

# Run
dotnet run
```

## Project Structure

```
ClanGuardBot/
├── Program.cs                    # Entry point, DI setup
├── appsettings.json              # Configuration
├── Models/
│   ├── BotConfig.cs              # Configuration model
│   └── Entities.cs               # EF Core entities
├── Data/
│   └── BotDbContext.cs           # Database context
├── Handlers/
│   ├── ActivityTrackingHandler.cs  # Message & voice tracking
│   └── SlashCommandHandler.cs      # Slash command processing
├── Services/
│   ├── DiscordBotService.cs      # Bot lifecycle & command registration
│   └── AwolCheckService.cs       # Background AWOL check loop
├── Dockerfile
├── docker-compose.yml
└── .env.example
```

## Database

Uses SQLite — zero configuration needed. The DB file (`clanguard.db`) is created automatically on first run. When using Docker, it's persisted in a named volume.

## Notes

- The bot needs to be running continuously to track voice time accurately. If the bot restarts, any in-progress voice sessions won't have their time counted for the period the bot was offline.
- The 28-day window resets per user, not globally. Each user's window starts when they're first seen.
- AWOL checks only run against users whose window has been open for the full 28 days, so new members get a grace period automatically.
- Exempt roles (Admin, Moderator, Officer, Bot) are never flagged as AWOL.