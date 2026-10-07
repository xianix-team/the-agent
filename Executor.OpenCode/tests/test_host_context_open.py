#!/usr/bin/env python3
"""Unit tests for Executor.OpenCode host_context.py."""
from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from host_context import (  # noqa: E402
    HOST_CONTEXT_MARKER,
    RECIPE_CONTEXT_MARKER,
    build_host_context_block,
    parse_inputs,
    prepend_host_context,
)


class ParseInputsTests(unittest.TestCase):
    def test_valid_dict(self) -> None:
        self.assertEqual(parse_inputs('{"platform":"github"}'), {"platform": "github"})

    def test_invalid_or_empty(self) -> None:
        self.assertEqual(parse_inputs(None), {})
        self.assertEqual(parse_inputs(""), {})
        self.assertEqual(parse_inputs("not-json"), {})
        self.assertEqual(parse_inputs("[1,2]"), {})


class BuildHostContextTests(unittest.TestCase):
    def test_github_hint(self) -> None:
        block = build_host_context_block("github", "owner/repo")
        self.assertTrue(block.startswith(HOST_CONTEXT_MARKER))
        self.assertIn("platform: github", block)
        self.assertIn("`gh` CLI", block)
        self.assertIn("repository-name: owner/repo", block)
        self.assertNotIn("Azure DevOps", block)

    def test_azuredevops_hint(self) -> None:
        block = build_host_context_block("azuredevops")
        self.assertIn("Azure DevOps REST API", block)
        self.assertNotIn("`gh` CLI", block)


class PrependHostContextTests(unittest.TestCase):
    def setUp(self) -> None:
        for key in (
            "XIANIX_LEAD_COMMAND_PATH",
            "XIANIX_MATCHED_COMMAND",
            "XIANIX_PLUGIN_ROOT",
            "CLAUDE_PLUGIN_ROOT",
            "XIANIX_PLUGIN_ID",
        ):
            os.environ.pop(key, None)

    def test_prepends_platform_block(self) -> None:
        result = prepend_host_context(
            "Review PR #4",
            json.dumps({"platform": "github", "repository-name": "a/b"}),
        )
        self.assertTrue(result.startswith(HOST_CONTEXT_MARKER))
        self.assertTrue(result.endswith("Review PR #4"))

    def test_no_platform_leaves_prompt(self) -> None:
        self.assertEqual(prepend_host_context("Just do it.", "{}"), "Just do it.")

    def test_injects_recipe_when_lead_file_exists(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            lead = Path(tmp) / "pr-review.md"
            lead.write_text("# recipe\n", encoding="utf-8")
            os.environ["XIANIX_LEAD_COMMAND_PATH"] = str(lead)
            os.environ["XIANIX_MATCHED_COMMAND"] = "pr-review"
            os.environ["XIANIX_PLUGIN_ID"] = "pr-reviewer"
            os.environ["XIANIX_PLUGIN_ROOT"] = tmp
            result = prepend_host_context(
                "Run /pr-review 4",
                json.dumps({"platform": "github"}),
            )
            self.assertIn(RECIPE_CONTEXT_MARKER, result)
            self.assertIn(str(lead), result)
            self.assertIn("/pr-review", result)
            self.assertIn(HOST_CONTEXT_MARKER, result)

    def test_skips_recipe_without_matched_command(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            lead = Path(tmp) / "pr-review.md"
            lead.write_text("# recipe\n", encoding="utf-8")
            os.environ["XIANIX_LEAD_COMMAND_PATH"] = str(lead)
            os.environ.pop("XIANIX_MATCHED_COMMAND", None)
            os.environ["XIANIX_PLUGIN_ID"] = "pr-reviewer"
            result = prepend_host_context(
                "Please reply to the comment.",
                json.dumps({"platform": "github"}),
            )
            self.assertNotIn(RECIPE_CONTEXT_MARKER, result)
            self.assertIn(HOST_CONTEXT_MARKER, result)

if __name__ == "__main__":
    unittest.main()
