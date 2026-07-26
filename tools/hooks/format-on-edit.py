#!/usr/bin/env python3
"""Format a C# file the moment an agent edits it (`PostToolUse`, matcher `Edit|Write`).

WHY THIS FILE EXISTS. `.claude/settings.json` has declared a format-on-edit hook since the MTK
bootstrap on 2026-07-24, pointing at `bash "$CLAUDE_PLUGIN_ROOT/hooks/format-on-edit.sh"`. iter133
measured that hook end to end and found it was born dead, in two independent ways:

  * `CLAUDE_PLUGIN_ROOT` is set by the CLI only for hooks that come FROM A PLUGIN. In a project
    settings file it expands to the EMPTY STRING, so the command ran `bash "/hooks/format-on-edit.sh"`
    and exited 127, "No such file or directory".
  * No such script was ever generated. `.claude/settings.json:20` was the only reference to it
    anywhere in the tree; there is no `.claude/hooks/` directory.

And the reason 133 iterations never noticed: a PostToolUse hook that fails is INVISIBLE to the agent.
The measured event carried `exit_code=127, outcome='error'`, yet no user/assistant turn mentioned it
and the session's `result.is_error` was `False`. Nothing surfaces, so nothing gets investigated.

WHAT THIS DOES. Reads the hook payload on stdin, and when the edited file is a `.cs` file inside this
repo, runs `dotnet format` scoped to that one file. Anything else is a no-op that costs nothing, which
matters because most of this loop's writes are Markdown and JSON, not C#.

Measured on this machine (iter133), which is why the scope is `.cs` only and the run is the full one:
  * `dotnet format <slnx> --include <one file>` .............. ~7.0 s wall (26 s CPU, parallel)
  * `dotnet format whitespace <slnx> --include <one file>` ... ~1.9 s wall, but whitespace only
The loop's own verification gate is `dotnet format --verify-no-changes` (the FULL check, covering
style and analyzer fixes as well as whitespace), so the whitespace-only subcommand would leave diffs
that gate still fails on. Paying 7 s per C# edit is the version that keeps the gate green, and it
replaces the slower loop of build-fails-on-an-analyzer, agent-fixes-whitespace-by-hand (iter132 did
exactly that for SA1515).
"""

from __future__ import annotations

import json
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOLUTION = "DocuMe.slnx"
TIMEOUT_SECONDS = 180


def target_file(payload: dict) -> str | None:
    """The edited file, or None when this payload is not one this hook should act on."""
    tool_input = payload.get("tool_input")
    if not isinstance(tool_input, dict):
        return None
    path = tool_input.get("file_path")
    if not isinstance(path, str) or not path.endswith(".cs"):
        return None
    absolute = os.path.abspath(path)
    if not os.path.isfile(absolute):
        # Deleted or moved between the tool call and this hook: nothing to format.
        return None
    if os.path.relpath(absolute, REPO).startswith(".."):
        # Outside the repo, so no project owns it and `--include` would match nothing.
        return None
    return absolute


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, UnicodeDecodeError):
        return 0
    if not isinstance(payload, dict):
        return 0

    absolute = target_file(payload)
    if absolute is None:
        return 0

    subprocess.run(
        [
            "dotnet",
            "format",
            SOLUTION,
            "--include",
            os.path.relpath(absolute, REPO),
            "--verbosity",
            "quiet",
        ],
        cwd=REPO,
        capture_output=True,
        timeout=TIMEOUT_SECONDS,
        check=False,
    )
    return 0


if __name__ == "__main__":
    # ALWAYS exit 0, and never print on success. Two measured reasons, not defensiveness:
    # a non-zero PostToolUse exit does not reach the agent at all (iter133), so it could only hide a
    # problem rather than report one; and stdout from a hook is injected into the agent's context,
    # where a formatter's chatter after every edit is pure noise. A formatter must never be able to
    # fail a tool call it only tidies up after.
    try:
        sys.exit(main())
    except subprocess.TimeoutExpired:
        sys.exit(0)
