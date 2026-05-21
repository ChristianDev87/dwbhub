# ADR 0004 — License: AGPLv3

**Status:** Accepted (2026-05-21)

## Context

DwbHub's competitive moat is being a credible open-source self-host alternative to WidgetBot's SaaS lock-in. The license has to protect us against a competitor forking the code, hosting it as a closed-source SaaS, and out-marketing the upstream project.

## Decision

License the repository under [GNU AGPLv3](../../LICENSE). All contributions are subject to the same license (no CLA in v1; we re-evaluate before opening a commercial offering).

## Consequences

- Anyone who modifies DwbHub and runs it as a network service must publish their modifications under AGPLv3.
- A future open-core SaaS variant (operated by the maintainers) can dual-license premium add-ons separately without breaking the AGPL grant on the core.
- Some enterprise customers won't touch AGPL; that is an acceptable trade-off for v1.
- README and source headers reference the license unambiguously.
