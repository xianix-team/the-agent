#!/usr/bin/env python3
"""Generate per-plugin .xianix/agent-setup.json from legacy recipe + setup catalogs.

Outputs:
  tools/generated-agent-setup/plugins/<name>/.xianix/agent-setup.json  (plugins-official handoff)
  TheAgent/Knowledge/agent-setup/<name>/agent-setup.json               (embedded offline fallback)

Usage:
  python tools/generate-agent-setup.py
"""

from __future__ import annotations

import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RECIPES = ROOT / "tools" / "legacy-catalogs" / "plugin-execution-recipes.json"
SETUP = ROOT / "tools" / "legacy-catalogs" / "plugin-setup-requirements.json"
HANDOFF = ROOT / "tools" / "generated-agent-setup" / "plugins"
EMBEDDED = ROOT / "TheAgent" / "Knowledge" / "agent-setup"


def merge_platform(recipe_plat: dict | None, setup_plat: dict | None) -> dict:
    recipe_plat = recipe_plat or {}
    setup_plat = setup_plat or {}
    out: dict = {}
    # Prefer setup-requirements for secrets/triggers/events; keep recipe executions.
    for key in ("requiredEnvs", "suggestedGitHubWebhookEvents", "suggestedTriggers"):
        if key in setup_plat and setup_plat[key]:
            out[key] = setup_plat[key]
        elif key in recipe_plat and recipe_plat[key]:
            out[key] = recipe_plat[key]
    if "notes" in setup_plat and setup_plat["notes"]:
        out["notes"] = setup_plat["notes"]
    if "executions" in recipe_plat:
        out["executions"] = recipe_plat["executions"]
    return out


def build_agent_setup(name: str, recipe: dict, setup: dict | None) -> dict:
    setup = setup or {}
    platforms_out: dict[str, dict] = {}
    recipe_platforms = recipe.get("platforms") or {}
    setup_platforms = setup.get("platforms") or {}
    for plat in sorted(set(recipe_platforms) | set(setup_platforms)):
        platforms_out[plat] = merge_platform(
            recipe_platforms.get(plat),
            setup_platforms.get(plat),
        )

    doc: dict = {
        "schemaVersion": 1,
        "plugin": name,
        "slashCommand": recipe.get("slashCommand") or "",
        "platforms": platforms_out,
    }
    if recipe.get("chat"):
        doc["chat"] = recipe["chat"]
    if setup.get("requiresAuthorization"):
        doc["requiresAuthorization"] = True
    return doc


def write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    if not RECIPES.is_file():
        raise SystemExit(f"Missing recipes input: {RECIPES}")
    if not SETUP.is_file():
        raise SystemExit(f"Missing setup input: {SETUP}")

    recipes_doc = json.loads(RECIPES.read_text(encoding="utf-8"))
    setup_doc = json.loads(SETUP.read_text(encoding="utf-8"))
    recipes = recipes_doc.get("recipes") or {}
    setups = setup_doc.get("plugins") or {}

    if HANDOFF.exists():
        shutil.rmtree(HANDOFF)
    if EMBEDDED.exists():
        shutil.rmtree(EMBEDDED)

    names = sorted(set(recipes) | set(setups))
    for name in names:
        if name not in recipes:
            print(f"skip {name}: no recipe executions")
            continue
        doc = build_agent_setup(name, recipes[name], setups.get(name))
        handoff_path = HANDOFF / name / ".xianix" / "agent-setup.json"
        embed_path = EMBEDDED / name / "agent-setup.json"
        write_json(handoff_path, doc)
        write_json(embed_path, doc)
        print(f"wrote {name}")

    print(f"generated {len(list(HANDOFF.iterdir()))} plugins -> {HANDOFF}")
    print(f"embedded fallback -> {EMBEDDED}")


if __name__ == "__main__":
    main()
