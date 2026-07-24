---
description: Pre-commit review checklist for DocuMe — selected by setup-bootstrap from scan evidence
---

# Pre-Commit Review List

Stack-conditional items (EF Core, MediatR, Lambda) were dropped: none of these appear in DocuMe's plan or code — no database, no MediatR, no Lambda (`PLAN.md` §4).

- [ ] No PII in logs
- [ ] Tests for new public methods
- [ ] No hardcoded secrets (watch: `DOCUME_CONFLUENCE_*` values, Confluence URLs with embedded credentials)
