#!/usr/bin/env python3
"""File-length ratchet (dev/quality.md, "File length").

Every tracked code file is measured like `wc -l`. Above FAIL lines the check fails, unless the file is in
length-baseline.json: files that were already too long when the limit came in. Those may not grow, and the
baseline only ever tightens: an entry that's out of date (the file shrank, or is gone) also fails, until
`--ratchet` records it.

    python3 tools/quality/length.py                       check (exit 1 on a failure)
    python3 tools/quality/length.py --baseline-from REV   also check the baseline against REV's
    python3 tools/quality/length.py --ratchet             record files that shrank or went

Never raise or add a baseline entry to get a change through: split the file. --baseline-from catches it
(tools/check.sh passes HEAD, CI the commit before): an entry that is new or larger than at REV fails.
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


def raised(baseline: dict, before: dict) -> list:
    """Failures for baseline entries that are new, or larger than they were (before): raised by hand."""
    failures = []
    for name, n in sorted(baseline.items()):
        was = before.get(name)
        if was is None:
            failures.append(f"{name}: added to the baseline ({n}). Entries are never added: split the file.")
        elif n > was:
            failures.append(f"{name}: raised in the baseline from {was} to {n}. Entries only shrink: split the file.")
    return failures


def baseline_at(rev: str, root: Path = ROOT):
    """The baseline as it was at a git revision, or None when git can't show it."""
    out = subprocess.run(["git", "show", f"{rev}:{BASELINE.relative_to(root).as_posix()}"], cwd=root, capture_output=True, text=True)
    return json.loads(out.stdout) if out.returncode == 0 else None


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
    if "--baseline-from" in argv:
        i = argv.index("--baseline-from")
        rev = argv[i + 1] if i + 1 < len(argv) else ""
        before = baseline_at(rev) if rev else None
        if before is None:
            failures.append(f"the baseline at {rev or '(no revision given)'} can't be read, so it can't be checked against it")
        else:
            failures += raised(baseline, before)
    for r in reports:
        print("  " + r)
    for f in failures:
        print("FAIL " + f)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
