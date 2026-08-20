# Licensing

DocuMe is **dual-licensed**. You may use it under **either** of the two licenses
below. Choose the one that fits how you intend to use the software.

> **Copyright © 2026 Moberg d.o.o.** All rights reserved.

Everything in this repository is covered: the `docume` CLI (`DocuMe.Cli`), the
library it is built on (`DocuMe.Core`), the Claude Code plugin under `plugin/`,
the workflow templates under `templates/`, and the composite action under
`actions/`.

---

## Option 1 — GNU AGPL v3.0 (free / open source)

DocuMe is available for free under the **GNU Affero General Public License,
version 3.0** (AGPLv3). The full text is in [`LICENSE`](LICENSE).

This is the right option if your use is open source, or internal, or you are
willing to comply with the AGPL's copyleft obligations. In short, the AGPL
requires that:

- If you **distribute** DocuMe, or a modified version of it, you must make the
  **complete corresponding source code** available under the AGPLv3.
- Any larger work that **incorporates** DocuMe becomes subject to the AGPLv3 as
  a whole. This is strong copyleft, and for DocuMe it is the clause that matters
  most: referencing the `DocuMe.Core` package from your own application makes
  that application a larger work.
- **§13 — network use is distribution.** If you run a modified DocuMe and let
  users interact with it over a network, you must offer those users the complete
  corresponding source of your modified version under the AGPLv3.

**Running the `docume` CLI is not distribution.** Installing the published tool,
pointing it at your repository, and publishing your own documentation with it
triggers none of the above, whatever license your own repository carries. The
AGPL governs DocuMe's code, not the markdown you write or the pages it produces.
Using DocuMe on a closed-source codebase is Option 1, and it is free.

If those obligations are acceptable to you, you owe nothing and need no further
permission. Just comply with [`LICENSE`](LICENSE).

---

## Option 2 — Commercial license (paid)

If you **cannot or do not want to** comply with the AGPLv3, for example because
you want to:

- reference **`DocuMe.Core`** from a **proprietary or closed-source** product,
- build DocuMe's converter, Confluence client, or drift engine into a tool you
  ship or sell **without** releasing your source,
- offer DocuMe, modified or not, as a **hosted or SaaS** service without
  source disclosure,
- redistribute a **modified** `docume` to customers under your own terms, or
- obtain a warranty, indemnification, or commercial support,

then you need a **commercial license**. A commercial license grants you the right
to use DocuMe **free of the AGPL's copyleft obligations**, under negotiated
terms.

**To obtain a commercial license, contact:**

- **Moberg d.o.o.** — <dev@moberg.hr>

> Commercial-license pricing and terms are negotiated separately and are **not**
> defined in this repository.

---

## Which one applies to me?

| Your situation | License you need |
|---|---|
| You install the `docume` CLI and publish your own repo's docs with it, closed source or not | AGPLv3 (free) |
| You install the Claude Code plugin and run its skills on your codebase | AGPLv3 (free) |
| You run the scaffolded GitHub Actions workflows in a private repository | AGPLv3 (free) |
| Personal, hobby, academic, or evaluation use | AGPLv3 (free) |
| You fork DocuMe, modify it, and publish your fork under AGPLv3 | AGPLv3 (free) |
| You **reference `DocuMe.Core`** from a **closed-source** application | **Commercial** |
| You ship a modified `docume` to customers without source disclosure | **Commercial** |
| You offer DocuMe as a hosted or SaaS service and will not share your source | **Commercial** |
| You need warranty, indemnity, or a support SLA | **Commercial** |

The dividing line is whether DocuMe's *code* leaves your hands. Consuming the
published tool is always free. Redistributing or embedding the library is what
the commercial license is for.

If you are unsure whether your use triggers the AGPL's copyleft, assume it does,
and either comply or contact us for a commercial license.

---

## Contributing

DocuMe's dual-license model only works if the project's owner holds, or is
licensed to relicense, the copyright in all contributions. **All contributors
must sign the Contributor License Agreement** before their contributions can be
merged. See [`CLA.md`](CLA.md).

This does **not** take away your rights to your own code. It grants the project
owner the rights needed to offer DocuMe under both the AGPLv3 and a commercial
license.
