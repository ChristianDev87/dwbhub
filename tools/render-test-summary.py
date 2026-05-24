#!/usr/bin/env python3
"""Parse TRX / Vitest / Playwright JUnit XML result files and emit a markdown
PR comment in the DWBHUB_TEST_SUMMARY format.

Usage:
    python3 tools/render-test-summary.py <results-dir>

The script walks a staged-results directory for the six known suite files,
parses each one, and writes a markdown comment to stdout.

Exit codes:
    0  all tests passed (or no result files found)
    1  at least one test failed
    2  usage error (missing / invalid argument)
"""

import io
import os
import subprocess
import sys
import xml.etree.ElementTree as ET

# Ensure stdout is UTF-8 even on Windows consoles that default to cp1252.
# On CI (Linux) this is a no-op; on local Windows dev it allows the emoji
# characters in the markdown output to be written without a UnicodeEncodeError.
if sys.stdout.encoding and sys.stdout.encoding.lower() not in ("utf-8", "utf8"):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

MAX_FAILED_DISPLAY = 25

# Suite mapping: (relative path inside results-dir, display name)
# Order here controls the table row order.
SUITE_MAP = [
    ("backend/unit.trx",                "Backend Unit"),
    ("backend/integration.trx",         "Backend Integration"),
    ("backend/security.trx",            "Backend Security"),
    ("frontend/vitest.xml",             "Frontend Vitest"),
    ("e2e/playwright.xml",              "E2E Playwright"),
    ("discord-live/discord-live.trx",   "Discord Live"),
]


# ---------------------------------------------------------------------------
# Data model
# ---------------------------------------------------------------------------

@dataclass
class SuiteResult:
    name: str
    cases: int              # unique test count (de-duplicated for cross-browser runs)
    runs: int               # total executions (cases × browsers for Playwright)
    passed: int
    failed: int
    skipped: int
    duration_s: float
    failed_test_names: List[str] = field(default_factory=list)
    browsers: List[str] = field(default_factory=list)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def fmt_time(seconds: float) -> str:
    """Format a duration in seconds as a human-readable string."""
    if seconds < 1:
        return "<1s"
    if seconds < 60:
        return f"{int(round(seconds))}s"
    m, s = divmod(int(round(seconds)), 60)
    return f"{m}m {s}s"


def _parse_iso_timestamp(ts: str) -> Optional[datetime]:
    """Parse an ISO-8601 timestamp as returned by .NET TRX files."""
    ts = ts.strip()
    # datetime.fromisoformat handles most ISO-8601 variants in Python 3.11+;
    # for older runtimes we normalise the trailing timezone manually.
    try:
        return datetime.fromisoformat(ts)
    except ValueError:
        pass
    # Fallback: strip sub-second precision beyond 6 digits
    import re
    ts = re.sub(r'(\.\d{6})\d+', r'\1', ts)
    try:
        return datetime.fromisoformat(ts)
    except ValueError:
        return None


def _trx_duration_seconds(times_elem) -> float:
    """Return elapsed seconds from a TRX <Times start="..." finish="..."/> element."""
    if times_elem is None:
        return 0.0
    start_str = times_elem.get("start", "")
    finish_str = times_elem.get("finish", "")
    t_start = _parse_iso_timestamp(start_str) if start_str else None
    t_finish = _parse_iso_timestamp(finish_str) if finish_str else None
    if t_start is None or t_finish is None:
        return 0.0
    delta = (t_finish - t_start).total_seconds()
    return max(0.0, delta)


def get_commit_sha() -> str:
    """Return the 8-character short commit SHA for the current HEAD."""
    sha = os.environ.get("GITHUB_SHA", "")
    if sha:
        return sha[:8]
    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode == 0:
            return result.stdout.strip()[:8]
    except Exception:
        pass
    return "unknown"


def status_cell(suite: SuiteResult) -> str:
    """Return the Status column text for a suite row."""
    if suite.failed > 0:
        return f"❌ {suite.passed} / {suite.failed} fail"
    return "✅"


def cases_cell(suite: SuiteResult) -> str:
    """Return the Cases column text for a suite row.

    For cross-browser Playwright runs the cell shows ``11 × 2 = 22``.
    For all other suites it shows the plain case count.
    """
    if suite.runs > suite.cases > 0:
        multiplier = suite.runs // suite.cases
        return f"{suite.cases} × {multiplier} = {suite.runs}"
    return str(suite.cases)


# ---------------------------------------------------------------------------
# Parsers
# ---------------------------------------------------------------------------

def parse_trx(path: Path, display_name: str) -> SuiteResult:
    """Parse a .NET TRX result file."""
    tree = ET.parse(path)
    root = tree.getroot()

    # Counters are always under ResultSummary in the TRX files produced by
    # dotnet test (xUnit adapter). Fall back to counting UnitTestResult
    # elements if the element is somehow absent.
    counters = root.find(f"{TRX_NS}ResultSummary/{TRX_NS}Counters")
    if counters is not None:
        total   = int(counters.get("total",   "0") or "0")
        passed  = int(counters.get("passed",  "0") or "0")
        failed  = int(counters.get("failed",  "0") or "0")
        skipped = int(counters.get("notExecuted", "0") or "0")
    else:
        # Fallback: count UnitTestResult elements directly
        all_results = root.findall(f".//{TRX_NS}UnitTestResult")
        total   = len(all_results)
        failed  = sum(1 for r in all_results if r.get("outcome") == "Failed")
        skipped = sum(1 for r in all_results if r.get("outcome") == "NotExecuted")
        passed  = total - failed - skipped

    # Duration from <Times start="..." finish="..."/>
    times = root.find(f"{TRX_NS}Times")
    duration_s = _trx_duration_seconds(times)

    # Collect failed test names
    failed_names: List[str] = []
    for r in root.iter(f"{TRX_NS}UnitTestResult"):
        if r.get("outcome") == "Failed":
            failed_names.append(r.get("testName", "<unknown>"))

    return SuiteResult(
        name=display_name,
        cases=total,
        runs=total,
        passed=passed,
        failed=failed,
        skipped=skipped,
        duration_s=duration_s,
        failed_test_names=failed_names,
    )


def parse_vitest(path: Path, display_name: str) -> SuiteResult:
    """Parse a Vitest JUnit XML result file."""
    tree = ET.parse(path)
    root = tree.getroot()

    total    = int(root.get("tests",    "0") or "0")
    failures = int(root.get("failures", "0") or "0")
    errors   = int(root.get("errors",   "0") or "0")
    skipped  = int(root.get("skipped",  "0") or "0")
    failed   = failures + errors
    passed   = total - failed - skipped
    duration_s = float(root.get("time", "0") or "0")

    failed_names: List[str] = []
    for tc in root.iter("testcase"):
        if tc.find("failure") is not None or tc.find("error") is not None:
            cls  = tc.get("classname", "")
            name = tc.get("name", "")
            label = f"{cls} > {name}" if cls else name
            failed_names.append(label)

    return SuiteResult(
        name=display_name,
        cases=total,
        runs=total,
        passed=passed,
        failed=failed,
        skipped=skipped,
        duration_s=duration_s,
        failed_test_names=failed_names,
    )


def parse_playwright(path: Path, base_display_name: str) -> SuiteResult:
    """Parse a Playwright JUnit XML result file.

    Playwright emits one <testsuite hostname="chromium|firefox"> block per
    browser.  The same spec file / test case appears once per browser, so:
      total runs  = root @tests  (e.g. 22)
      unique cases = runs / n_browsers  (e.g. 11)
    """
    tree = ET.parse(path)
    root = tree.getroot()

    total_runs = int(root.get("tests",    "0") or "0")
    failures   = int(root.get("failures", "0") or "0")
    skipped    = int(root.get("skipped",  "0") or "0")
    errors     = int(root.get("errors",   "0") or "0")
    failed     = failures + errors
    passed     = total_runs - failed - skipped
    duration_s = float(root.get("time", "0") or "0")

    # Detect distinct browser names from testsuite @hostname, preserving order
    browsers: List[str] = []
    for ts in root.findall("testsuite"):
        hostname = ts.get("hostname", "").strip()
        if hostname and hostname not in browsers:
            browsers.append(hostname)

    n_browsers = max(1, len(browsers))
    unique_cases = total_runs // n_browsers if n_browsers > 1 else total_runs

    # Build display name: "E2E Playwright (chromium + firefox)"
    if browsers:
        display_name = f"{base_display_name} ({' + '.join(browsers)})"
    else:
        display_name = base_display_name

    # Collect failed test names, preserving the browser tag from the
    # enclosing testsuite.  Walk testsuite-by-testsuite so we can attach
    # the hostname to each failed testcase.
    failed_names: List[str] = []
    for ts in root.findall("testsuite"):
        browser_tag = ts.get("hostname", "").strip()
        suite_label = f"{base_display_name} ({browser_tag})" if browser_tag else base_display_name
        for tc in ts.findall("testcase"):
            if tc.find("failure") is not None or tc.find("error") is not None:
                name = tc.get("name", "<unknown>")
                classname = tc.get("classname", "")
                full_name = f"{classname} > {name}" if classname else name
                failed_names.append(f"**{suite_label}** — `{full_name}`")

    return SuiteResult(
        name=display_name,
        cases=unique_cases,
        runs=total_runs,
        passed=passed,
        failed=failed,
        skipped=skipped,
        duration_s=duration_s,
        failed_test_names=failed_names,
        browsers=browsers,
    )


# ---------------------------------------------------------------------------
# Markdown renderer
# ---------------------------------------------------------------------------

def render_markdown(suites: List[SuiteResult]) -> str:
    """Render the locked DWBHUB_TEST_SUMMARY markdown from a list of suites."""
    total_runs    = sum(s.runs for s in suites)
    total_cases   = sum(s.cases for s in suites)
    total_passed  = sum(s.passed for s in suites)
    total_failed  = sum(s.failed for s in suites)
    total_time_s  = sum(s.duration_s for s in suites)
    n_suites      = len(suites)
    sha           = get_commit_sha()
    time_str      = fmt_time(total_time_s)

    lines = ["<!-- DWBHUB_TEST_SUMMARY -->"]

    if total_failed == 0:
        # ------------------------------------------------------------------ #
        # All-green header
        # ------------------------------------------------------------------ #
        lines.append("## ✅ All Tests Passed")
        lines.append("")
        lines.append(
            f"**{total_runs} runs** in {n_suites} suites"
            f" · **{time_str}**"
            f" · commit `{sha}`"
        )
    else:
        # ------------------------------------------------------------------ #
        # Failure header
        # ------------------------------------------------------------------ #
        lines.append(f"## ❌ {total_failed} Tests Failed")
        lines.append("")
        lines.append(
            f"**{total_passed} ✅ · {total_failed} ❌**"
            f" in {total_runs} runs"
            f" · {time_str}"
            f" · commit `{sha}`"
        )

    lines.append("")

    # ---------------------------------------------------------------------- #
    # Table
    # ---------------------------------------------------------------------- #
    lines.append("| Suite | Cases | Status | Time |")
    lines.append("|---|---:|:---:|---:|")

    for s in suites:
        lines.append(
            f"| {s.name}"
            f" | {cases_cell(s)}"
            f" | {status_cell(s)}"
            f" | {fmt_time(s.duration_s)}"
            " |"
        )

    # Total row uses total_cases (sum of unique cases) for the Cases column
    # and total_runs for the bold Total label context.  Per spec, the Total
    # row Cases cell = sum of all runs (287 in the spec example), which equals
    # total_cases when there is no cross-browser deduplication, but equals
    # total_runs for pure run totals.  The spec §3.3 shows **287** in the
    # Total row (runs, not unique cases).  We follow the spec literally:
    # Total Cases = total_runs.
    total_cases_cell = f"**{total_runs}**"
    if total_failed == 0:
        total_status_cell = "**✅**"
    else:
        total_status_cell = f"**❌ {total_passed} / {total_failed} fail**"

    lines.append(
        f"| **Total**"
        f" | {total_cases_cell}"
        f" | {total_status_cell}"
        f" | **{time_str}**"
        " |"
    )

    # ---------------------------------------------------------------------- #
    # Failed-tests section
    # ---------------------------------------------------------------------- #
    if total_failed > 0:
        lines.append("")
        lines.append("### Failed tests")

        all_failed_names: List[str] = []
        for s in suites:
            if s.failed > 0:
                for name in s.failed_test_names:
                    # Playwright parser already formats with suite label;
                    # TRX / Vitest parsers return bare test names.
                    if name.startswith("**"):
                        all_failed_names.append(f"- {name}")
                    else:
                        all_failed_names.append(f"- **{s.name}** — `{name}`")

        overflow = len(all_failed_names) - MAX_FAILED_DISPLAY
        for entry in all_failed_names[:MAX_FAILED_DISPLAY]:
            lines.append(entry)
        if overflow > 0:
            lines.append(
                f"- ... and {overflow} more — see per-job check-runs"
            )

        lines.append("")
        lines.append(
            "Drill into details via the per-job check-runs in the PR Checks tab."
        )

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# File discovery
# ---------------------------------------------------------------------------

def load_suites(results_dir: Path) -> List[SuiteResult]:
    """Walk results_dir, parse known suite files, return SuiteResult list."""
    suites: List[SuiteResult] = []
    known_paths = set()

    for relpath, display_name in SUITE_MAP:
        f = results_dir / relpath
        known_paths.add(f.resolve())
        if not f.exists():
            # Suite was skipped (e.g. e2e on PR→develop); omit from table
            continue
        if relpath.endswith(".trx"):
            suites.append(parse_trx(f, display_name))
        elif "playwright" in relpath:
            suites.append(parse_playwright(f, display_name))
        elif "vitest" in relpath:
            suites.append(parse_vitest(f, display_name))

    # Surface unknown TRX files (catches new test projects added without
    # updating the mapping above — better to show Unknown: path than silence).
    for trx in sorted(results_dir.rglob("*.trx")):
        if trx.resolve() not in known_paths:
            try:
                sr = parse_trx(trx, f"Unknown: {trx.relative_to(results_dir)}")
                suites.append(sr)
            except Exception as exc:
                print(f"warning: could not parse {trx}: {exc}", file=sys.stderr)

    return suites


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main() -> None:
    if len(sys.argv) != 2:
        print(
            "usage: render-test-summary.py <results-dir>",
            file=sys.stderr,
        )
        sys.exit(2)

    results_dir = Path(sys.argv[1])
    if not results_dir.is_dir():
        print(f"results-dir not found: {results_dir}", file=sys.stderr)
        sys.exit(2)

    suites = load_suites(results_dir)
    markdown = render_markdown(suites)
    print(markdown)

    total_failed = sum(s.failed for s in suites)
    sys.exit(1 if total_failed > 0 else 0)


if __name__ == "__main__":
    main()
