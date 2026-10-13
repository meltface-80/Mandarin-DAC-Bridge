"""Tests for tools/check.sh itself: python3 -m unittest discover -s tools/quality"""
import os
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASH = "/bin/bash"


@unittest.skipUnless(os.path.exists(BASH), "needs bash at /bin/bash")
class EveryWayItStopsSaysFail(unittest.TestCase):
    def run_check(self, env):
        return subprocess.run([BASH, str(ROOT / "tools" / "check.sh"), "gates"], cwd=ROOT, env=env, capture_output=True, text=True)

    def test_without_its_tools_it_says_what_is_missing(self):
        out = self.run_check({"PATH": "/nonexistent", "HOME": "/nonexistent"})
        self.assertNotEqual(out.returncode, 0)
        self.assertIn("[FAIL]", out.stdout)
        self.assertIn("git isn't installed", out.stdout)

    def test_a_baseline_it_cant_check_against_fails(self):
        env = dict(os.environ, BASELINE_FROM="no-such-revision")
        out = self.run_check(env)
        self.assertNotEqual(out.returncode, 0)
        self.assertIn("[FAIL] file length", out.stdout)
        self.assertIn("can't be read", out.stdout)


if __name__ == "__main__":
    unittest.main()
