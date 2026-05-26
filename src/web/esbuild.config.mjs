// Bundle the cmux webview content into src/CmuxWin/wwwroot/. WebView2's
// virtual-host mapping serves this directory under https://cmux.local/.
//
// `node esbuild.config.mjs`           — one-shot build
// `node esbuild.config.mjs --watch`   — incremental rebuild on file change

import * as esbuild from "esbuild";
import { cp, mkdir, rm } from "node:fs/promises";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outDir = resolve(here, "../CmuxWin/wwwroot");
const watch = process.argv.includes("--watch");

async function copyStatics() {
  await mkdir(outDir, { recursive: true });
  // index.html + the xterm.css (consumed by main.ts but bundled separately
  // because it sets up font metrics that need to be in <head> before xterm
  // initializes).
  await cp(resolve(here, "index.html"), resolve(outDir, "index.html"));
  await cp(
    resolve(here, "node_modules/@xterm/xterm/css/xterm.css"),
    resolve(outDir, "xterm.css")
  );
}

await rm(outDir, { recursive: true, force: true });
await copyStatics();

/** @type {esbuild.BuildOptions} */
const opts = {
  entryPoints: [resolve(here, "src/main.ts")],
  bundle: true,
  format: "esm",
  target: "es2022",
  // Single-bundle JS + a single CSS file from the imported tokens/style sheets.
  outfile: resolve(outDir, "app.js"),
  loader: { ".css": "css" },
  sourcemap: "linked",
  // Minify in non-watch builds — the page load is local, but smaller bundles
  // mean faster startup.
  minify: !watch,
  logLevel: "info",
  // Make sure relative font URLs from xterm.css resolve at runtime by
  // inlining; we don't currently use any.
  assetNames: "assets/[name]-[hash]",
};

if (watch) {
  const ctx = await esbuild.context(opts);
  await ctx.watch();
  console.log("[esbuild] watching src/web/src/ ...");
} else {
  await esbuild.build(opts);
  console.log(`[esbuild] built -> ${outDir}`);
}
