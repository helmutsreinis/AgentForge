# ADR 0040: Authenticated durable channels

Status: Accepted — 2026-08-12

## Decision

Raw webhook bytes are admitted only through the exact account adapter. Telegram validates its secret-token
header in fixed time and WhatsApp validates HMAC-SHA256 over the raw body. Normalized events require a durable
external-sender binding, attachment scan, replay hash, order key, and atomic inbox/audit commit.

Outbound sends use `channel:send` as an external-mutation capability. The exact recipient and content hash
must match a current approval, which is consumed before transport. Quiet hours, rate limits, bounded attempts,
and idempotency apply independently. Definite failures can retry; uncertain outcomes are terminal dead letters.

## Consequences

Provider callbacks cannot self-assign an actor or agent. Recipient substitution invalidates approval. Live
accounts are absent from default composition. Official adapters support text; media is typed failure until a
download/scanning adapter is explicitly configured.
