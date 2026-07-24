---
description: Git workflow rules for DocuMe — loop commits, milestone messages
---

# Git Workflow Rules

- **§8.1 [CONVENTION]** Work lands on `main` via autonomous-loop commits; messages are milestone-prefixed and descriptive, e.g. `M2: publish pipeline — upsert + attachment hashing` (`tools/loop/ITERATION-PROMPT.md`).
- **§8.2 [ENFORCED — loop-settings deny + protocol]** NEVER force-push or rewrite history (`tools/loop/loop-settings.json` denies `git push --force`).
- **§8.3 [CONVENTION]** Never commit red builds: `dotnet build` + `dotnet test` green before every commit (iteration protocol; MTK verification-before-completion).
- **§8.4 [CONVENTION]** Branch names for PR flows use slash grouping: `docs/feedback-<date>`, `docs/refresh-<date>`, `docs/sync` (`PLAN.md` §6.3, §9, §10; coding guidelines "Git branch naming").
