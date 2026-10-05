using System.Security.Cryptography;

namespace ClanGuardBot.Services;

/// <summary>
/// Symmetric authenticated encryption for SQLite backup files. AES-256-GCM
/// with a per-file 16-byte random salt and 12-byte random nonce; the key
/// is derived from a passphrase via PBKDF2-HMAC-SHA256 (210,000 iterations
/// — OWASP 2023 recommendation for PBKDF2-SHA256).
///
/// ── File format (self-contained, no external metadata needed) ──
///   bytes 0..3    Magic "CGB1" (0x43 0x47 0x42 0x31) — "ClanGuard Backup v1"
///   byte  4       Format version (currently 0x01)
///   bytes 5..20   PBKDF2 salt (16 bytes, random per file)
///   bytes 21..32  AES-GCM nonce (12 bytes, random per file)
///   bytes 33..N-17  Ciphertext (same length as plaintext)
///   bytes N-16..N-1 AES-GCM authentication tag (16 bytes)
///
/// Total fixed overhead: 4 + 1 + 16 + 12 + 16 = 49 bytes per file.
///
/// ── Why this format instead of `age` / `openssl enc` / a library wrapper ──
/// `age` would be lovely (modern, well-reviewed) but there is no maintained
/// .NET port we'd want to depend on. `openssl enc` is fine for shell scripts
/// but introduces an external binary dependency in the Docker image and is
/// painful to invoke from C#. Native .NET AesGcm is FIPS-validated when the
/// underlying platform is, has zero deps, and is hard to misuse when wrapped
/// in a typed API. The format here is deliberately the simplest thing that
/// is still authenticated — magic byte + version makes future upgrades easy
/// (bump version, branch on the byte during decrypt).
///
/// ── Operational warning ──
/// If you lose the passphrase, you lose every backup encrypted with it.
/// Anthropic / DigitalOcean / Google Drive cannot recover it. Store the
/// passphrase in a password manager BEFORE setting CLANGUARD_BACKUP_PASSPHRASE
/// in the environment.
///
/// ── Decryption ──
/// For offline recovery, see tools/decrypt-backup.py — a standalone Python
/// script that reads the same format and outputs the plaintext .db.gz.
/// Symmetric with EncryptFileAsync below; the format constants must stay
/// in sync between the two.
/// </summary>
internal static class SqliteBackupCrypto
{
    private static readonly byte[] Magic = { 0x43, 0x47, 0x42, 0x31 }; // "CGB1"
    private const byte FormatVersion = 0x01;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;          // 256-bit AES key
    private const int Pbkdf2Iterations = 210_000;
    private const int HeaderSize = 4 + 1 + SaltSize + NonceSize; // = 33

    /// <summary>Overhead added by encryption (header + auth tag). Useful for log messages.</summary>
    public const int FixedOverheadBytes = HeaderSize + TagSize;  // = 49

    /// <summary>
    /// Encrypts <paramref name="sourcePath"/> to <paramref name="destPath"/>
    /// using a key derived from <paramref name="passphrase"/>. Writes a
    /// self-contained file in the format documented on the class.
    ///
    /// Reads the entire source into memory. SQLite backups are bounded
    /// (well under 100MB even for very mature deployments), so streaming
    /// would add complexity for no benefit. AES-GCM in .NET 8 supports up
    /// to ~2GB per call, well above any plausible backup size.
    /// </summary>
    public static async Task EncryptFileAsync(
        string sourcePath,
        string destPath,
        string passphrase,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase must not be empty", nameof(passphrase));

        var plaintext = await File.ReadAllBytesAsync(sourcePath, ct);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(passphrase, salt);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally
        {
            // Best-effort wipe of the derived key bytes from heap.
            // Doesn't help against memory dumps but the cost is trivial.
            CryptographicOperations.ZeroMemory(key);
        }

        var output = new byte[HeaderSize + ciphertext.Length + TagSize];
        Magic.CopyTo(output, 0);
        output[4] = FormatVersion;
        Buffer.BlockCopy(salt,        0, output, 5,                              SaltSize);
        Buffer.BlockCopy(nonce,       0, output, 5 + SaltSize,                   NonceSize);
        Buffer.BlockCopy(ciphertext,  0, output, HeaderSize,                     ciphertext.Length);
        Buffer.BlockCopy(tag,         0, output, HeaderSize + ciphertext.Length, TagSize);

        await File.WriteAllBytesAsync(destPath, output, ct);
    }

    /// <summary>
    /// Decrypts a file produced by <see cref="EncryptFileAsync"/>. Validates
    /// the magic header and format version before attempting decryption so
    /// you get a clear error on truncated / wrong-tool / wrong-format input
    /// instead of an opaque "auth tag mismatch" crash.
    ///
    /// Throws <see cref="CryptographicException"/> on any of:
    ///   • file shorter than the minimum (header + tag) size
    ///   • magic header mismatch (not a CGB1 file)
    ///   • unsupported format version
    ///   • authentication tag verification failure (wrong passphrase, or
    ///     the ciphertext / tag has been modified in flight)
    /// </summary>
    public static async Task DecryptFileAsync(
        string sourcePath,
        string destPath,
        string passphrase,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase must not be empty", nameof(passphrase));

        var input = await File.ReadAllBytesAsync(sourcePath, ct);
        if (input.Length < HeaderSize + TagSize)
            throw new CryptographicException(
                $"File too small to be a valid encrypted backup ({input.Length} bytes < {HeaderSize + TagSize})");

        if (!input.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Magic header mismatch — not a ClanGuard backup file");

        if (input[4] != FormatVersion)
            throw new CryptographicException(
                $"Unsupported format version 0x{input[4]:X2} (this build understands 0x{FormatVersion:X2})");

        var salt = input.AsSpan(5, SaltSize).ToArray();
        var nonce = input.AsSpan(5 + SaltSize, NonceSize).ToArray();
        var ciphertextLength = input.Length - HeaderSize - TagSize;
        var ciphertext = input.AsSpan(HeaderSize, ciphertextLength).ToArray();
        var tag = input.AsSpan(HeaderSize + ciphertextLength, TagSize).ToArray();

        var key = DeriveKey(passphrase, salt);
        var plaintext = new byte[ciphertextLength];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            // AuthenticationTagMismatchException (subtype of CryptographicException)
            // throws here on wrong passphrase or tampered ciphertext.
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        await File.WriteAllBytesAsync(destPath, plaintext, ct);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password: passphrase,
            salt: salt,
            iterations: Pbkdf2Iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: KeySize);
}
