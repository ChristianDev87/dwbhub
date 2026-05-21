# ADR 0002 — Dapper over Entity Framework Core

**Status:** Accepted (2026-05-21)

## Context

The persistence layer must give us full control over SQL while keeping mapping ergonomics. EF Core hides SQL behind LINQ and a change-tracker, which historically caused surprising query plans and complicated debugging when the v1 prototype evolved.

## Decision

Use Dapper as the only ORM. Schema migrations are managed by FluentMigrator. No `DbContext`, no LINQ-to-SQL generation. Each domain entity gets a dedicated repository interface in `DwbHub.Core` with a Dapper-based implementation in `DwbHub.Data`.

## Consequences

- Engineers must write SQL by hand. We accept that as a feature, not a bug.
- No implicit lazy loading or change-tracking surprises.
- Migrations are explicit SQL/Fluent calls; rollback paths are tested in CI.
- Test fixtures use Testcontainers for real Postgres; no in-memory provider needed.
