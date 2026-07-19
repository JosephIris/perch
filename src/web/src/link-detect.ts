// Terminal link detection: web URLs (http/https) and references to local HTML
// files (file:// URLs, Windows drive/UNC paths, unix-absolute paths). Shared by
// pane.ts's always-underline byte injection (URL_RE) and the xterm link
// provider (findLinksInLine). Pure + unit-tested (test/link-detect.test.ts).

// URL regex — same as @xterm/addon-web-links's strictUrlRegex, copied so our
// custom provider doesn't depend on the addon at all. Matches http(s)://… up
// to the first whitespace / quote / disallowed-final.
//
// IMPORTANT: control bytes (\x00-\x1F including ESC) are excluded from BOTH
// char classes. Without this, the regex happily absorbs an ANSI escape
// sequence that immediately follows a URL ("http://x.com\x1b[0m"), causing
// injectUrlUnderlines to stick its \x1b[24m INSIDE that sequence.
export const URL_RE =
  /(https?|HTTPS?):[/]{2}[^\s\x00-\x1f"'!*(){}|\\\^<>`]*[^\s\x00-\x1f"':,.!?{}|\\\^~\[\]`()<>]/;

// A reference to a LOCAL HTML file, ending in .html/.htm. Only absolute /
// openable forms — file:// URLs, Windows drive paths, UNC, unix-absolute — so
// we never underline a bare "index.html" mentioned in prose, or a relative
// path we couldn't resolve without the pane's cwd.
// The trailing (?![A-Za-z0-9]) pins the extension to a real boundary, so
// "report.htmlx" doesn't match as "report.html".
export const HTML_FILE_RE = new RegExp(
  "(?:" +
    "file://[^\\s\"'`<>|]+\\.html?" + //     file:///C:/x.html
    "|~[\\\\/][^\\s\"'`<>|]*\\.html?" + //   ~\AppData\…\x.html (home-abbreviated)
    "|[A-Za-z]:[\\\\/][^\\s\"'`<>|]*\\.html?" + // C:\x\y.html or C:/x/y.html
    "|\\\\\\\\[^\\s\"'`<>|]*\\.html?" + //   \\host\share\x.html (UNC)
    "|/[^\\s\"'`<>|]*\\.html?" + //          /home/me/x.html (unix-absolute)
  ")(?![A-Za-z0-9])",
  "i"
);

// The user's home dir (%USERPROFILE%), pushed with every state message so a
// "~\…" token can be expanded into a real path. Empty until the first state.
let homeDir = "";
export function setHomeDir(dir: string): void {
  homeDir = dir ?? "";
}

export type LinkKind = "url" | "file";
export interface DetectedLink {
  /** 0-based char offset of the first char. */
  start: number;
  /** 0-based char offset one past the last char. */
  end: number;
  text: string;
  kind: LinkKind;
}

/** Find every web URL and local HTML-file reference in one line of terminal
 *  text, as 0-based [start, end) ranges, ordered by position. A file token that
 *  overlaps a URL match is dropped — it's that URL's path tail (e.g. the
 *  `/a.html` inside `http://x.com/a.html`), not a local file. */
export function findLinksInLine(text: string): DetectedLink[] {
  const urls: DetectedLink[] = [];
  const urlRe = new RegExp(URL_RE.source, "g");
  let m: RegExpExecArray | null;
  while ((m = urlRe.exec(text))) {
    urls.push({ start: m.index, end: m.index + m[0].length, text: m[0], kind: "url" });
  }

  const files: DetectedLink[] = [];
  const fileRe = new RegExp(HTML_FILE_RE.source, "gi");
  while ((m = fileRe.exec(text))) {
    const start = m.index;
    const end = start + m[0].length;
    if (urls.some((u) => start < u.end && end > u.start)) continue; // inside a URL
    files.push({ start, end, text: m[0], kind: "file" });
  }

  return [...urls, ...files].sort((a, b) => a.start - b.start);
}

/** Convert a detected local-file token to a navigable file:// URL. A token that
 *  is already a file:// (or http) URL passes through. A leading "~" is expanded
 *  to the home dir from the last state push (left as-is if none is known yet —
 *  the link still shows, it just can't resolve). */
export function htmlFileToUrl(token: string): string {
  let s = token.trim();
  if (/^(file|https?):\/\//i.test(s)) return s;
  if (s[0] === "~" && homeDir) {
    // "~\AppData\x.html" → "<home>\AppData\x.html"; normalize the join slash.
    s = homeDir.replace(/[\\/]+$/, "") + "\\" + s.slice(1).replace(/^[\\/]+/, "");
  }
  if (/^[A-Za-z]:[\\/]/.test(s)) return "file:///" + s.replace(/\\/g, "/"); // drive
  if (s.startsWith("\\\\")) return "file:" + s.replace(/\\/g, "/"); //          UNC
  return "file://" + s.replace(/\\/g, "/"); //          unix-absolute / unresolved ~
}
