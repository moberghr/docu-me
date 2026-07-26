#!/usr/bin/env python3
"""PreToolUse hook: refuse any `git push` that force-updates a remote ref.

WHY THIS EXISTS (iter130). Rule §8.2 is labelled "[ENFORCED — loop-settings deny +
protocol]", and the enforcement half was `tools/loop/loop-settings.json → permissions.deny`:

    "Bash(git push --force:*)"
    "Bash(git push -f:*)"

Those patterns match on WHOLE TOKENS FROM THE START of the command, so they only cover the
spelling where the flag comes first. Measured at iter130 against a nonexistent remote (so no
probe could push anything):

    git push --force <remote> HEAD            -> DENIED
    git push -f <remote> HEAD                 -> DENIED
    git push <remote> HEAD --force            -> ALLOWED   <- the bypass
    git push <remote> HEAD -f                 -> ALLOWED   <- the bypass
    git push --force-with-lease <remote> HEAD -> ALLOWED    (a different token, so no match)

The pattern language cannot express "this flag appears anywhere in the argv" -- a remote and a
refspec of any name may sit in front of it -- so no list of deny entries closes this. A hook can,
because it sees the whole command string.

The rule this enforces is unconditional ("NEVER force-push or rewrite history"), so this hook is
FAIL-CLOSED: when a command cannot be parsed it is blocked rather than allowed. A false block costs
one rephrased command; a false allow costs history that §8.2 says cannot be recovered.

Matching is on shlex TOKENS, which is more precise than it first looks and the harness pins both
sides of it: `grep -rn "git push --force" docs/` is ALLOWED, because the quoted mention is a single
token and neither `git` nor `push` stands alone. The residual over-block is an UNQUOTED mention --
`echo git push --force >> notes.md` tokenises exactly like the real command and is refused. That
case is asserted in .mtk/paths-130/mutate-force-push-guard.py so the cost stays visible.

WHAT IT DOES NOT DO: it does not widen any authority, and it does not touch `--delete` or plain
pushes. §8.2 is about force-updating refs; deleting a branch is a different question nobody has
asked. `git tag` / `git push --tags` stay with the deny list, which covers them correctly.

CONTRACT: stdin is the PreToolUse JSON payload ({"tool_name", "tool_input": {"command"}}).
Exit 0 allows, exit 2 blocks and shows stderr to the agent. Any other tool name is allowed
untouched.
"""

import json
import re
import shlex
import sys

# Flags that cause a non-fast-forward update of a remote ref. `--force-if-includes` only has an
# effect alongside a lease, but it is listed so the set reads as "every force spelling git has".
FORCE_FLAGS = frozenset(
    {"-f", "--force", "--force-with-lease", "--force-if-includes", "--mirror"}
)

# Shell separators that start a new command. Split on these so `make && git push ... -f` is seen
# as two segments and the second one is judged on its own.
SEGMENT_SPLIT = re.compile(r"(?:\|\||&&|[;|\n&])")


def _force_flag_in(tokens):
    """True when any token forces the update: a force flag, a short cluster, or a `+refspec`."""
    for token in tokens:
        if token in FORCE_FLAGS:
            return True
        # `-uf origin main` sets upstream AND forces; a cluster is short, starts with one dash,
        # and is not a negative number or a lone dash.
        if len(token) > 1 and token.startswith("-") and not token.startswith("--"):
            if "f" in token[1:] and token[1:].isalpha():
                return True
        # `git push origin +main:main` carries NO flag at all -- the leading `+` on the refspec is
        # what forces it. No non-force push argument starts with `+`, so this costs no precision.
        if len(token) > 1 and token.startswith("+"):
            return True
    return False


def _segment_is_force_push(segment):
    try:
        tokens = shlex.split(segment)
    except ValueError:
        # Unbalanced quotes. Fall back to whitespace so a malformed command cannot slip past by
        # being unparseable -- fail-closed, per the docstring.
        tokens = segment.split()

    if not tokens:
        return False
    if not any(token == "git" or token.endswith("/git") for token in tokens):
        return False
    if "push" not in tokens:
        return False

    return _force_flag_in(tokens)


def is_force_push(command):
    """True when any segment of `command` is a git push carrying a force flag."""
    return any(
        _segment_is_force_push(segment) for segment in SEGMENT_SPLIT.split(command)
    )


def main():
    raw = sys.stdin.read()
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError:
        # A payload this hook cannot read is not evidence that the command is safe, but blocking
        # every Bash call on a malformed payload would wedge the loop. Say so loudly and allow;
        # the deny list still covers the flag-first spellings underneath.
        print(
            "deny-history-rewrite: could not parse the hook payload; not inspecting this command",
            file=sys.stderr,
        )
        return 0

    if payload.get("tool_name") != "Bash":
        return 0

    command = payload.get("tool_input", {}).get("command", "")
    if not isinstance(command, str):
        return 0

    if not is_force_push(command):
        return 0

    print(
        "BLOCKED by tools/loop/hooks/deny-history-rewrite.py: this is a force-push.\n"
        "Rule §8.2 (.claude/rules/git-workflow.md): NEVER force-push or rewrite history.\n"
        "The loop-settings deny list only catches the flag-first spelling; this hook catches the\n"
        "rest. If you believe a force-update is genuinely required, it is Mirko's call -- record it\n"
        "as a blocker in tools/loop/state.json phrased as a question. Do not rephrase to evade.",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    sys.exit(main())
