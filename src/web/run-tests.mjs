// Test harness: discover and run every test/*.test.ts under `node --test`.
//
// esbuild bundles each test (plus the real source modules it imports) into
// .test-out/, then node's built-in test runner executes them. Auto-discovery
// is the point — drop a new `test/<feature>.test.ts` beside the others and it
// runs with `npm test`, no script edits. Keep one suite per shipped feature so
// new functionality lands with coverage.

import { build } from "esbuild";
import { readdirSync, rmSync, mkdirSync } from "node:fs";
import { join, basename } from "node:path";
import { spawnSync } from "node:child_process";

const TEST_DIR = "test";
const OUT_DIR = ".test-out";

const entries = readdirSync(TEST_DIR)
  .filter((f) => f.endsWith(".test.ts"))
  .map((f) => join(TEST_DIR, f));

if (entries.length === 0) {
  console.error(`no *.test.ts files found in ${TEST_DIR}/`);
  process.exit(1);
}

rmSync(OUT_DIR, { recursive: true, force: true });
mkdirSync(OUT_DIR, { recursive: true });

await build({
  entryPoints: entries,
  bundle: true,
  platform: "node",
  format: "esm",
  outdir: OUT_DIR,
  // A test might transitively import a module that pulls in CSS; drop it so
  // the bundle still builds for node (styles aren't under test here).
  loader: { ".css": "empty" },
});

const outFiles = entries.map((e) => join(OUT_DIR, basename(e).replace(/\.ts$/, ".js")));
console.log(`running ${outFiles.length} test file(s): ${entries.map((e) => basename(e)).join(", ")}`);

const result = spawnSync(process.execPath, ["--test", ...outFiles], { stdio: "inherit" });
process.exit(result.status ?? 1);
