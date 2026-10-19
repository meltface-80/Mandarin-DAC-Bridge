"""Tests for tools/ffmpeg/build.sh's pins (dev/design/install.md): python3 -m unittest discover -s tools/quality

The bridge's own ffmpeg ships in every release, so what it is built from must be exactly what was
pinned: a tarball whose SHA-256 differs is refused before anything is compiled."""
import os
import platform
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BASH = "/bin/bash"
LOCK = ROOT / "tools" / "ffmpeg" / "sources.lock"


def pins():
    lines = [l.split() for l in LOCK.read_text().splitlines() if l.strip() and not l.startswith("#")]
    return {l[0]: l for l in lines}


def this_rid():
    arch = {"x86_64": "x64", "amd64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    system = {"Linux": "linux", "Darwin": "osx"}.get(platform.system())
    return f"{system}-{arch}" if arch and system else None


class EverySourceIsPinned(unittest.TestCase):
    def test_ffmpeg_and_libsoxr_each_have_a_version_an_https_url_and_a_sha256(self):
        p = pins()
        self.assertEqual(set(p), {"ffmpeg", "soxr"})
        for name, line in p.items():
            self.assertEqual(len(line), 4, name)
            self.assertTrue(line[2].startswith("https://"), name)
            self.assertIn(line[1], line[2], name)
            self.assertRegex(line[3], r"^[0-9a-f]{64}$", name)


@unittest.skipUnless(os.path.exists(BASH) and this_rid() and shutil.which("make") and shutil.which("curl"),
                     "needs bash, make and curl on Linux or macOS")
class WhatIsntPinnedIsntBuilt(unittest.TestCase):
    def run_build(self, rid, work, out):
        env = dict(os.environ, WORK=str(work))
        return subprocess.run([BASH, str(ROOT / "tools" / "ffmpeg" / "build.sh"), rid, str(out)],
                              cwd=ROOT, env=env, capture_output=True, text=True, timeout=120)

    def test_a_tarball_that_isnt_the_pinned_one_is_refused_and_thrown_away(self):
        with tempfile.TemporaryDirectory() as d:
            work, out = Path(d) / "work", Path(d) / "out"
            work.mkdir()
            url = pins()["ffmpeg"][2]
            bad = work / url.rsplit("/", 1)[1]
            bad.write_bytes(b"not ffmpeg's source")
            r = self.run_build(this_rid(), work, out)
            self.assertNotEqual(r.returncode, 0)
            self.assertIn("[FAIL]", r.stderr)
            self.assertIn("not the pinned " + pins()["ffmpeg"][3], r.stderr)
            self.assertFalse(bad.exists(), "the file that didn't match is kept")
            self.assertFalse((out / "bin" / "ffmpeg").exists())
            self.assertFalse(list(work.glob("soxr-*")), "something was built from it")

    def test_a_build_it_doesnt_make_is_refused(self):
        with tempfile.TemporaryDirectory() as d:
            r = self.run_build("plan9-mips", Path(d) / "w", Path(d) / "o")
            self.assertNotEqual(r.returncode, 0)
            self.assertIn("not a build this script makes", r.stderr)


if __name__ == "__main__":
    unittest.main()
