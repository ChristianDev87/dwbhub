# ADR 0007 — Bot Token Encryption (AES-256-GCM)

**Status:** Accepted (2026-05-21)

## Context

Discord bot tokens give full control over the bot. Storing them in plaintext would mean a single read-only DB compromise hands attackers permanent guild access. The operator chose web-UI token entry over env-vars (for multi-guild UX), so the tokens must live in the database — encrypted.

## Decision

- Encrypt every bot token with AES-256-GCM at the column level.
- Master key supplied via env var `DWBHUB_ENCRYPTION_KEY` (Base64-encoded 32-byte key).
- `ITokenEncryption` service exposes `Encrypt(plaintext)` returning `(ciphertext, nonce, auth_tag, key_version)` and `Decrypt(EncryptedToken)`.
- Backend self-tests the encryption at startup (encrypt + decrypt a probe string) and fails fast on mismatch.
- Key rotation is supported via the `key_version` column: new writes use the current version; old reads lazy-re-encrypt to the current version.
- Serilog redaction filter masks any value matching the Discord bot-token regex `MT[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+`.

## Consequences

- Loss of `DWBHUB_ENCRYPTION_KEY` means losing all stored bot tokens. The self-host docs make the operator's backup responsibility explicit.
- The encryption layer is testable in isolation (Plan 0.4 introduces it).
- Tokens never appear in API responses; the guild detail endpoint returns a `bot_token_preview` like `MTA...****` only.
