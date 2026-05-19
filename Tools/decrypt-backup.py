#!/usr/bin/env python3
"""
Decrypt a ClanGuard SQLite backup file (CGB1 format).

The bot ships backups as `clanguard-backup-<timestamp>.db.gz.enc` when
encryption is enabled. This script reverses the encryption step so you
end up with a plain `.db.gz` you can gunzip into a working SQLite file.

Usage
-----
    # Passphrase via flag:
    ./decrypt-backup.py \\
        --in  clanguard-backup-2026-05-19T05-00-00Z.db.gz.enc \\
        --out clanguard-backup-2026-05-19T05-00-00Z.db.gz \\
        --passphrase 'your-passphrase-here'

    # Passphrase via env (recommended — keeps it out of shell history):
    export CLANGUARD_BACKUP_PASSPHRASE='your-passphrase-here'
    ./decrypt-backup.py \\
        --in  clanguard-backup-2026-05-19T05-00-00Z.db.gz.enc \\
        --out clanguard-backup-2026-05-19T05-00-00Z.db.gz

    # Then to get the live DB:
    gunzip clanguard-backup-2026-05-19T05-00-00Z.db.gz
    sqlite3 clanguard-backup-2026-05-19T05-00-00Z.db '.tables'

Requires
--------
    pip install cryptography

File format (kept in sync with SqliteBackupCrypto.cs)
-----------------------------------------------------
    bytes 0..3      Magic "CGB1"
    byte  4         Format version (0x01)
    bytes 5..20     PBKDF2 salt (16 bytes)
    bytes 21..32    AES-GCM nonce (12 bytes)
    bytes 33..N-17  Ciphertext
    bytes N-16..N-1 AES-GCM authentication tag

Key derivation: PBKDF2-HMAC-SHA256, 210,000 iterations, 32-byte output.
"""

import argparse
import os
import sys

try:
    from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
except ImportError:
    sys.exit(
        "Missing dependency: pip install cryptography\n"
        "(or use a virtualenv: python3 -m venv .venv && "
        "source .venv/bin/activate && pip install cryptography)"
    )

MAGIC = b"CGB1"
VERSION = 0x01
SALT_SIZE = 16
NONCE_SIZE = 12
TAG_SIZE = 16
KEY_SIZE = 32
PBKDF2_ITERATIONS = 210_000
HEADER_SIZE = 4 + 1 + SALT_SIZE + NONCE_SIZE  # 33


def decrypt(in_path: str, out_path: str, passphrase: str) -> None:
    with open(in_path, "rb") as f:
        data = f.read()

    if len(data) < HEADER_SIZE + TAG_SIZE:
        sys.exit(
            f"File too small to be a valid backup: {len(data)} bytes < "
            f"{HEADER_SIZE + TAG_SIZE} minimum"
        )
    if data[:4] != MAGIC:
        sys.exit(f"Magic mismatch — expected {MAGIC!r}, got {data[:4]!r}. "
                 "Are you sure this is a CGB1 file?")
    if data[4] != VERSION:
        sys.exit(f"Unsupported format version 0x{data[4]:02X} "
                 f"(this script understands 0x{VERSION:02X})")

    salt = data[5:5 + SALT_SIZE]
    nonce = data[5 + SALT_SIZE:5 + SALT_SIZE + NONCE_SIZE]
    # AESGCM in `cryptography` expects ciphertext+tag concatenated, which
    # is exactly what our file layout gives us from HEADER_SIZE to end.
    body = data[HEADER_SIZE:]

    kdf = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=KEY_SIZE,
        salt=salt,
        iterations=PBKDF2_ITERATIONS,
    )
    key = kdf.derive(passphrase.encode("utf-8"))

    aesgcm = AESGCM(key)
    try:
        plaintext = aesgcm.decrypt(nonce, body, associated_data=None)
    except Exception as e:
        sys.exit(
            f"Decryption failed: {type(e).__name__}: {e}\n"
            "Wrong passphrase, or the file has been modified in transit."
        )

    with open(out_path, "wb") as f:
        f.write(plaintext)
    print(
        f"OK: {len(data):,} bytes encrypted → {len(plaintext):,} bytes plaintext"
        f"\n     wrote {out_path}"
    )


def main() -> None:
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    ap.add_argument("--in",  dest="in_path",  required=True,
                    help="Path to the encrypted .enc file")
    ap.add_argument("--out", dest="out_path", required=True,
                    help="Path to write the decrypted file (typically .db.gz)")
    ap.add_argument("--passphrase", required=False,
                    help="Passphrase. If omitted, read from "
                         "CLANGUARD_BACKUP_PASSPHRASE env var.")
    args = ap.parse_args()

    passphrase = args.passphrase or os.environ.get("CLANGUARD_BACKUP_PASSPHRASE")
    if not passphrase:
        ap.error("--passphrase not given and CLANGUARD_BACKUP_PASSPHRASE not set")

    decrypt(args.in_path, args.out_path, passphrase)


if __name__ == "__main__":
    main()
