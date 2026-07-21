// Builds the landing page into site/dist. Reuses src/web's node_modules for
// esbuild (no second install) and pulls shared assets from the repo so the
// site can't drift from the app: the hero rig is imported from
// setup-overlay.ts at bundle time, the product shot comes from docs/media.
//
//   node site/build.mjs
//
// Deployed by .github/workflows/pages.yml on push to main.

import { createRequire } from "module";
import { cp, mkdir, writeFile } from "fs/promises";
import { fileURLToPath } from "url";
import { dirname, resolve } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..");
const dist = resolve(here, "dist");

const require = createRequire(resolve(repo, "src/web/package.json"));
const esbuild = require("esbuild");

await mkdir(resolve(dist, "media"), { recursive: true });
await mkdir(resolve(dist, "fonts"), { recursive: true });

await esbuild.build({
  entryPoints: [resolve(here, "src/main.ts")],
  bundle: true,
  format: "iife",
  target: "es2022",
  minify: true,
  outfile: resolve(dist, "app.js"),
});
await esbuild.build({
  entryPoints: [resolve(here, "src/style.css")],
  bundle: true,
  minify: true,
  loader: { ".woff2": "copy" },
  external: ["./fonts/*"],
  outfile: resolve(dist, "app.css"),
});

await cp(resolve(here, "index.html"), resolve(dist, "index.html"));
await cp(resolve(repo, "docs/media/workspace.png"), resolve(dist, "media/workspace.png"));
await cp(resolve(repo, "src/web/perch-glyph.png"), resolve(dist, "perch-glyph.png"));
await cp(resolve(repo, "src/web/fonts/InterVariable.woff2"), resolve(dist, "fonts/InterVariable.woff2"));
// Actions-based Pages skips Jekyll anyway; the marker makes it explicit.
await writeFile(resolve(dist, ".nojekyll"), "");

console.log("[site] built ->", dist);
