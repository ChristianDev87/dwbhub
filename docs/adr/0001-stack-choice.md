# ADR 0001 — Stack Choice (.NET 10 + React 19 + Postgres 17)

**Status:** Accepted (2026-05-21)

## Context

Spec 0 establishes the long-term technology stack. The v1 prototype already used .NET + React + Postgres with success; the briefing endorses continuing on the same lane.

## Decision

- **Backend:** .NET 10 / ASP.NET Core 10.
- **Frontend:** React 19 + TypeScript 5 + Vite 8 + TailwindCSS 4 + shadcn/ui.
- **Database:** PostgreSQL 17.

## Consequences

- Easy onboarding for developers already comfortable with the v1 prototype's stack.
- First-class Discord.Net support for the Discord integration.
- Postgres 17 is the current major release; we pin to it to benefit from incremental I/O improvements and to avoid an unnecessary major-version bump during the project's expected v1 lifetime.
- The team must keep up with .NET annual releases; an LTS-only policy would be a separate ADR.
