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
        # Fallback to aggregating directly from assistant records
        in_tok = 0
        cache_read = 0
        cache_write = 0
        out_tok = 0
        thinking_tok = 0
        session_id = None

        with open(latest, "r", encoding="utf-8") as f:
            for line in f:
                try:
                    data = json.loads(line)
                    if not session_id and data.get("sessionId"):
                        session_id = data.get("sessionId")
                    if data.get("type") == "assistant":
                        u = data.get("message", {}).get("usage", {})
                        in_tok += u.get("input_tokens", 0)
                        cache_read += u.get("cache_read_input_tokens", 0)
                        cache_write += u.get("cache_creation_input_tokens", 0)
                        out_tok += u.get("output_tokens", 0)
                        details = u.get("output_tokens_details") or {}
                        thinking_tok += details.get("thinking_tokens", 0)
                except Exception:
                    continue

        cost = (in_tok * 3.0 + cache_read * 0.30 + cache_write * 3.75 + out_tok * 15.0) / 1_000_000
        return {
            "session_id": session_id,
            "total_cost_usd": round(cost, 4),
            "models": {
                "claude-sonnet-5-5": {
                    "inputTokens": in_tok,
                    "outputTokens": out_tok,
                    "cacheReadInputTokens": cache_read,
                    "cacheCreationInputTokens": cache_write,
                    "thinkingTokens": thinking_tok,
                }
            },
            "transcript_file": latest,
        }

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
