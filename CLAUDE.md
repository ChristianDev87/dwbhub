# CLAUDE.md

Diese Datei verweist auf die verbindlichen Agent-Konventionen für dieses Repo.

**Vor jeder Plan-Execution / jeder Code-Änderung MUSS [AGENTS.md](./AGENTS.md) gelesen und befolgt werden.**

Kurzform der nicht-verhandelbaren Regeln:

1. **Lokale Tests sind verbindlich, NIE optional.** Vor jedem `git commit`: alle relevanten Test-Suiten (dotnet + pnpm + Pester + Browser-Smoke) müssen grün laufen. Subagent-Reports MÜSSEN explizit auflisten welche Tests gelaufen sind.

2. **CI-Logs werden IMMER selbst gelesen.** `conclusion: success` ist nicht vertrauenswürdig — `gh run view --log` für JEDEN Job und nach Test-Result-Markern grep.

3. **Frontend-Routing-Änderungen brauchen echten Browser-Test.** Vitest mit `MemoryRouter` reicht NICHT — `docker compose up` + manuell `/` aufrufen + Redirect-Verhalten verifizieren.

4. **Failure-Reports sind verbindlich.** Subagents melden `DONE_WITH_CONCERNS` mit explizitem Log, nicht still hoch-runden.

5. **Plan-Closeout enthält 6 Pflicht-Schritte.** Keine "may be skipped" Klauseln.

Vollständiger Wortlaut: [AGENTS.md](./AGENTS.md)
