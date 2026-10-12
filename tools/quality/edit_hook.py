#!/usr/bin/env python3
"""Claude Code PreToolUse hook (.claude/settings.json): the file-length tripwire at edit time, so an agent
learns a file needs splitting at its first edit, not at commit. A note past WARN lines; a refusal when an
edit would take a file past FAIL (or a baselined file past its recorded length). An edit that shrinks a file
is never refused, so an oversized file can always be split. Anything unexpected is silent: the commit-time
check (length.py, in pre-commit and CI) is the backstop, and a hook must never block an edit it can't judge.
"""
import json
import sys
from pathlib import Path

from length import BASELINE, CODE, LIMITS, ROOT, lines


def project(tool: str, before: str, inp: dict):
    """The file's text after this edit, or None if it can't be told."""
    if tool == "Write":
        return inp.get("content")
    edits = inp.get("edits") if tool == "MultiEdit" else [inp] if tool == "Edit" else None
    if edits is None:
        return None
    text = before
    for e in edits:
        old, new = e.get("old_string"), e.get("new_string")
        if old is None or new is None:
            return None
        text = text.replace(old, new) if e.get("replace_all") else text.replace(old, new, 1)
    return text


def decide(payload: dict, read, baseline: dict, limits: dict = LIMITS):
    """The hook's answer for this payload, or None to stay silent."""
    tool, inp = payload.get("tool_name"), payload.get("tool_input") or {}
    path = inp.get("file_path") or inp.get("path")
    if not tool or not path:
        return None
    try:
        name = Path(path).resolve().relative_to(ROOT).as_posix() if Path(path).is_absolute() else Path(path).as_posix()
    except ValueError:
        return None
    if not CODE.search(name):
        return None
    try:
        before = read(name)
    except OSError:
        before = ""
    after = project(tool, before, inp)
    if after is None:
        return None
    n_before, n_after = lines(before), lines(after)
    if n_after <= n_before and n_after > limits["fail"]:
        return None
    allowed = baseline.get(name, limits["fail"])
    if n_after > allowed:
        why = (f"{name} would be {n_after} lines, past its recorded {allowed}: it's already over the limit, split it before adding."
               if name in baseline else f"{name} would be {n_after} lines, over the {limits['fail']}-line limit. Split it first.")
        return {"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny", "permissionDecisionReason": why}}
    if n_after > limits["warn"] and n_after > n_before:
        return {"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                       "additionalContext": f"{name} is now {n_after} lines (the limit is {limits['fail']}): plan to split it."}}
    return None


def main() -> None:
    try:
        payload = json.loads(sys.stdin.read())
        baseline = json.loads(BASELINE.read_text()) if BASELINE.exists() else {}
        out = decide(payload, lambda name: (ROOT / name).read_text(encoding="utf-8", errors="replace"), baseline)
        if out:
            sys.stdout.write(json.dumps(out))
    except Exception:  # silent by design (see above)
        pass


if __name__ == "__main__":
    main()
