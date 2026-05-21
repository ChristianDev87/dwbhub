# ADR 0003 — Multi-Tenant + Multi-Guild from v1

**Status:** Accepted (2026-05-21)

## Context

The Spec-0 brainstorming explicitly decided that v1 must support multiple Discord guilds per deployment (operator wants to host two community servers from one DwbHub instance). The system also needs to be ready for a later SaaS variant where the deployment serves multiple paying tenants.

## Decision

- Every domain table carries `tenant_id BIGINT NOT NULL` (an internal `BIGSERIAL` surrogate key — **not** a Discord snowflake; Discord-side IDs use separate `TEXT` columns per ADR-0006). CI lint blocks new migrations without it (whitelist for `tenants` itself and pure lookup tables).
- One tenant owns one-to-many guilds: tenants↔guilds is the only place multi-guild logic shows up.
- URL routing uses `/t/<slug>/...` from day one. The default self-host deployment seeds exactly one tenant during the setup wizard.
- The repository layer mandates a `long tenantId` argument on every query. A defense-in-depth re-check happens in the service layer using the JWT `tid` claim.

## Consequences

- All later specs inherit a multi-tenant filter that must never be bypassed.
- Self-host deployments pay a tiny constant cost (one row in `tenants`, one slug in URLs) for full SaaS readiness.
- Cross-tenant data leaks become a single explicit threat surface tested in `DwbHub.Tests.Security`.
