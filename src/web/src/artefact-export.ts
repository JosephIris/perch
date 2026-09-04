// Turning one artefact into a standalone HTML document.
//
// The room's artefact panel is a strip at the bottom of the window; a plan or
// a draft ticket wants a whole tab. Rather than teach the host a second
// markdown renderer, the PAGE renders the document — it already has the text
// and the renderer the room uses — and hands the host finished HTML to write
// out and open as a browser tab.
//
// "Standalone" is the whole point: the tab is a separate WebView2 with no
// access to our bundle, so the styling has to travel inside the file. We lift
// it out of the running stylesheet instead of duplicating it, so the document
// keeps matching the room even as the room's CSS changes.

/** The `:root` custom properties plus every rule the markdown renderer's
 *  output can hit. Read from the live stylesheet, so this can't drift from
 *  what the room shows. Silent on a stylesheet we're not allowed to read
 *  (there are none today; the guard is for a future CDN/font sheet). */
export function documentCss(): string {
  const out: string[] = [];
  for (const sheet of Array.from(document.styleSheets)) {
    let rules: CSSRuleList;
    try {
      rules = (sheet as CSSStyleSheet).cssRules;
    } catch {
      continue;
    }
    for (const rule of Array.from(rules)) {
      const sel = (rule as CSSStyleRule).selectorText;
      if (!sel) continue;
      // `:root` carries the design tokens every rule below refers to; the
      // rest is the markdown renderer's own output (see text.ts appendBlocks)
      // and the panel's <pre> for non-prose artefacts.
      if (sel === ":root" || /(^|[\s,])\.md\b|\.md-|\.team-arte__pre/.test(sel))
        out.push(rule.cssText);
    }
  }
  return out.join("\n");
}

/** Wrap a rendered artefact body in a full HTML document.
 *  `title` and `meta` are inserted as text, never as markup. */
export function artefactDocument(title: string, meta: string, bodyHtml: string): string {
  const esc = (s: string) =>
    s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)}</title>
<style>
${documentCss()}
/* The document's own frame. Deliberately narrow: an artefact is prose, and
   prose past ~80 characters a line stops being readable. */
html { color-scheme: dark; }
body {
  margin: 0;
  background: var(--color-terminal-bg, #1f1f1f);
  color: var(--color-text-primary, rgba(255,255,255,0.92));
  /* Inter is served from the app's virtual host, which a file:// document
     can't reach, so the exported page lands on the Segoe fallback. That's the
     chain the constitution already specifies for exactly this case. */
  font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
  font-size: 14px;
  line-height: 1.5;
}
.arte-page { max-width: 76ch; margin: 0 auto; padding: 32px 24px 48px; }
.arte-page__title { font-size: 20px; font-weight: 600; line-height: 1.2; margin: 0 0 4px; }
.arte-page__meta { font-size: 12px; color: var(--color-text-tertiary, rgba(255,255,255,0.55)); margin: 0 0 24px; }
</style>
</head>
<body>
<main class="arte-page">
<h1 class="arte-page__title">${esc(title)}</h1>
<p class="arte-page__meta">${esc(meta)}</p>
${bodyHtml}
</main>
</body>
</html>
`;
}
