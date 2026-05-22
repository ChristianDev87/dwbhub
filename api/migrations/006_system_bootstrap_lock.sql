-- system_bootstrap_lock: singleton-by-UNIQUE table that gates the first-time
-- setup wizard. The API generates a CSPRNG bootstrap token on first start,
-- stores its SHA-256 hash here, and writes the plaintext to a volume file.
-- POST /api/setup/complete validates the token, runs the wizard, and atomically
-- UPDATEs consumed_at — preventing replay even under concurrent submissions.
-- Operator can reset via DELETE FROM system_bootstrap_lock + container restart.
CREATE TABLE system_bootstrap_lock (
    id                   BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    token_hash           BYTEA       NOT NULL UNIQUE,
    issued_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    consumed_at          TIMESTAMPTZ NULL,
    consumed_by_user_id  BIGINT      NULL REFERENCES users(id) ON DELETE SET NULL
);
