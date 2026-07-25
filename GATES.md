# Human gates

The build loop pauses **dependent** work until the box is ticked; independent milestone work continues.
Tick a box (`- [x]`) and the loop picks it up within ~30 minutes. Each gate also fires a macOS notification when opened.

## Open gates

- [ ] **gate-m1-aurservices-files** (opened 2026-07-25 iter 21) — **the loop needs the AurServices wiki markdown to finish M1.** Every named M1 feature is now built and green (frontmatter, converter, mermaid render, link map); the only thing left in the milestone is its acceptance criterion — "all 79 AurServices pages convert with zero errors and zero unknown-construct warnings" (PLAN.md §4.4) — and those files are not in this repo.
  **What to do:** copy the AurServices wiki (the `.md` pages *and* their images, directory structure intact) somewhere this machine can read, then add its absolute path to `tools/loop/state.json` as `"acceptance": { "aurServicesWikiPath": "/Users/mirkobudimir/…" }`. Edit it **between iterations** (each iteration reads state at start and rewrites it at the end, so a mid-iteration edit is lost).
  **Do not commit those files into this repo** — they belong to AurServices. A path outside the repo, or the gitignored `.mtk/aurservices/`, is what the loop expects.
  **What the loop does with it:** read-only. Loads the tree, converts all 79 pages, and reports failures grouped by construct and by dialect. No Confluence access, no publishing, nothing written back to those files. It settles two open questions at once: how many pages use a code-fence dialect that now fails loud, and how many of the 59 mermaid diagrams use spellings `beautiful-mermaid` rejects (`graph TD;` with a trailing semicolon, `pie`) — the single highest-risk unknown left in M1.

## Anticipated (for orientation, not yet open)

- **gate-m2-aur-review** — page-by-page human review of the 79-page AurServices bulk publish in the sandbox space (PLAN.md §14 M2 acceptance)
- **gate-m3-approval-roundtrip** — a human adds the `approved` label to verify the approval → invalidation → re-approval cycle (M3 acceptance)
- **gate-m7-production** — permission to publish to the production AUR space + team onboarding (M7). Ticking this also requires setting `confluence.productionAllowed: true` in `tools/loop/state.json`.

## Setup Mirko must do before M2 (the loop will BLOCK on these otherwise)

**Decided 2026-07-24 (delegated to Claude): a dedicated disposable sandbox space on Moberg's own Confluence — NOT the existing `ITAPPS` space.** `publish --prune` deletes orphan pages and republish overwrites hand edits by design, so the loop must never point at a space holding human content or watchers. Full rationale and the accepted cross-tenant consequence: `.claude/references/decisions.md`. Only the clicks below are left; nothing here needs another judgement call.

- [ ] **Create the space** on Moberg's Confluence: *Spaces → Create a space → Blank space*, name **`DocuMe Sandbox`**, key **`DOCUMESBX`**. Restrict permissions to yourself only, so 79 pages of machine churn plus machine comments never reach anyone's notification feed.
- [ ] **Set the key:** `confluence.sandboxSpaceKey: "DOCUMESBX"` in `tools/loop/state.json`. Edit it between iterations (each iteration reads state at start and rewrites it at the end, so a mid-iteration edit is lost).
- [ ] **Set the sandbox base URL.** The sandbox is on a *different tenant* than production `AUR` (`kvika.atlassian.net`), and `state.json → confluence` has no field for this yet. Add `confluence.sandboxBaseUrl: "https://<moberg-site>.atlassian.net/wiki"` when you set the key; the next loop iteration should thread it through the config loader as its own slice before any publish attempt.
- [ ] **Create an API token** for your *Moberg-site* account at `id.atlassian.com/manage-profile/security/api-tokens` (the AUR/Kvika token will not work cross-tenant).
- [x] **Export credentials, then RESTART the loop.** `DOCUME_CONFLUENCE_EMAIL` and `DOCUME_CONFLUENCE_TOKEN` must be exported in the shell that launches `tools/loop/docume-loop.sh`. **Done — verified 2026-07-25 (iter33):** the running loop has both vars. Evidence: a `docume publish` smoke run got past `ConfluenceCredentials.FromEnvironment()` (which throws when either is missing) and reached the space lookup. Which token/account it is has not been checked, and cannot be from inside the loop — if it is the Kvika/AUR token it will not work against a Moberg-site sandbox (cross-tenant), so the API-token item above may still be outstanding.

**So the only thing left blocking a live sandbox run is the space itself: the three items above (create `DOCUMESBX`, set `confluence.sandboxSpaceKey`, set `confluence.sandboxBaseUrl`).** As of iter33 the write path is built and WireMock-tested; it needs a real space to smoke-test against.

## Setup — not milestone-blocking, but do it soon

- [x] **setup-claude-dir** (opened 2026-07-24 iter 1; done 2026-07-24 in an interactive session) — MTK bootstrap finalized: the staged `.claude-proposed/` config was promoted into `.claude/` (rules, references incl. Moberg coding-guidelines, settings, tech-stack, mtk-version) and `.claude-proposed/` removed. Claude Code now auto-loads `.claude/rules/` and MTK resolves `.claude/references/`. The headless loop still cannot *write* `.claude/` (sensitive-file protection) but reads it fine, so no loop change was required.
  Optional, to let future loop iterations maintain `.claude/` themselves: add `"Write(.claude/**)"` and `"Edit(.claude/**)"` to `permissions.allow` in `tools/loop/loop-settings.json` (not done — the loop doesn't need to write `.claude/` for current milestones).
