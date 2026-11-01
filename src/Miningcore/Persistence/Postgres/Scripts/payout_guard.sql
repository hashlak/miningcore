-- hashlak payout guard (docs/LEDGER.md in the hashlak repo; ADR 2026-11-01): reservation before broadcast, caps, breaker.
-- Idempotent. Apply once per database before deploying a build that carries the guard:
--   docker compose exec -T postgres psql -U miningcore -d miningcore -f - < payout_guard.sql   (infra/host/payout-guard.sh install)
-- Money columns are decimal(28,12) and timestamps are UTC `timestamp` columns, like every other Miningcore table.

SET ROLE miningcore;

-- one row per pool: breaker + the figures of the last run (observability; the guard recomputes them from the journal)
CREATE TABLE IF NOT EXISTS payout_guard_state
(
    poolid            TEXT PRIMARY KEY,
    tripped           BOOLEAN NOT NULL DEFAULT false,
    tripped_reason    TEXT NULL,
    tripped_at        TIMESTAMP NULL,
    day               DATE NULL,
    paid_today        decimal(28,12) NOT NULL DEFAULT 0,   -- Σ items reserved/paid/unresolved in the rolling 24 h at the last run
    expected_today    decimal(28,12) NOT NULL DEFAULT 0,   -- Σ block rewards credited in the rolling 24 h at the last run
    wallet_balance    decimal(28,12) NULL,                 -- hot wallet total as the handler reported it
    wallet_balance_at TIMESTAMP NULL,
    updated           TIMESTAMP NOT NULL DEFAULT (now() at time zone 'utc')
);

-- one row per payout attempt of a pool; written BEFORE the chain transaction
CREATE TABLE IF NOT EXISTS payout_runs
(
    run_id       UUID PRIMARY KEY,
    poolid       TEXT NOT NULL,
    coin         TEXT NOT NULL,                                 -- symbol, so the host script can insert payments rows
    status       TEXT NOT NULL CHECK (status IN ('reserved', 'paid', 'failed', 'unresolved')),
    amount_total decimal(28,12) NOT NULL CHECK (amount_total > 0),
    payee_count  INT NOT NULL CHECK (payee_count > 0),
    error        TEXT NULL,
    created      TIMESTAMP NOT NULL DEFAULT (now() at time zone 'utc'),
    updated      TIMESTAMP NOT NULL DEFAULT (now() at time zone 'utc')
);
CREATE INDEX IF NOT EXISTS idx_payout_runs_pool_status ON payout_runs (poolid, status);
CREATE INDEX IF NOT EXISTS idx_payout_runs_pool_created ON payout_runs (poolid, created DESC);

-- one row per payee of a run; `amount` is what was debited from balances when the run was reserved
CREATE TABLE IF NOT EXISTS payout_run_items
(
    run_id      UUID NOT NULL REFERENCES payout_runs (run_id),
    address     TEXT NOT NULL,
    amount      decimal(28,12) NOT NULL CHECK (amount > 0),
    sent_amount decimal(28,12) NULL,                               -- what the handler reports it sent
    recipient   BOOLEAN NOT NULL DEFAULT false,                    -- rewardRecipients address: no payments row (upstream rule)
    status      TEXT NOT NULL CHECK (status IN ('reserved', 'paid', 'failed', 'unresolved')),
    txid        TEXT NULL,
    error       TEXT NULL,
    updated     TIMESTAMP NOT NULL DEFAULT (now() at time zone 'utc'),
    PRIMARY KEY (run_id, address)
);
CREATE INDEX IF NOT EXISTS idx_payout_run_items_address ON payout_run_items (address);
CREATE INDEX IF NOT EXISTS idx_payout_run_items_status ON payout_run_items (status);
