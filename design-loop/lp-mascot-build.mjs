// Bundles lp-mascot-entry.ts -> lp-mascot.js for the LP design mockups.
// Reuses src/web's esbuild, same as site/build.mjs.  node design-loop/lp-mascot-build.mjs
import { createRequire } from "module";
import { fileURLToPath } from "url";
import { dirname, resolve } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, "..");
const require = createRequire(resolve(repo, "src/web/package.json"));
const esbuild = require("esbuild");

await esbuild.build({
  entryPoints: [resolve(here, "lp-mascot-entry.ts")],
  bundle: true,
  format: "iife",
  target: "es2022",
  minify: true,
  outfile: resolve(here, "lp-mascot.js"),
});
// The app's real stylesheet + demo shims, for the reused-component stages.
// Font urls stay as-is (/fonts/*) — the design-loop server aliases them.
await esbuild.build({
  entryPoints: [resolve(here, "lp-demo.css")],
  bundle: true,
  minify: true,
  external: ["/fonts/*", "./fonts/*"],
  outfile: resolve(here, "lp-app.css"),
});
console.log("[lp-mascot] built -> design-loop/lp-mascot.js + lp-app.css");
