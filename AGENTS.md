# Agent Conventions

Verbindliche Konventionen für ALLE Agent-Sessions (Claude, Subagents, Codex, etc.) die an diesem Repo arbeiten. Diese Regeln entstanden nach dem Hotfix für Plan 0.4, als sich herausstellte dass die e2e-Tests seit Plan 0.3d silent rot waren weil weder lokal getestet noch CI-Logs ehrlich geprüft wurden.

**Diese Konventionen überschreiben jede frühere Erlaubnis "Tests sind optional", "Smoke kann übersprungen werden", "Docker ist heavy, skip wenn nicht praktisch".** Keine Ausnahmen mehr.

---

## Regel 1 — Lokale Tests sind verbindlich, NIE optional

Vor JEDEM `git commit` MUSS der vollständige relevante Test-Stack lokal grün sein:

| Änderung tangiert | MUSS lokal laufen vor commit |
|-------------------|------------------------------|
| Backend Code (Application/Data/Infrastructure/Api) | `dotnet test api/DwbHub.sln` |
| Backend SQL (Migrationen, Queries) | `dotnet test` MIT realer Postgres (Testcontainers) |
| Frontend Code (web/src/) | `pnpm --filter web test --run` |
| Frontend Routing / Guards / Pages | `pnpm --filter web test --run` **PLUS** lokaler Browser-Smoke (`docker compose up` + manuell `/` aufrufen) |
| PowerShell-Tools (tools/) | `Invoke-Pester -Path tools/tests/` |
| Compose / Dockerfiles | `docker compose -f deploy/compose/docker-compose.yml up -d --build` + health-checks |
| ci.yml workflow | Local-act dry-run wenn möglich, sonst Push-to-feature-branch und CI watchen |

**Subagent-Pflicht:** Jeder Subagent der Code committet MUSS in seinem STATUS-Report explizit auflisten welche dieser Tests er gelaufen ist UND welche nicht. Fehlende Tests sind grundsätzlich `DONE_WITH_CONCERNS`, niemals stille `DONE`.

**Anti-Pattern (in Plans 0.3d und früher gemacht — nie wieder):**
- ❌ "Container smoke may be skipped if Docker is heavy" → Closeout-Schritt
- ❌ "Frontend regression check via Vitest only" → reicht nicht für routing/guards
- ❌ "Manual verification optional"

---

## Regel 2 — CI-Logs werden IMMER selbst gelesen, conclusion-Feld reicht NICHT

Nach jedem CI-Push MUSS für jeden grünen UND roten Job der tatsächliche Log angesehen werden. Das `conclusion: success`-Feld in `gh pr view --json statusCheckRollup` ist NICHT vertrauenswürdig — Test-Scripts können fehlerhaft konfiguriert sein (siehe `scripts/e2e-up.ps1`-Bug der zwischen Plan 0.3d und 0.4 silent rot war).

**Verbindliche Schritte für jeden Release-PR (develop→main):**

1. Watch CI mit `gh pr checks <PR> --watch` bis alle Jobs konkludiert sind
2. **Für JEDEN Job einzeln** (auch die scheinbar grünen):
   ```bash
   gh run view <run-id> --repo <owner>/<repo> --job=<job-id> --log | \
     grep -iE "(error|fail|pass|test.*results|××F::)"
   ```
3. Parse explizit nach Test-Result-Markern. Bei Playwright: `passed` / `failed` Zeilen. Bei dotnet: `Bestanden:` / `Fehlgeschlagen:` Zeilen.
4. **Bei keinem expliziten "tests passed" Marker im Log: behandle den Job als FAIL** — selbst wenn conclusion=success.

**Niemals:** auf "conclusion: success" verlassen ohne den Log selbst zu lesen.

---

## Regel 3 — Frontend-Änderungen brauchen echten Browser-Test

Vitest mit `MemoryRouter initialEntries={['/route']}` testet NICHT das Routing- oder Guard-Verhalten zwischen Routes. Für jede Änderung an:

- `web/src/app/router.tsx`
- `web/src/app/SetupGuard.tsx` oder anderen Guards
- Routing-Logik in irgendwelchen Pages
- `App.tsx` Shell

MUSS ein echter Browser-Smoke gemacht werden:

```powershell
# In einem Terminal: stack up
docker compose -f deploy/compose/docker-compose.yml up -d --build
# In einem anderen: open browser, exercise the actual flow
# - Visit /
# - Visit /?lang=en
# - Verify redirect target renders
# - Verify expected H1 / content is visible (not just spinner)
```

Optional aber empfohlen: Playwright lokal: `pnpm --filter web exec playwright test` bevor der CI-Push.

---

## Regel 4 — Failure-Reports sind verbindlich, keine "stillen Tasks"

Wenn ein Subagent ein Test-Failure findet das er nicht selbst beheben kann, MUSS er es als `DONE_WITH_CONCERNS` mit explizitem Failure-Log reporten — nicht still ignorieren oder zu "pass" hoch-runden.

Wenn ein Subagent eine Annahme aus dem Plan korrigieren musste (z.B. SQL-Pattern, API-Shape, Konstruktor-Reihenfolge), MUSS das im Report explizit stehen mit der genauen Abweichung und Begründung.

---

## Regel 5 — Plan-Closeout-Verifikation

Jeder Plan-Closeout (z.B. Task 14) MUSS folgende Schritte enthalten — keine "kann übersprungen werden"-Klausel:

1. ✅ `dotnet test api/DwbHub.sln` — lokal, 0 Fehler
2. ✅ `pnpm --filter web test --run` — lokal, 0 Fehler
3. ✅ `Invoke-Pester -Path tools/tests/` — lokal, 0 Fehler
4. ✅ `tools/verify-openapi.ps1` — 0 drift
5. ✅ `docker compose up` + manueller Browser-Smoke aller Frontend-Routen die touched wurden
6. ✅ Nach CI-Push: Jeden Job-Log selbst gelesen + explizit nach Test-Result-Markern gegrept

Fehlt einer dieser Schritte: Closeout ist NICHT komplett, Release-PR wird NICHT geöffnet.

---

## Eskalationspfad

Wenn ein Schritt aus diesen Regeln aus echten technischen Gründen nicht durchführbar ist (z.B. Docker Desktop tot, Postgres-Image zieht nicht), MUSS:

1. Der Agent das explizit reportet (`BLOCKED` Status, NICHT `DONE`)
2. Der User gefragt werden ob temporär ein Schritt entfallen darf
3. Eine TODO-Markierung im PR-Body eingetragen werden mit der genauen ausstehenden Verifikation

Niemals heimlich überspringen.

---

## Historischer Kontext

Diese Konventionen wurden eingeführt nachdem zwischen Plan 0.3d (2026-05-22) und Plan 0.4 Release (2026-05-22) folgende Bugs silent durchgerutscht sind:

1. `scripts/e2e-up.ps1` schluckte Playwright's `$LASTEXITCODE` → 4 Releases lang als "success" gemeldet trotz roter e2e-Tests
2. `web/tests/e2e/smoke.spec.ts` testete eine seit Plan 0.3d unerreichbare HelloPage
3. `web/src/app/SetupGuard.tsx` blieb forever in "redirecting" state weil `useEffect([])` nach navigate() nicht re-run — SetupPage wurde NIE gerendert wenn User auf `/` startete

Alle drei wären bei Anwendung von Regel 1, 2 und 3 sofort gefangen worden. Der User musste sie selbst aus den CI-Logs ziehen — das ist nicht der Workflow, das ist Versagen.

Diese Konventionen sind die Reparatur.
