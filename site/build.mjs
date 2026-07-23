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
  // ./fonts/* — the LP's own @font-face; /fonts/* — the app tokens.css
  // @font-face (root-absolute). Both resolve at runtime against dist/fonts.
  external: ["./fonts/*", "/fonts/*"],
  outfile: resolve(dist, "app.css"),
});

await cp(resolve(here, "index.html"), resolve(dist, "index.html"));
// Privacy policy page. Self-contained (its own inline styles + the shared
// Inter face from /fonts), served at /privacy for the Microsoft Store listing.
await cp(resolve(here, "privacy.html"), resolve(dist, "privacy.html"));
await cp(resolve(repo, "docs/media/workspace.png"), resolve(dist, "media/workspace.png"));
await cp(resolve(repo, "src/web/perch-glyph.png"), resolve(dist, "perch-glyph.png"));
// The app icon (monocled bird) — the projects-empty state and any real chrome
// we mount reference /perch-logo.png at the server root.
await cp(resolve(repo, "src/web/perch-glyph.png"), resolve(dist, "perch-logo.png"));
await cp(resolve(repo, "src/web/fonts/InterVariable.woff2"), resolve(dist, "fonts/InterVariable.woff2"));
// Geist Mono — the app's terminal/mono face. The real chrome we reuse (sidebar
// meta, inspector work rows) reads var(--font-mono) from the app's tokens.css,
// whose @font-face points at /fonts/GeistMonoVariable.woff2.
await cp(resolve(repo, "src/web/fonts/GeistMonoVariable.woff2"), resolve(dist, "fonts/GeistMonoVariable.woff2"));
// Actions-based Pages skips Jekyll anyway; the marker makes it explicit.
await writeFile(resolve(dist, ".nojekyll"), "");
// Custom domain. The workflow deploy source (unlike branch-based Pages) never
// commits a CNAME, so emit it into every artifact to keep buildwithperch.com
// pinned — a redeploy can't silently drop the domain.
await writeFile(resolve(dist, "CNAME"), "buildwithperch.com\n");

console.log("[site] built ->", dist);
