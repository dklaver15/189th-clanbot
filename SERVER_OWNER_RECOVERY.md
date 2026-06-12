# 189th — Server Recovery Runbook: Compromised Owner Account

**Purpose:** What HQ does if the server owner's Discord account is hacked and starts spamming the server. Pin this in the HQ channel.

**Read this first — the hard truth:** A bot *cannot* ban, kick, or strip permissions from the server owner. Discord places the owner above every member and every bot by design. Our ClanGuard bot will catch and ban compromised *officer/admin* accounts (including HQ), but a compromised **owner** account can only be stopped by (a) the real owner regaining control, or (b) Discord Support. Your job during an incident is to **contain the damage** and **start the recovery clock** as fast as possible.

---

## Phase 1 — Immediate response (first ~15 minutes)

Do these in parallel; don't wait on each other.

1. **Do not click anything the owner account posts.** Hacked accounts almost always spam phishing/malware links. Tell members the same in a channel the attacker hasn't locked: *"The owner account is compromised. Do not click any links it posts. We're handling it."*

2. **Let the bot do its part.** ClanGuard auto-purges spam from accounts it can act on and bans compromised officer/admin accounts that trip the honeypot or age gate. It will **not** ban the owner (it can't) — expect a log line / alert saying so. That's expected, not a bug.

3. **Lock the channels being spammed.** Any officer with **Manage Channels** can stop the bleeding:
   - Open the spammed channel → **Edit Channel → Permissions → @everyone**.
   - Set **Send Messages** to ❌ (red).
   - Repeat for each affected channel, or set it on the parent **category** to lock everything under it at once.
   - This does **not** stop the owner (owner ignores channel permissions), but it stops *everyone else* — including any secondary compromised accounts — and makes the server quiet enough to coordinate.

4. **Reach the real owner out-of-band — phone, SMS, in person, anything but Discord.** The single fastest fix is the owner reclaiming their own account. Get them to Phase 2 immediately.

5. **Screenshot the abuse** (messages, timestamps, any links). You'll need evidence if this escalates to Discord Support.

---

## Phase 2 — Owner reclaims their account (owner does this)

If you can reach the owner, have them do this right away, ideally from a device that was *not* used when the account was hacked:

1. **Reset the password** at <https://discord.com/reset> (or Settings → My Account → Edit → Change Password). A password reset **logs out every active session**, which kicks the attacker off.
2. **Re-secure email.** If the account email password was also leaked, reset that first — otherwise the attacker just resets Discord again.
3. **Check authorized connections:** Settings → **Authorized Apps** and **Devices** — revoke anything unfamiliar.
4. **Enable / re-enable 2FA** and save the backup codes somewhere safe (not in Discord).

**If the attacker enabled their own 2FA and locked the owner out:** the owner cannot self-recover. They must file a Trust & Safety report (see below) and wait — this can take a week or more, so start it immediately.

> Submit a hacked-account report: <https://support.discord.com/hc/en-us/requests/new> → *Help & Support* → enter the account email → *Trust & Safety* / *Hacked or Compromised Account*. Reference: [Discord — My Account was Hacked or Compromised](https://support.discord.com/hc/en-us/articles/24160905919511-My-Discord-Account-was-Hacked-or-Compromised).

---

## Phase 3 — If the owner account can't be recovered: request an ownership transfer

This is the last resort, and Discord grants it **only** under strict conditions. As of 2026, Discord's stated criteria for a Support-forced ownership transfer are:

- The server has **100+ members** (by active-user count).
- The **current owner has not accessed their account for at least 30 days.**
- **You (the requester) have been active** on your own account in the last 30 days.
- **You have Administrator or Moderator permissions** in the server.
- The server is **not monetized.**

**Important caveats — this is where most requests stall:**
- If the owner's account was **disabled**, the transfer may be **denied**.
- If the owner's account was **banned** by Discord, Support generally **will not** transfer ownership at all.
- The 30-day-inactivity rule means there is **no instant transfer.** Containment (Phase 1) has to hold for up to a month.

**How to file the request:**
1. Go to <https://support.discord.com/hc/en-us/requests/new>.
2. *What can we help you with?* → **Help & Support**.
3. Enter the **email tied to your Discord account**.
4. *Type of Question?* → **Server Related Requests**.
5. → **Server Ownership Transfer Request**.
6. Attach your evidence (server ID, your user ID, screenshots, dates) and explain that the owner account is compromised/inaccessible.

Reference: [Discord — Requesting a Transfer of Server Ownership](https://support.discord.com/hc/en-us/articles/26286635870359-Requesting-a-Transfer-of-Server-Ownership).

> Note: ownership does **not** auto-transfer when an owner account goes inactive or disabled — it stays parked on the dead account until Discord manually moves it. Plan for the manual route.

---

## Phase 4 — Prevention (do these now, before it happens again)

The cleanest incident is the one that can't start. Lock these in:

**Owner account hardening**
- Long, unique password (a password manager, not reused anywhere).
- **2FA enabled**, with backup codes printed/stored offline.
- Treat the owner account as a high-value target: no random app authorizations, no clicking login links.

**Server settings**
- **Server Settings → Safety Setup / Moderation → require 2FA for moderator actions.** This means even a compromised admin can't ban/kick/delete en masse without 2FA.
- Keep a **trusted backup admin** account (a different real person) with Administrator — not to ban the owner (impossible), but to run containment and to be the eligible requester for a Phase 3 transfer.

**Consider a "vault owner" model**
- The safest setup for a clan this size: ownership sits on a low-activity, heavily-secured account that the owner rarely logs into, while day-to-day leadership runs on separate admin accounts. A rarely-used owner account has a much smaller attack surface.

**Bot positioning (ties to the honeypot change)**
- For ClanGuard to actually ban a compromised **HQ/officer** account, the **bot's role must sit ABOVE all HQ roles** in Server Settings → Roles. If the bot is below them, it falls back to alert-only and can't act. Verify this now.

---

## Quick reference

| Situation | Who can fix it | Speed |
|---|---|---|
| Compromised member/officer/admin | ClanGuard bot (auto-ban + purge), if bot role is above theirs | Seconds |
| Compromised owner — account recoverable | Owner: password reset → logs out attacker | Minutes |
| Compromised owner — locked out by attacker 2FA | Discord Trust & Safety ticket | Days+ |
| Owner account gone for good | Discord ownership-transfer request (strict criteria) | 30+ days |

**Containment is always available even when banning isn't:** lock channels, warn members, preserve evidence, and start the Discord ticket clock immediately.

---

### Sources
- [Discord — My Account was Hacked or Compromised](https://support.discord.com/hc/en-us/articles/24160905919511-My-Discord-Account-was-Hacked-or-Compromised)
- [Discord — Requesting a Transfer of Server Ownership](https://support.discord.com/hc/en-us/articles/26286635870359-Requesting-a-Transfer-of-Server-Ownership)
- [Discord Support — Server owner banned, ownership not transferred (criteria discussion)](https://support.discord.com/hc/en-us/community/posts/8319209920919-Server-owner-is-banned-and-Discord-Support-won-t-transfer-ownership)

*Last updated: 2026-06-11. Criteria are Discord's and can change at their discretion — verify against the linked articles before relying on them in an incident.*
