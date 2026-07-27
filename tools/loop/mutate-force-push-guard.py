#!/usr/bin/env python3
"""Prove tools/loop/hooks/deny-history-rewrite.py blocks what it claims and nothing else.

iter119's lesson: a guard whose red branch has never fired is not a guard. This drives the hook
the way Claude Code does -- PreToolUse JSON on stdin, exit code out -- and requires BOTH
directions, because a hook that blocked everything would otherwise score full marks.

The BLOCK cases are not invented: cases 1-3 are the exact command shapes iter130 measured as
ALLOWED by the loop-settings deny list, run then against a nonexistent remote so nothing could
push. The ALLOW cases are commands this loop actually runs every iteration.

ONE BRANCH OF THE HOOK IS NOT COVERED HERE, AND ITS CASES LIVE ELSEWHERE (noted iter163). Every
case below hands the hook a WELL-FORMED payload, so none of them says anything about what happens
when the payload itself cannot be parsed - a branch that returned 0 until iter163 measured that a
non-blocking hook is inaudible in the driver's invocation and made it fail closed. Those cells are
in `tools/loop/mutate-soft-flags.py` (family 3), which also re-checks the two controls below.
The count here is deliberately unchanged at 25 so the "25/25" cited in GATES.md and state.json stays
true; run both harnesses, not just this one.

Run: python3 tools/loop/mutate-force-push-guard.py
"""

import json
import pathlib
import subprocess
import sys

HOOK = pathlib.Path(__file__).resolve().parents[2] / "tools/loop/hooks/deny-history-rewrite.py"

BLOCK = 2
ALLOW = 0

# (command, why it is in this list)
MUST_BLOCK = [
    ("git push deny-probe-nonexistent-remote HEAD --force", "iter130 measured this ALLOWED"),
    ("git push deny-probe-nonexistent-remote HEAD -f", "iter130 measured this ALLOWED"),
    ("git push --force-with-lease deny-probe-nonexistent-remote HEAD", "iter130 measured this ALLOWED"),
    ("git push origin main --force-if-includes", "other force spelling, flag last"),
    ("git push origin main --mirror", "--mirror force-updates every ref"),
    ("git -c push.default=current push origin main --force", "global option before the subcommand"),
    ("git --no-pager push origin main -f", "global option, short flag"),
    ("dotnet test && git push origin main --force", "force push in the second segment"),
    ("git push -uf origin main", "short flag cluster -uf"),
    ("/usr/bin/git push origin main --force", "absolute path to git"),
    ("git push origin +main:main", "refspec force carries no flag at all"),
    ("git push origin +main", "same, short refspec form"),
    ('git push origin main --force "', "unbalanced quote must fail closed, not open"),
]

MUST_ALLOW = [
    ("git push origin main", "a plain push is a pushPolicy question, not a force question"),
    ("git push --dry-run origin main", "dry run writes nothing"),
    ('git commit -m "M6: loop machinery"', "every iteration runs this"),
    ("git status -sb", "every iteration runs this"),
    ("dotnet build", "every iteration runs this"),
    ("python3 tools/loop/check-state-size.py", "every iteration runs this"),
    ("git log --oneline -8", "step 1 of the protocol"),
    ("git fetch --force origin", "fetch --force touches local refs only, and has no push token"),
    ("git rev-parse HEAD", "harmless"),
    ('grep -rn "git push --force" docs/', "a QUOTED mention is one shlex token, so it is not a push"),
]

# Commands the hook refuses even though they push nothing. Documented, not hidden: the docstring
# calls the guard fail-closed, so these are the price of that choice and the harness pins them so
# the cost stays visible instead of being discovered later as a bug.
EXPECTED_OVERBLOCK = [
    ("echo git push --force >> notes.md", "an UNQUOTED mention tokenises exactly like the real thing"),
]


def run_hook(command, tool_name="Bash"):
    payload = json.dumps({"tool_name": tool_name, "tool_input": {"command": command}})
    proc = subprocess.run(
        [sys.executable, str(HOOK)],
        input=payload,
        capture_output=True,
        text=True,
        timeout=20,
    )
    return proc.returncode, proc.stderr


def main():
    if not HOOK.exists():
        print(f"FAIL: hook not found at {HOOK}")
        return 1

    passed = 0
    failed = []

    for command, why in MUST_BLOCK:
        code, err = run_hook(command)
        if code == BLOCK and "8.2" in err:
            passed += 1
            continue
        failed.append(f"NOT BLOCKED (exit {code}) [{why}]: {command}")

    for command, why in MUST_ALLOW:
        code, _ = run_hook(command)
        if code == ALLOW:
            passed += 1
            continue
        failed.append(f"WRONGLY BLOCKED (exit {code}) [{why}]: {command}")

    for command, why in EXPECTED_OVERBLOCK:
        code, _ = run_hook(command)
        if code == BLOCK:
            passed += 1
            continue
        failed.append(f"over-block case changed behaviour (exit {code}) [{why}]: {command}")

    # A force-push string arriving through a different tool must not be judged by this hook.
    code, _ = run_hook("git push origin main --force", tool_name="Edit")
    if code == ALLOW:
        passed += 1
    else:
        failed.append(f"non-Bash tool wrongly blocked (exit {code})")

    total = len(MUST_BLOCK) + len(MUST_ALLOW) + len(EXPECTED_OVERBLOCK) + 1
    print(f"{passed}/{total} cases behaved as specified")
    print(f"  block cases: {len(MUST_BLOCK)}   allow cases: {len(MUST_ALLOW)}   "
          f"documented over-blocks: {len(EXPECTED_OVERBLOCK)}   other-tool: 1")

    for line in failed:
        print(f"  {line}")

    if failed:
        print("RESULT: FAIL")
        return 1

    print("RESULT: PASS -- both directions proven, so a hook that blocked everything could not pass")
    return 0


if __name__ == "__main__":
    sys.exit(main())
