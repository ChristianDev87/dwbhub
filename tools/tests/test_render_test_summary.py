#!/usr/bin/env python3
"""Self-tests for tools/render-test-summary.py using synthetic fixture files."""
import subprocess
import sys
import unittest
from pathlib import Path

SCRIPT   = Path(__file__).parent.parent / "render-test-summary.py"
FIXTURES = Path(__file__).parent / "fixtures"


def run_script(fixture: str) -> tuple:
    """Run the renderer against a fixture directory and return (exit_code, stdout)."""
    result = subprocess.run(
        [sys.executable, str(SCRIPT), str(FIXTURES / fixture)],
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    return result.returncode, result.stdout


class TestAllPass(unittest.TestCase):
    """all-pass fixture: 6 suites, everything green."""

    @classmethod
    def setUpClass(cls):
        cls.code, cls.out = run_script("all-pass")

    def test_exit_code_zero(self):
        self.assertEqual(self.code, 0, msg=f"Expected exit 0, got {self.code}\n{self.out}")

    def test_marker_present(self):
        self.assertIn("<!-- DWBHUB_TEST_SUMMARY -->", self.out)

    def test_all_passed_heading(self):
        self.assertIn("## ✅ All Tests Passed", self.out)

    def test_playwright_cross_browser_name(self):
        self.assertIn("E2E Playwright (chromium + firefox)", self.out)

    def test_playwright_multiplier_format(self):
        self.assertIn("× 2 =", self.out)

    def test_backend_unit_row_present(self):
        self.assertIn("Backend Unit", self.out)

    def test_backend_integration_row_present(self):
        self.assertIn("Backend Integration", self.out)

    def test_backend_security_row_present(self):
        self.assertIn("Backend Security", self.out)

    def test_frontend_vitest_row_present(self):
        self.assertIn("Frontend Vitest", self.out)

    def test_discord_live_row_present(self):
        self.assertIn("Discord Live", self.out)

    def test_no_failed_tests_section(self):
        self.assertNotIn("### Failed tests", self.out)

    def test_total_row_present(self):
        self.assertIn("**Total**", self.out)

    def test_table_header(self):
        self.assertIn("| Suite | Cases | Status | Time |", self.out)


class TestTwoFailed(unittest.TestCase):
    """two-failed fixture: one TRX with a failed test, one Playwright with a failure."""

    @classmethod
    def setUpClass(cls):
        cls.code, cls.out = run_script("two-failed")

    def test_exit_code_one(self):
        self.assertEqual(self.code, 1, msg=f"Expected exit 1, got {self.code}\n{self.out}")

    def test_marker_present(self):
        self.assertIn("<!-- DWBHUB_TEST_SUMMARY -->", self.out)

    def test_failure_heading(self):
        self.assertIn("## ❌", self.out)

    def test_failed_tests_section_present(self):
        self.assertIn("### Failed tests", self.out)

    def test_failed_trx_test_name_listed(self):
        self.assertIn("Suite.Test2", self.out)

    def test_failed_playwright_test_listed(self):
        self.assertIn("broken test", self.out)

    def test_failed_playwright_browser_tag(self):
        # The chromium browser tag should appear in the failed-tests section
        self.assertIn("chromium", self.out)

    def test_drill_down_hint(self):
        self.assertIn("Drill into details", self.out)


class TestSkippedSuiteOmitted(unittest.TestCase):
    """backend-only fixture: only backend/unit.trx, no e2e or discord-live."""

    @classmethod
    def setUpClass(cls):
        cls.code, cls.out = run_script("backend-only")

    def test_exit_code_zero(self):
        self.assertEqual(self.code, 0, msg=f"Expected exit 0, got {self.code}\n{self.out}")

    def test_backend_unit_present(self):
        self.assertIn("Backend Unit", self.out)

    def test_e2e_row_absent(self):
        self.assertNotIn("E2E Playwright", self.out)

    def test_discord_live_row_absent(self):
        self.assertNotIn("Discord Live", self.out)

    def test_no_zero_cases_rows(self):
        # Skipped suites must not appear as empty rows
        lines = self.out.splitlines()
        for line in lines:
            if "| " in line and " | 0 |" in line:
                self.fail(f"Found a zero-cases row: {line!r}")


if __name__ == "__main__":
    unittest.main(verbosity=2)
