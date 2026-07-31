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
    plugin_command_lines,
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

    def test_github_hint_is_inline_and_platform_specific(self) -> None:
        result = prepend_host_context("Do it.", json.dumps({"platform": "github"}))

        self.assertIn("platform: github — for PR/issue/comment API calls use the `gh` CLI", result)
        # The other platform's tooling must not leak into a GitHub run.
        self.assertNotIn("Azure DevOps", result)

    def test_azuredevops_hint_is_inline_and_platform_specific(self) -> None:
        result = prepend_host_context("Do it.", json.dumps({"platform": "azuredevops"}))

        self.assertIn("platform: azuredevops — for PR/issue/comment API calls", result)
        self.assertIn("Azure DevOps REST API", result)
        self.assertNotIn("gh` CLI", result)

    def test_unknown_platform_uses_generic_hint(self) -> None:
        result = prepend_host_context("Do it.", json.dumps({"platform": "gitlab"}))

        self.assertIn("platform: gitlab — for PR/issue/comment API calls use this platform's", result)

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


class PluginCommandTests(unittest.TestCase):
    def test_lists_plugin_commands_alongside_platform(self) -> None:
        inputs = json.dumps({"platform": "github"})
        plugins = [
            {"plugin-name": "pr-reviewer@xianix-plugins-official", "slash-command": "/pr-review"},
        ]
        result = prepend_host_context("Review this PR.", inputs, plugins)

        self.assertTrue(result.startswith(HOST_CONTEXT_MARKER))
        self.assertIn("platform: github", result)
        self.assertIn("Available plugin commands", result)
        self.assertIn("- /pr-review — pr-reviewer@xianix-plugins-official", result)
        self.assertTrue(result.endswith("Review this PR."))

    def test_lists_plugin_commands_without_platform(self) -> None:
        plugins = [{"plugin-name": "pr-reviewer", "slash-command": "/pr-review"}]
        result = prepend_host_context("Do the review.", "{}", plugins)

        self.assertTrue(result.startswith(HOST_CONTEXT_MARKER))
        self.assertNotIn("platform:", result)
        self.assertIn("- /pr-review — pr-reviewer", result)
        self.assertTrue(result.endswith("Do the review."))

    def test_plugins_without_slash_command_leave_prompt_unchanged(self) -> None:
        original = "Just summarize the repo."
        plugins = [{"plugin-name": "pr-reviewer", "marketplace": "mp"}]
        self.assertEqual(prepend_host_context(original, "{}", plugins), original)

    def test_deduplicates_repeated_commands(self) -> None:
        plugins = [
            {"plugin-name": "pr-reviewer", "slash-command": "/pr-review"},
            {"plugin-name": "pr-reviewer", "slash-command": "/pr-review"},
        ]
        self.assertEqual(plugin_command_lines(plugins), ["- /pr-review — pr-reviewer"])

    def test_command_without_plugin_name_renders_bare(self) -> None:
        self.assertEqual(
            plugin_command_lines([{"slash-command": "/pr-review"}]),
            ["- /pr-review"],
        )

    def test_ignores_non_dict_and_blank_entries(self) -> None:
        plugins = ["nope", {"slash-command": "  "}, {"plugin-name": "x"}]
        self.assertEqual(plugin_command_lines(plugins), [])

    def test_idempotent_with_plugins(self) -> None:
        plugins = [{"plugin-name": "pr-reviewer", "slash-command": "/pr-review"}]
        once = prepend_host_context("Review PR #18", "{}", plugins)
        twice = prepend_host_context(once, "{}", plugins)
        self.assertEqual(once, twice)
        self.assertEqual(twice.count(HOST_CONTEXT_MARKER), 1)


class ProvisionedRuntimesTests(unittest.TestCase):
    def test_runtimes_line_rendered_alongside_platform(self) -> None:
        inputs = json.dumps({"platform": "github"})
        result = prepend_host_context(
            "Write unit tests.", inputs, runtimes="dotnet 9.0; node 22.11.0")

        self.assertTrue(result.startswith(HOST_CONTEXT_MARKER))
        self.assertIn("platform: github", result)
        self.assertIn(
            "provisioned runtimes (already installed and on PATH): dotnet 9.0; node 22.11.0",
            result,
        )
        self.assertTrue(result.endswith("Write unit tests."))

    def test_runtimes_alone_still_prepend_block(self) -> None:
        result = prepend_host_context("Build it.", "{}", runtimes="dotnet 9.0")

        self.assertTrue(result.startswith(HOST_CONTEXT_MARKER))
        self.assertNotIn("platform:", result)
        self.assertIn("provisioned runtimes", result)
        self.assertTrue(result.endswith("Build it."))

    def test_blank_runtimes_do_not_trigger_block(self) -> None:
        original = "Summarize the repo."
        self.assertEqual(prepend_host_context(original, "{}", runtimes="  "), original)
        self.assertEqual(prepend_host_context(original, "{}", runtimes=None), original)

    def test_idempotent_with_runtimes(self) -> None:
        once = prepend_host_context("Review PR #18", "{}", runtimes="dotnet 9.0")
        twice = prepend_host_context(once, "{}", runtimes="dotnet 9.0")
        self.assertEqual(once, twice)
        self.assertEqual(twice.count(HOST_CONTEXT_MARKER), 1)

    def test_runtimes_precede_plugin_commands_with_separator(self) -> None:
        plugins = [{"plugin-name": "unit-tester", "slash-command": "/write-tests"}]
        result = prepend_host_context(
            "Go.", "{}", plugins, runtimes="dotnet 9.0")

        runtimes_pos = result.index("provisioned runtimes")
        commands_pos = result.index("Available plugin commands")
        self.assertLess(runtimes_pos, commands_pos)


if __name__ == "__main__":
    unittest.main(verbosity=2)
