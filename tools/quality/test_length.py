"""Tests for the file-length tripwire: python3 -m unittest discover -s tools/quality"""
import unittest

from length import judge, lines, ratchet
from edit_hook import decide

LIMITS = {"fail": 10, "warn": 8, "report": 5}


class Judge(unittest.TestCase):
    def test_a_new_file_over_the_limit_fails(self):
        failures, _ = judge({"a.cs": 11}, {}, LIMITS)
        self.assertEqual(len(failures), 1)
        self.assertIn("over the 10-line limit", failures[0])

    def test_a_file_at_the_limit_passes_and_is_reported(self):
        failures, reports = judge({"a.cs": 10}, {}, LIMITS)
        self.assertEqual(failures, [])
        self.assertEqual(reports, ["a.cs: 10 lines"])

    def test_a_baselined_file_may_not_grow(self):
        failures, _ = judge({"big.cs": 21}, {"big.cs": 20}, LIMITS)
        self.assertIn("up from 20", failures[0])

    def test_a_baselined_file_that_shrank_fails_until_recorded(self):
        failures, _ = judge({"big.cs": 19}, {"big.cs": 20}, LIMITS)
        self.assertIn("--ratchet", failures[0])

    def test_a_baselined_file_that_is_gone_fails_until_recorded(self):
        failures, _ = judge({}, {"big.cs": 20}, LIMITS)
        self.assertIn("gone", failures[0])

    def test_a_baselined_file_unchanged_passes(self):
        failures, reports = judge({"big.cs": 20}, {"big.cs": 20}, LIMITS)
        self.assertEqual(failures, [])
        self.assertIn("may not grow", reports[0])


class Ratchet(unittest.TestCase):
    def test_it_only_ever_tightens(self):
        counts = {"shrank.cs": 15, "grew.cs": 30, "now-short.cs": 9, "new-big.cs": 50}
        baseline = {"shrank.cs": 20, "grew.cs": 25, "now-short.cs": 20, "gone.cs": 20}
        self.assertEqual(ratchet(counts, baseline, LIMITS), {"shrank.cs": 15, "grew.cs": 25})


class EditHook(unittest.TestCase):
    def payload(self, before, after, name="src/A.cs"):
        return {"tool_name": "Write", "tool_input": {"file_path": name, "content": after}}, before

    def test_an_edit_that_crosses_the_limit_is_refused(self):
        p, before = self.payload("x\n" * 9, "x\n" * 11)
        out = decide(p, lambda _: before, {}, LIMITS)
        self.assertEqual(out["hookSpecificOutput"]["permissionDecision"], "deny")

    def test_an_edit_past_the_warning_line_gets_a_note(self):
        p, before = self.payload("x\n" * 3, "x\n" * 9)
        out = decide(p, lambda _: before, {}, LIMITS)
        self.assertIn("additionalContext", out["hookSpecificOutput"])

    def test_an_edit_that_shrinks_an_oversized_file_is_never_refused(self):
        p, before = self.payload("x\n" * 30, "x\n" * 25)
        self.assertIsNone(decide(p, lambda _: before, {"src/A.cs": 30}, LIMITS))

    def test_a_baselined_file_may_not_grow(self):
        p, before = self.payload("x\n" * 30, "x\n" * 31)
        out = decide(p, lambda _: before, {"src/A.cs": 30}, LIMITS)
        self.assertEqual(out["hookSpecificOutput"]["permissionDecision"], "deny")

    def test_an_edit_is_projected_from_old_and_new_strings(self):
        p = {"tool_name": "Edit", "tool_input": {"file_path": "src/A.cs", "old_string": "a\n", "new_string": "a\n" * 12}}
        out = decide(p, lambda _: "a\nb\n", {}, LIMITS)
        self.assertEqual(out["hookSpecificOutput"]["permissionDecision"], "deny")

    def test_files_that_arent_code_are_left_alone(self):
        p, before = self.payload("x\n", "x\n" * 50, name="README.md")
        self.assertIsNone(decide(p, lambda _: before, {}, LIMITS))

    def test_counts_like_wc(self):
        self.assertEqual(lines("a\nb\n"), 2)
        self.assertEqual(lines("a\nb"), 1)


if __name__ == "__main__":
    unittest.main()
