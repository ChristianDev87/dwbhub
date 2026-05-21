# ADR 0006 — Discord Snowflakes as Strings in JSON

**Status:** Accepted (2026-05-21)

## Context

A v1-prototype bug rounded the last three digits of Discord snowflakes after they passed through `JSON.parse` in the browser. JavaScript `Number` is only precise up to 2^53; snowflakes are 64-bit integers (18–19 digits). The corrupted IDs caused 404s on follow-up API calls.

## Decision

- Backend wraps snowflakes in a `DiscordSnowflake` value object (C# backing field: `long`). The value object serialises to a JSON string (via custom `JsonConverter<DiscordSnowflake>`) and persists to a `TEXT` column in the database.
- DTOs never expose snowflake fields as `long`; they always go through `DiscordSnowflake`.
- Frontend type: `type DiscordSnowflake = string`. An ESLint rule (`dwbhub/no-snowflake-parse-int`) bans `parseInt`/`Number(...)` on `DiscordSnowflake`-typed values.
- A round-trip test in `DwbHub.Tests.Security` exercises a worst-case 19-digit value.

## Consequences

- All Discord-ID handling in the codebase is funnelled through one value type.
- Database columns storing snowflakes use `TEXT` (matching the wire format) rather than `BIGINT`; queries compare strings. We accept a small index-size cost for the absolute safety guarantee.
