#!/usr/bin/env python3
"""Sync plugins-official handoff tree from test fixtures.

Source: TheAgent.Tests/Fixtures/agent-setup/<name>/agent-setup.json
Output: tools/generated-agent-setup/plugins/<name>/.xianix/agent-setup.json

Runtime the-agent never reads these — live plugins-official URLs only.

Usage:
  python tools/generate-agent-setup.py
"""

from __future__ import annotations

import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "TheAgent.Tests" / "Fixtures" / "agent-setup"
HANDOFF = ROOT / "tools" / "generated-agent-setup" / "plugins"


def main() -> None:
    if not FIXTURES.is_dir():
        raise SystemExit(f"Missing fixture tree: {FIXTURES}")

    if HANDOFF.exists():
        shutil.rmtree(HANDOFF)

    count = 0
    for setup_path in sorted(FIXTURES.glob("*/agent-setup.json")):
        name = setup_path.parent.name
        json.loads(setup_path.read_text(encoding="utf-8"))
        dest = HANDOFF / name / ".xianix" / "agent-setup.json"
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(setup_path, dest)
        count += 1
        print(f"synced {name}")

    print(f"synced {count} plugins -> {HANDOFF}")


if __name__ == "__main__":
    main()
