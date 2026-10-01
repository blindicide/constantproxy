#!/usr/bin/env python3
"""Extract token usage and cost for constantproxy from Claude Code session transcripts."""

from __future__ import annotations

import glob
import json
import os
import sys
from pathlib import Path

PROJECT_SLUG = "-home-clawuser-projects-constantproxy"
CLAUDE_PROJECTS_DIR = Path.home() / ".claude" / "projects" / PROJECT_SLUG


def extract_usage() -> dict:
    pattern = str(CLAUDE_PROJECTS_DIR / "*.jsonl")
    files = sorted(glob.glob(pattern), key=os.path.getmtime)
    if not files:
        return {"error": f"No session transcripts found in {CLAUDE_PROJECTS_DIR}"}

    latest = files[-1]
    last_cost = None
    last_turn_duration = None

    with open(latest, "r", encoding="utf-8") as f:
        for line in f:
            try:
                data = json.loads(line)
                t = data.get("type")
                if t == "cost-state":
                    last_cost = data
                elif t == "system" and data.get("subtype") == "turn_duration":
                    last_turn_duration = data
            except Exception:
                continue

    if not last_cost:
        return {"file": latest, "error": "No cost-state record found yet"}

    model_usage = last_cost.get("modelUsage", {})
    summary = {
        "session_id": last_cost.get("sessionId"),
        "total_cost_usd": last_cost.get("totalCostUSD"),
        "models": model_usage,
        "transcript_file": latest,
    }
    return summary


def main():
    usage = extract_usage()
    print(json.dumps(usage, indent=2))


if __name__ == "__main__":
    main()
