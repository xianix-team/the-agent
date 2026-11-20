#!/usr/bin/env python3
"""Unit tests for execute_plugin.result_error — the Claude SDK is stubbed, so it need not be installed."""
from __future__ import annotations

import sys
import types
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

if "claude_agent_sdk" not in sys.modules:
    _sdk = types.ModuleType("claude_agent_sdk")
    for _name in ("ClaudeAgentOptions", "AssistantMessage", "UserMessage", "SystemMessage", "ResultMessage"):
        setattr(_sdk, _name, type(_name, (), {}))
    _sdk.query = None
    sys.modules["claude_agent_sdk"] = _sdk

from execute_plugin import result_error  # noqa: E402


def _result(**fields) -> types.SimpleNamespace:
    return types.SimpleNamespace(**fields)


class ResultErrorTests(unittest.TestCase):
    def test_no_result_message_is_not_an_error(self) -> None:
        self.assertIsNone(result_error(None))

    def test_successful_run_is_not_an_error(self) -> None:
        self.assertIsNone(result_error(_result(is_error=False, subtype="success", result="done")))

    def test_sdk_error_names_subtype_and_detail(self) -> None:
        error = result_error(_result(is_error=True, subtype="error_max_turns", result="Reached max turns"))

        self.assertEqual(error, "Claude Code reported the run failed (error_max_turns): Reached max turns")

    def test_sdk_error_without_detail(self) -> None:
        error = result_error(_result(is_error=True, subtype="error_during_execution", result=None))

        self.assertEqual(error, "Claude Code reported the run failed (error_during_execution).")


if __name__ == "__main__":
    unittest.main()
