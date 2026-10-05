using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// One A2S_INFO reading of the Valheim server.
/// </summary>
/// <param name="Name">Server name as it appears in the browser (host-controlled text — escape before posting).</param>
/// <param name="World">
/// World/save name. Source calls this field "map"; Valheim puts the world name in it,
/// which is why it is renamed here.
/// </param>
/// <param name="Players">Players connected right now.</param>
/// <param name="MaxPlayers">Slot limit the server reports (the Shockbyte plan says 10).</param>
/// <param name="PasswordProtected">True when the server requires the join password.</param>
/// <param name="VacSecured">VAC status. Informational only.</param>
/// <param name="Version">
/// The A2S "version" string. Valheim has historically reported a placeholder here
/// ("1.0.0.0") and put the real build in <paramref name="Keywords"/>, so prefer
/// <see cref="ValheimServerInfo.DisplayVersion"/> over reading this directly.
/// </param>
/// <param name="Keywords">
/// Raw A2S tag string. Valheim packs the build number and network version in here;
/// kept verbatim because the exact layout is not contractual and has changed between
/// patches.
/// </param>
/// <param name="SteamId">Server's Steam ID, or 0 when not supplied.</param>
/// <param name="RoundTrip">How long the query took — a crude latency signal for the status embed.</param>
public sealed record ValheimServerInfo(
    string Name,
    string World,
    int Players,
    int MaxPlayers,
    bool PasswordProtected,
    bool VacSecured,
    string Version,
    string Keywords,
    ulong SteamId,
    TimeSpan RoundTrip)
{
    /// <summary>
    /// Best available version string: the first dotted-numeric token in the
    /// keywords when there is one (that is where Valheim actually puts the build),
    /// otherwise the A2S version field, otherwise null.
    ///
    /// <para>Deliberately a heuristic over a parser. The keyword layout is not
    /// documented and has changed across patches, so this reads what looks like a
    /// version and gives up quietly rather than asserting a shape the server never
    /// promised.</para>
    /// </summary>
    public string? DisplayVersion
    {
        get
        {
            foreach (var token in Keywords.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0) continue;

                // A version token looks like 0.220.5: digits and dots, at least one dot.
                var dots = 0;
                var ok = true;
                foreach (var c in trimmed)
                {
                    if (c == '.') { dots++; continue; }
                    if (!char.IsDigit(c)) { ok = false; break; }
                }

                if (ok && dots >= 1 && char.IsDigit(trimmed[0])) return trimmed;
            }

            return string.IsNullOrWhiteSpace(Version) ? null : Version;
        }
    }
}

/// <summary>
/// Queries the clan's Valheim server with the Steam A2S_INFO protocol over UDP.
///
/// ── Why A2S and not an API ──
/// Vanilla Valheim ships no REST API and no RCON — unlike Satisfactory
/// (<see cref="SatisfactoryApiService"/>), which has a first-party HTTP
/// surface. The Steam query port is the only thing an unmodded Valheim server
/// will answer, so it is the only integration that needs nothing installed on
/// the host. That matters here: the server is on Shockbyte via Discord's Game
/// Servers, and panel/FTP access is not guaranteed.
///
/// ── What this can and cannot see ──
/// A2S_INFO gives a player COUNT, the world name, the slot limit, and the
/// password/VAC flags. It does NOT give player names: Valheim answers A2S_PLAYER
/// with blank entries, so there is no per-player feed, no playtime tracking and no
/// Discord↔player link to be built on this. That is a protocol limit, not a
/// missing feature — closing it requires a server-side mod (DiscordConnector),
/// which is a separate integration. A2S_PLAYER is therefore not implemented at
/// all rather than implemented and returning nothing useful.
///
/// ── The port ──
/// The query port is the GAME port + 1 (Valheim binds both), so a server on 22533
/// answers on 22534. <see cref="BotConfig.ValheimQueryPort"/> overrides that for
/// hosts which allocate the two independently.
///
/// ── Challenge handshake ──
/// Valheim requires the A2S challenge: the first request is answered with a
/// four-byte challenge (header 'A') that must be echoed back before the server
/// will send info (header 'I'). Both orders are handled — a server that replies
/// with info immediately is also valid — and the exchange is retried a bounded
/// number of times, because a challenge can legitimately be issued twice.
///
/// ── Failure semantics ──
/// <see cref="QueryAsync"/> returns null on ANY failure (offline, timeout, DNS
/// failure, truncated or malformed packet) and never throws. Callers MUST read
/// null as "unknown", NOT as "nobody online" — <see cref="ValheimStatusService"/>
/// depends on that distinction to avoid announcing an outage on one dropped
/// datagram. UDP has no delivery guarantee, so a lost packet is routine and
/// expected, not exceptional.
/// </summary>
public sealed class ValheimQueryService
{
    /// <summary>Valheim's default game port. The query port is this + 1.</summary>
    public const int DefaultGamePort = 2456;

    /// <summary>
    /// Per-attempt budget. UDP gives no failure signal, so an unreachable server
    /// costs exactly this much wall-clock; kept short because the poll loop and
    /// a slash command both wait on it.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many times to answer a challenge before giving up. Two is enough for
    /// the normal one-challenge exchange plus a re-issue; more would just extend
    /// the stall on a server that is looping.
    /// </summary>
    private const int MaxChallengeRetries = 2;

    private static readonly byte[] SimpleResponsePrefix = { 0xFF, 0xFF, 0xFF, 0xFF };

    private const byte InfoRequest = 0x54;        // 'T'
    private const byte InfoResponse = 0x49;       // 'I'
    private const byte ChallengeResponse = 0x41;  // 'A'

    /// <summary>The fixed A2S_INFO payload, sans any challenge suffix.</summary>
    private static readonly byte[] InfoPayload =
        Encoding.ASCII.GetBytes("Source Engine Query\0");

    private readonly ILogger<ValheimQueryService> _logger;
    private readonly BotConfig _config;

    public ValheimQueryService(ILogger<ValheimQueryService> logger, IOptions<BotConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// True once a host is set. Unlike the Satisfactory client there
    /// is no credential to check — A2S is unauthenticated, which is precisely why
    /// this integration works without panel access.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.ValheimHost);

    /// <summary>Game port players type in. Falls back to Valheim's default.</summary>
    public int GamePort => _config.ValheimGamePort > 0 ? _config.ValheimGamePort : DefaultGamePort;

    /// <summary>
    /// Port actually queried: the explicit override when set, else game port + 1.
    /// </summary>
    public int QueryPort => _config.ValheimQueryPort > 0 ? _config.ValheimQueryPort : GamePort + 1;

    /// <summary>"135.148.252.143:22533" — what someone pastes into the game to join.</summary>
    public string JoinAddress => $"{_config.ValheimHost}:{GamePort}";

    /// <summary>
    /// Queries the server. Null means "couldn't tell" — offline, mid-restart, a
    /// dropped datagram, or a firewalled query port. Never throws.
    /// </summary>
    public async Task<ValheimServerInfo?> QueryAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        var host = _config.ValheimHost.Trim();
        var port = QueryPort;

        try
        {
            var address = await ResolveAsync(host, ct);
            if (address is null) return null;

            var endpoint = new IPEndPoint(address, port);

            using var udp = new UdpClient(address.AddressFamily);
            udp.Client.ReceiveTimeout = (int)AttemptTimeout.TotalMilliseconds;

            var stopwatch = Stopwatch.StartNew();

            byte[]? challenge = null;

            for (var attempt = 0; attempt <= MaxChallengeRetries; attempt++)
            {
                var request = BuildInfoRequest(challenge);
                await udp.SendAsync(request, request.Length, endpoint);

                var payload = await ReceiveFromAsync(udp, endpoint, ct);
                if (payload is null) return null;   // timed out — treat as unreachable

                switch (Classify(payload))
                {
                    case PacketKind.Info:
                        stopwatch.Stop();
                        return Parse(payload, stopwatch.Elapsed);

                    case PacketKind.Challenge:
                        // Header (4) + 'A' (1) + four-byte challenge.
                        if (payload.Length < 9)
                        {
                            _logger.LogDebug("Valheim: truncated challenge from {Endpoint} ({Length} bytes)",
                                endpoint, payload.Length);
                            return null;
                        }

                        challenge = payload[5..9];
                        continue;   // answer it

                    default:
                        _logger.LogDebug("Valheim: unexpected reply from {Endpoint} ({Length} bytes)",
                            endpoint, payload.Length);
                        return null;
                }
            }

            _logger.LogDebug("Valheim: {Endpoint} kept re-issuing challenges; gave up after {Retries}",
                endpoint, MaxChallengeRetries);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline is the routine case, so this stays at Debug: a server that is
            // down must not fill the log with stack traces every poll.
            _logger.LogDebug(ex, "Valheim: query to {Host}:{Port} failed", host, port);
            return null;
        }
    }

    // ─── Wire helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the configured host, preferring IPv4 — the Shockbyte allocation is
    /// a bare v4 literal, and a v6 answer for a host with no v6 route would time
    /// out every poll. A literal IP short-circuits to no DNS at all.
    /// </summary>
    private async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;

        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0)
        {
            _logger.LogWarning("Valheim: host {Host} resolved to no addresses", host);
            return null;
        }

        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses[0];
    }

    /// <summary>
    /// Waits for a datagram from the expected endpoint, discarding anything from
    /// anywhere else and returning null on timeout.
    ///
    /// <para>The sender filter is not paranoia for its own sake: this socket is
    /// unconnected and bound to an ephemeral port, so any host on the internet can
    /// land a datagram in it. Without the check a spoofed reply could set the
    /// player count the bot then announces.</para>
    /// </summary>
    private static async Task<byte[]?> ReceiveFromAsync(UdpClient udp, IPEndPoint expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AttemptTimeout);

        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token);

                if (!result.RemoteEndPoint.Address.Equals(expected.Address)
                    || result.RemoteEndPoint.Port != expected.Port)
                {
                    continue;   // not our server — keep waiting within the budget
                }

                return result.Buffer;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // our own timeout, not a shutdown
        }
    }

    private static byte[] BuildInfoRequest(byte[]? challenge)
    {
        var length = 5 + InfoPayload.Length + (challenge?.Length ?? 0);
        var request = new byte[length];

        request[0] = 0xFF;
        request[1] = 0xFF;
        request[2] = 0xFF;
        request[3] = 0xFF;
        request[4] = InfoRequest;

        InfoPayload.CopyTo(request, 5);
        challenge?.CopyTo(request, 5 + InfoPayload.Length);

        return request;
    }

    private enum PacketKind { Unknown, Info, Challenge }

    /// <summary>
    /// Identifies a reply by its header. Split responses (prefix 0xFFFFFFFE) are
    /// reported as Unknown rather than reassembled: an A2S_INFO payload is well
    /// under the MTU, so a split one means something unexpected is answering, and
    /// guessing at it would be worse than declining.
    /// </summary>
    private static PacketKind Classify(byte[] payload)
    {
        if (payload.Length < 5) return PacketKind.Unknown;

        for (var i = 0; i < SimpleResponsePrefix.Length; i++)
            if (payload[i] != SimpleResponsePrefix[i]) return PacketKind.Unknown;

        return payload[4] switch
        {
            InfoResponse => PacketKind.Info,
            ChallengeResponse => PacketKind.Challenge,
            _ => PacketKind.Unknown,
        };
    }

    /// <summary>
    /// Parses an A2S_INFO response body. Returns null if the packet runs out
    /// mid-field — a truncated or hostile datagram must degrade to "unknown", not
    /// throw out of the poll loop.
    /// </summary>
    private ValheimServerInfo? Parse(byte[] payload, TimeSpan roundTrip)
    {
        var reader = new PacketReader(payload, 5);   // past header + 'I'

        try
        {
            reader.ReadByte();                       // protocol version — unused
            var name = reader.ReadString();
            var world = reader.ReadString();         // Source "map"; Valheim's world name
            reader.ReadString();                     // folder ("valheim")
            reader.ReadString();                     // game ("Valheim")
            reader.ReadInt16();                      // truncated app id
            var players = reader.ReadByte();
            var maxPlayers = reader.ReadByte();
            reader.ReadByte();                       // bots — always 0 for Valheim
            reader.ReadByte();                       // server type ('d')
            reader.ReadByte();                       // environment ('l'/'w')
            var visibility = reader.ReadByte();      // 0 public, 1 password-protected
            var vac = reader.ReadByte();             // 0 unsecured, 1 secured
            var version = reader.ReadString();

            // Everything past here is optional, announced by the extra-data flag.
            // Absent EDF is normal and not an error, so the rest is best-effort.
            var keywords = string.Empty;
            ulong steamId = 0;

            if (reader.HasMore)
            {
                var edf = reader.ReadByte();

                if ((edf & 0x80) != 0) reader.ReadInt16();            // game port
                if ((edf & 0x10) != 0) steamId = reader.ReadUInt64(); // server steam id
                if ((edf & 0x40) != 0)                                // spectator port + name
                {
                    reader.ReadInt16();
                    reader.ReadString();
                }
                if ((edf & 0x20) != 0) keywords = reader.ReadString();
                if ((edf & 0x01) != 0) reader.ReadUInt64();           // full app id
            }

            return new ValheimServerInfo(
                Name: name,
                World: world,
                Players: players,
                MaxPlayers: maxPlayers,
                PasswordProtected: visibility == 1,
                VacSecured: vac == 1,
                Version: version,
                Keywords: keywords,
                SteamId: steamId,
                RoundTrip: roundTrip);
        }
        catch (PacketReader.TruncatedException)
        {
            _logger.LogDebug("Valheim: A2S_INFO response was truncated ({Length} bytes)", payload.Length);
            return null;
        }
    }

    /// <summary>
    /// Minimal little-endian cursor over an A2S packet. Every read is bounds-checked
    /// and throws <see cref="TruncatedException"/>, which <see cref="Parse"/> turns
    /// into a null result — the packet is attacker-reachable, so an overrun must be
    /// a handled outcome rather than an unhandled exception in a background service.
    /// </summary>
    private struct PacketReader
    {
        public sealed class TruncatedException : Exception;

        private readonly byte[] _data;
        private int _index;

        public PacketReader(byte[] data, int index)
        {
            _data = data;
            _index = index;
        }

        public readonly bool HasMore => _index < _data.Length;

        private void Require(int count)
        {
            if (_index + count > _data.Length) throw new TruncatedException();
        }

        public byte ReadByte()
        {
            Require(1);
            return _data[_index++];
        }

        public short ReadInt16()
        {
            Require(2);
            var value = (short)(_data[_index] | (_data[_index + 1] << 8));
            _index += 2;
            return value;
        }

        public ulong ReadUInt64()
        {
            Require(8);
            ulong value = 0;
            for (var i = 7; i >= 0; i--) value = (value << 8) | _data[_index + i];
            _index += 8;
            return value;
        }

        /// <summary>
        /// Reads a null-terminated UTF-8 string. An unterminated string is
        /// truncation, not a string running to the end of the packet — accepting
        /// the latter would silently return a half-read field.
        /// </summary>
        public string ReadString()
        {
            var start = _index;

            while (_index < _data.Length && _data[_index] != 0) _index++;

            if (_index >= _data.Length) throw new TruncatedException();

            var value = Encoding.UTF8.GetString(_data, start, _index - start);
            _index++;   // consume the terminator
            return value;
        }
    }
}
