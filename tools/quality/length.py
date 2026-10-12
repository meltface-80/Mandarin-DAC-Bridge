#!/usr/bin/env python3
"""File-length ratchet (dev/quality.md, "File length").

Every tracked code file is measured like `wc -l`. Above FAIL lines the check fails, unless the file is in
length-baseline.json: files that were already too long when the limit came in. Those may not grow, and the
baseline only ever tightens: an entry that's out of date (the file shrank, or is gone) also fails, until
`--ratchet` records it.

    python3 tools/quality/length.py             check (exit 1 on a failure)
    python3 tools/quality/length.py --ratchet   record files that shrank or went

Never raise or add a baseline entry to get a change through: split the file.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

LIMITS = {"fail": 500, "warn": 450, "report": 400}
CODE = re.compile(r"\.(cs|js|html|css|sh|ps1|py|c|h)$")
# Never part of the program or its tests: the GitHub Pages site.
IGNORED = re.compile(r"^docs/")


def measured(name: str) -> bool:
    """Whether the gate counts this tracked file."""
    return bool(CODE.search(name)) and not IGNORED.search(name)
ROOT = Path(__file__).resolve().parents[2]
BASELINE = Path(__file__).with_name("length-baseline.json")


def lines(text: str) -> int:
    """Lines as `wc -l` counts them: newlines."""
    return text.count("\n")


def judge(counts: dict, baseline: dict, limits: dict = LIMITS) -> tuple:
    """(failures, reports) for these line counts against the baseline."""
    failures, reports = [], []
    for name in sorted(counts):
        n, allowed = counts[name], baseline.get(name)
        if allowed is None:
            if n > limits["fail"]:
                failures.append(f"{name}: {n} lines, over the {limits['fail']}-line limit. Split it.")
            elif n > limits["report"]:
                reports.append(f"{name}: {n} lines")
        elif n > allowed:
            failures.append(f"{name}: {n} lines, up from {allowed}. It's already over the limit: split it before adding.")
        elif n < allowed:
            failures.append(f"{name}: {n} lines, down from {allowed}. Good: run `python3 tools/quality/length.py --ratchet` to record it.")
        else:
            reports.append(f"{name}: {n} lines (over the limit; may not grow)")
    for name in sorted(baseline):
        if name not in counts:
            failures.append(f"{name}: gone, but still in the baseline. Run `python3 tools/quality/length.py --ratchet`.")
    return failures, reports


def ratchet(counts: dict, baseline: dict, limits: dict = LIMITS) -> dict:
    """The baseline brought up to date: entries only shrink or disappear, never grow or appear."""
    return {name: min(counts[name], allowed) for name, allowed in sorted(baseline.items())
            if name in counts and counts[name] > limits["fail"]}


def tracked(root: Path = ROOT) -> dict:
    """Line counts of the code files git tracks."""
    out = subprocess.run(["git", "ls-files"], cwd=root, capture_output=True, text=True, check=True).stdout
    counts = {}
    for name in out.splitlines():
        p = root / name
        if measured(name) and p.is_file():
            counts[name] = lines(p.read_text(encoding="utf-8", errors="replace"))
    return counts


def main(argv: list) -> int:
    baseline = json.loads(BASELINE.read_text()) if BASELINE.exists() else {}
    counts = tracked()
    if "--ratchet" in argv:
        BASELINE.write_text(json.dumps(ratchet(counts, baseline), indent=2, sort_keys=True) + "\n")
        print("length-baseline.json brought up to date.")
        return 0
    failures, reports = judge(counts, baseline)
    for r in reports:
        print("  " + r)
    for f in failures:
        print("FAIL " + f)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
