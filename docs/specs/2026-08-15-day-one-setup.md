# Day-one setup: the scaffolded style guide asks every question the skills answer to

Date: 2026-08-15 · Requested interactively by Mirko (remote session) · Scope: small-feature + docs

## Problem

A consumer who runs `docume init` and then `/docs-loop` gets a wiki whose depth, diagram usage and
tier coverage were never chosen by anybody. The scaffolded `_meta/STYLE.md` asks four questions
(audience, tone, structure, verification) and is silent on the three that decide whether the wiki
reads sparse: how big it should be, whether pages carry diagrams, and whether the business tier
exists at all. DocuMe's own dogfood wiki demonstrated the failure: ten technical pages, one diagram,
no business tier, and nothing in the docs explaining that `_meta/STYLE.md` is the lever or that
`/docs-processes` has to be run before the business tier exists.

## Change

1. **Scaffold** — `ProjectScaffolder.BuildStyleGuide()` grows from four to seven ask-only bullets:
   Audience, Tone, Structure, Scope, Diagrams, Business, Verification. Constraints preserved: one
   heading, the verbatim invitation "Fill these in for your project.", questions not answers
   (rule §9.5, `ConsumerKnowledgeCoverageTests`).
2. **Docs** — root `README.md`, `docs/wiki/README.md`, `docs/wiki/10-concepts/lifecycle.md`,
   `docs/wiki/20-reference/cli.md` and `docs/wiki/30-automation/skills.md` state plainly: the skills
   write to `_meta/STYLE.md`'s answers; generation is two skills, one per tier; the first run of
   each writes an inventory and no page; `_meta/BUSINESS.md` seeds the facts no code states.
3. **Skill contract** — `docs-loop/SKILL.md`'s description of the scaffolded file follows the new
   shape (seven bullets).

## Out of scope, recorded as follow-ups

- An interactive `docume init` interview (prompting for the seven answers on the terminal). The
  in-file questionnaire is the §9.4-idempotent, CI-safe form; an `--interview` flag can layer on it.
- Changing the dogfood wiki's own `_meta/STYLE.md` voice (more diagrams, deeper pages) and
  regenerating: that is a `/docs-refresh` run after the voice change, plus `ConsumerTopics` surgery
  in `ConsumerKnowledgeCoverageTests` if new sections are added.
- Running `/docs-processes` on this repo (first run: inventory only). Blocked on the M10 branch
  settling first; queue order is a gate question per `tasks/todo.md`.
