# DocuMe — Claude Code plugin

The generative tier of the DocuMe lifecycle (PLAN.md §11). The `docume` CLI is deterministic and does
every Confluence write; the skills in here are the parts that need judgement — writing a page, deciding
whether a reviewer's comment is right, rewriting a page whose code moved. **Their output is always a pull
request.** No skill calls the Confluence API, and no skill publishes (rule §0.4, PLAN.md §9).

## Skills

| Skill | What it does | Spec |
|---|---|---|
| `/docs-loop` | The generation engine: inventory, section taxonomy, one-unit-per-run discipline, PROGRESS/GAPS bookkeeping, opens `docs/loop-<date>` | §11 |
| `/docs-refresh` | Rewrites the pages whose declared `sources` changed since the generation baseline, opens `docs/refresh-<date>` | §10 |
| `/docs-feedback` | Verifies a reviewer's Confluence comment against the code, opens `docs/feedback-<date>` (or declines with a citation) | §9 |

Repo-specific knowledge (domain list, tone, audience, structure) is not in these skills: it lives in the
consumer repo's `docume.json` and `_meta/STYLE.md`, which every skill reads at start (rule §9.5).

## Install

The repo is its own marketplace, so it can be installed directly:

```
/plugin marketplace add moberghr/docu-me
/plugin install docume@docume
```

`docu-me` is private, so this uses your git credentials for the clone.

### The entry for the Moberg marketplace

§11 distributes DocuMe through the existing Moberg marketplace, the same one MTK ships from. That
marketplace lives in another repository, so this entry has to be added there by hand — add it to its
`.claude-plugin/marketplace.json` `plugins` array:

```json
{
  "name": "docume",
  "source": {
    "source": "git-subdir",
    "url": "https://github.com/moberghr/docu-me.git",
    "path": "plugin",
    "ref": "v0.1.1"
  },
  "description": "The generative half of the DocuMe docs lifecycle: write, refresh and correct a repo-based wiki that publishes to Confluence. Every skill's output is a pull request, and only the `docume` CLI talks to Confluence.",
  "category": "documentation"
}
```

`git-subdir` rather than `github`, because the plugin is one directory of a repository that also carries
a .NET solution: a sparse clone of `plugin/` is the whole download. Bump `ref` to each release tag —
§12's single-version rule means that tag is also the `DocuMe.Cli` package version.

## Two conventions worth knowing before editing

**The manifest declares no `skills` path.** `skills/` is scanned by default, so every
`skills/<name>/SKILL.md` in this directory is discovered without being listed. A fourth skill is therefore
a new directory and nothing else — `docs-loop` was added that way. What keeps that honest is
`PluginManifestTests`, which walks the tree rather than the manifest, plus `SkillContractTests`, which
asserts each skill's frontmatter, its untrusted-input clause and the branch its PR is opened on.

**`version` in `plugin.json` is the only copy.** It is deliberately absent from the marketplace entry:
Claude Code lets the entry carry one and lets `plugin.json` win, which would leave two numbers to bump
and one of them silently ignored. §12 releases CLI, Core, plugin and action off a single version, so a
release bumps `Directory.Build.props` and `plugin.json` together — asserted equal by
`PluginManifestTests`, because a plugin pinned to a stale version string is one that never updates.
