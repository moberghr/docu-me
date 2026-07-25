#!/usr/bin/env node
// DocuMe mermaid renderer (PLAN.md §4: "shell out to Node + beautiful-mermaid").
//
// Scaffolded into consumer repos by `docume init` as tools/render-mermaid.mjs and named by
// docume.json -> mermaid.renderer (PLAN.md §5.1). `docume publish` runs it once per
// ```mermaid fence and uploads the SVG as a page attachment (§6.2 step 3).
//
// CONTRACT — deliberately dumb and text-only, so it is testable from a shell:
//   in   diagram source on stdin, or the file named by the first argument
//   out  a self-contained SVG document on stdout, and nothing else
//   err  a one-line diagnostic, never a stack trace
//   exit 0 rendered | 2 the diagram did not render | 3 dependency missing | 4 no input
// Distinct failure codes because the fixes differ: 3 is "run npm install", 2 is "fix the
// diagram". Anything on stdout that is not SVG would be uploaded verbatim as an attachment,
// so diagnostics go to stderr only.
//
// Requires Node >= 20 and beautiful-mermaid installed where Node can resolve it from this
// script's directory (repo root node_modules is the normal case):
//   npm install beautiful-mermaid@1.1.3
//
// The renderer is a reimplementation of mermaid, not mermaid itself, so it accepts a
// SUBSET of the dialect. Verified on 1.1.3: graph/flowchart, sequenceDiagram, classDiagram,
// stateDiagram-v2, erDiagram and xychart-beta render; `pie` does not, and a trailing
// semicolon on the HEADER line (`graph TD;`) is rejected even though mermaid.js accepts it.
// Those surface here as exit 2 with the renderer's own message — a failed page, never a
// wrong diagram.

import { readFileSync } from 'node:fs';

const EXIT_RENDER_FAILED = 2;
const EXIT_DEPENDENCY_MISSING = 3;
const EXIT_NO_INPUT = 4;

// process.exitCode over process.exit(): exit() can truncate a pending stdout write, which
// on a 30 KB SVG would publish a corrupt attachment.
function fail(code, message) {
  process.stderr.write(`render-mermaid: ${message}\n`);
  process.exitCode = code;
}

async function readSource() {
  const [file] = process.argv.slice(2);
  if (file) {
    return readFileSync(file, 'utf8');
  }

  // Nothing piped in and no file named: a human ran the script by hand.
  if (process.stdin.isTTY) {
    return null;
  }

  // The stream, never readFileSync(0): a parent that spawns this script gets a NON-BLOCKING
  // stdin pipe (.NET's Process does, so `docume publish` does), and a synchronous read of fd 0
  // then throws EAGAIN whenever the source has not arrived yet — a lost diagram decided by
  // process scheduling. Iterating the stream waits for readability instead, which is also what
  // keeps a source larger than one pipe buffer whole.
  const chunks = [];
  for await (const chunk of process.stdin) {
    chunks.push(chunk);
  }

  return Buffer.concat(chunks).toString('utf8');
}

async function main() {
  let source;
  try {
    source = await readSource();
  } catch (error) {
    return fail(EXIT_NO_INPUT, `could not read the diagram source: ${error.message}`);
  }

  if (source === null) {
    return fail(
      EXIT_NO_INPUT,
      'no diagram source. Pipe it on stdin, or pass a file path as the first argument.',
    );
  }

  if (source.trim() === '') {
    return fail(EXIT_NO_INPUT, 'the diagram source is empty.');
  }

  let renderMermaidSVG;
  try {
    ({ renderMermaidSVG } = await import('beautiful-mermaid'));
  } catch (error) {
    return fail(
      EXIT_DEPENDENCY_MISSING,
      'cannot load beautiful-mermaid. Install it where Node resolves it from this script '
        + `(usually the repo root): npm install beautiful-mermaid@1.1.3 — ${error.message}`,
    );
  }

  let svg;
  try {
    svg = renderMermaidSVG(source);
  } catch (error) {
    return fail(EXIT_RENDER_FAILED, `the diagram did not render: ${error.message}`);
  }

  process.stdout.write(svg);
  return undefined;
}

await main();
