# Contributor License Agreement (CLA)

> **Note:** This is a drafting template, not legal advice. Have it reviewed by a
> qualified lawyer before relying on it.

Thank you for your interest in contributing to **DocuMe** (the "Project"), owned
by **Moberg d.o.o.** (the "Owner").

This Contributor License Agreement ("Agreement") documents the rights granted by
contributors to the Owner. It exists so the Owner can offer DocuMe under **both**
the GNU AGPL v3.0 **and** a separate commercial license (see
[`LICENSING.md`](LICENSING.md)). By submitting a Contribution, for example by
opening a pull request, you agree to the terms below.

## 1. Definitions

- **"You"** means the individual or legal entity making a Contribution.
- **"Contribution"** means any original work of authorship, including code,
  documentation, workflow templates, skill definitions, or other materials, that
  You intentionally submit to the Project, in any form and through any medium.

## 2. Copyright License

You grant the Owner and recipients of software distributed by the Owner a
**perpetual, worldwide, non-exclusive, royalty-free, irrevocable** copyright
license to reproduce, prepare derivative works of, publicly display, publicly
perform, sublicense, and distribute Your Contributions and such derivative
works. **You agree that the Owner may license Your Contributions under any
terms, including both the GNU AGPL v3.0 and proprietary or commercial licenses.**

## 3. Patent License

You grant the Owner and recipients of the software a perpetual, worldwide,
non-exclusive, royalty-free, irrevocable (except as stated in this section)
patent license to make, have made, use, offer to sell, sell, import, and
otherwise transfer Your Contribution, where such license applies only to those
patent claims licensable by You that are necessarily infringed by Your
Contribution alone or by combination of Your Contribution with the Project.

## 4. You Retain Your Rights

You **retain all right, title, and interest** in and to Your Contributions. This
Agreement does **not** transfer ownership; it grants the licenses described
above. You are free to use Your Contributions for any other purpose.

## 5. Your Representations

You represent that:

1. Each Contribution is **Your original creation**, or You have the right to
   submit it under this Agreement.
2. Your Contribution does **not** knowingly violate any third party's copyright,
   patent, trademark, or other intellectual-property right.
3. If Your employer has rights to intellectual property You create, You have
   received permission to make the Contribution on behalf of that employer, or
   the employer has waived such rights for Your Contributions.
4. Your Contribution contains **no credentials, tokens, or customer data**. This
   matters more than usual here: DocuMe's tests and fixtures sit next to code
   that authenticates against Confluence, and the Project's own rules forbid a
   credential in source, config, tests, fixtures, or logs.

## 6. Support and Warranty Disclaimer

You are not expected to provide support for Your Contributions. Contributions are
provided **"AS IS"**, without warranties or conditions of any kind, express or
implied, except as required by applicable law or agreed to in writing.

---

### How to sign

Add a `Signed-off-by:` line to each commit in your pull request, using the same
name and email as your commit authorship:

```bash
git commit -s -m "your message"
```

That line certifies that you have read this Agreement and that the
representations in §5 hold for the Contribution. A maintainer will ask for it if
it is missing.
