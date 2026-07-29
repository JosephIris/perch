// What a browser (URL) pane is allowed to host — ONE definition, consumed by
// every page-side path that can create or navigate one.
//
// This exists because the rule used to live in two places that disagreed. The
// page happily handed the host a `file://…/report.html` pane URL (from a
// terminal link, or a C:\ path typed into the browser prompt) while
// UrlPaneController rejected everything but http/https — so the pane was
// created, reported its rect, and then just… sat there as an empty dark
// rectangle. No WebView2, no error, no log the user would ever see. A blank
// page with no way to tell it apart from a slow-loading site.
//
// The allowed set matches the host's `url.open` rule exactly (see
// WebUrlPolicy.cs, which mirrors this file and is pinned to it by
// test/fixtures/url-policy-cases.json):
//
//   - http / https                        — any web address
//   - file:// pointing at a .html/.htm    — the "agent wrote a report, open it"
//                                           flow; a local page in a WebView2 is
//                                           the same exposure as opening it in
//                                           the default browser, which we
//                                           already allow
//
// Everything else (about:, data:, javascript:, ftp:, mailto:, a file:// to an
// .exe/.ps1/a directory) is refused. Terminal output is attacker-influenced,
// so a crafted line must never be one click away from a scheme handler.

export type PaneUrlKind = "web" | "html-file";

/** Classify a URL for browser-pane hosting, or null if a pane must not host it.
 *  Pure + total: any string in, a decision out. */
export function paneUrlKind(url: string): PaneUrlKind | null {
  if (!url) return null;
  let u: URL;
  try {
    u = new URL(url);
  } catch {
    return null; // not absolute / unparseable
  }
  if (u.protocol === "http:" || u.protocol === "https:") return "web";
  if (u.protocol === "file:") {
    // Decode first: "%2Ereport%2Ehtml" and "report.html?x=1#y" must both land on
    // the same answer as the host's Uri.LocalPath-based check.
    let path: string;
    try {
      path = decodeURIComponent(u.pathname);
    } catch {
      return null; // malformed percent-escape
    }
    return /\.html?$/i.test(path) ? "html-file" : null;
  }
  return null;
}

/** True when a browser pane can actually display this URL. */
export function canOpenInPane(url: string): boolean {
  return paneUrlKind(url) !== null;
}
