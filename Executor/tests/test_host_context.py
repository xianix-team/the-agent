#!/usr/bin/env python3
"""Unit tests for Executor/host_context.py — no Docker / Claude SDK required."""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from host_context import (  # noqa: E402
    HOST_CONTEXT_MARKER,
    prepend_host_context,
)


class PrependHostContextTests(unittest.TestCase):
    def test_prepends_when_platform_set(self) -> None:
        inputs = json.dumps({
            "platform": "azuredevops",
            "repository-name": "HasithY/Xianix-tests/XiansAi.Server",
        })
        original = "You are @xianix. Review this PR."
        result = prepend_host_context(original, inputs)

        self.assertTrue(result.startswith(HOST_CONTEXT_MARKER))
        self.assertIn("platform: azuredevops", result)
        self.assertIn("repository-name: HasithY/Xianix-tests/XiansAi.Server", result)
        self.assertIn("Azure DevOps REST", result)
        self.assertTrue(result.endswith(original))
        self.assertIn("\n---\n\n" + original, result)

    def test_platform_only_omits_repository_name_line(self) -> None:
        inputs = json.dumps({"platform": "github"})
        result = prepend_host_context("Run /pr-review 1", inputs)

        self.assertIn("platform: github", result)
        self.assertNotIn("repository-name:", result)
        self.assertTrue(result.endswith("Run /pr-review 1"))

    def test_no_platform_leaves_prompt_unchanged(self) -> None:
        original = "Just summarize the repo."
        self.assertEqual(prepend_host_context(original, "{}"), original)
        self.assertEqual(prepend_host_context(original, None), original)
        self.assertEqual(
            prepend_host_context(original, json.dumps({"repository-name": "a/b"})),
            original,
        )

    def test_empty_platform_leaves_prompt_unchanged(self) -> None:
        original = "Do something."
        self.assertEqual(
            prepend_host_context(original, json.dumps({"platform": "  "})),
            original,
        )

    def test_idempotent_when_marker_already_present(self) -> None:
        inputs = json.dumps({"platform": "azuredevops"})
        once = prepend_host_context("Review PR #18", inputs)
        twice = prepend_host_context(once, inputs)
        self.assertEqual(once, twice)
        self.assertEqual(twice.count(HOST_CONTEXT_MARKER), 1)

    def test_invalid_inputs_json_leaves_prompt_unchanged(self) -> None:
        original = "Hello"
        self.assertEqual(prepend_host_context(original, "not-json"), original)


if __name__ == "__main__":
    unittest.main(verbosity=2)
