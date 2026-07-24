# Human gates

The build loop pauses **dependent** work until the box is ticked; independent milestone work continues.
Tick a box (`- [x]`) and the loop picks it up within ~30 minutes. Each gate also fires a macOS notification when opened.

## Open gates

_(none yet — the loop appends them as it reaches them)_

## Anticipated (for orientation, not yet open)

- **gate-m2-aur-review** — page-by-page human review of the 79-page AurServices bulk publish in the sandbox space (PLAN.md §14 M2 acceptance)
- **gate-m3-approval-roundtrip** — a human adds the `approved` label to verify the approval → invalidation → re-approval cycle (M3 acceptance)
- **gate-m7-production** — permission to publish to the production AUR space + team onboarding (M7). Ticking this also requires setting `confluence.productionAllowed: true` in `tools/loop/state.json`.

## Setup Mirko must do before M2 (the loop will BLOCK on these otherwise)

- [ ] Create/choose a **sandbox Confluence space** and set `confluence.sandboxSpaceKey` in `tools/loop/state.json`
- [ ] Export `DOCUME_CONFLUENCE_EMAIL` and `DOCUME_CONFLUENCE_TOKEN` in the shell that runs `tools/loop/docume-loop.sh`

## Setup — not milestone-blocking, but do it soon

- [x] **setup-claude-dir** (opened 2026-07-24 iter 1; done 2026-07-24 in an interactive session) — MTK bootstrap finalized: the staged `.claude-proposed/` config was promoted into `.claude/` (rules, references incl. Moberg coding-guidelines, settings, tech-stack, mtk-version) and `.claude-proposed/` removed. Claude Code now auto-loads `.claude/rules/` and MTK resolves `.claude/references/`. The headless loop still cannot *write* `.claude/` (sensitive-file protection) but reads it fine, so no loop change was required.
  Optional, to let future loop iterations maintain `.claude/` themselves: add `"Write(.claude/**)"` and `"Edit(.claude/**)"` to `permissions.allow` in `tools/loop/loop-settings.json` (not done — the loop doesn't need to write `.claude/` for current milestones).
