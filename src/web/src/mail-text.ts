// Turns an email's plain-text body (what the Gmail export saves) into pieces
// a reader can draw: text, links and pictures. Gmail's plain body writes every
// link and image of the designed email as its address in brackets after its
// label — "View work item\n[https://…]", "Jira logo [https://…/logo.png]" —
// which read as a wall of URLs. Here a bracketed address becomes a link on its
// label (or a picture, with the label as its description), bare addresses
// become short links, character codes (&#x2192;) are decoded, and runs of
// blank lines collapse. Pure, so it is tested.

export type MailSeg =
  | { kind: "text"; text: string }
  | { kind: "link"; text: string; href: string }
  | { kind: "image"; src: string; alt: string; href?: string };

/** A person's picture (drawn small and round, not at its full 256px). */
export function isAvatarUrl(src: string): boolean {
  return /gravatar\.com|\/avatar/i.test(src);
}

const IMAGE_RX = /\.(png|jpe?g|gif|webp|svg|bmp)(\?|#|$)/i;
/** Hosts that serve pictures without a file extension (avatars). */
const IMAGE_HOSTS = /(^|\.)(gravatar\.com|googleusercontent\.com|avatar-management[^/]*)$/i;
/** A label this long isn't a label — it's the paragraph the link sits in. */
const MAX_LABEL = 90;

export function isImageUrl(href: string): boolean {
  try {
    const u = new URL(href);
    return IMAGE_RX.test(u.pathname) || IMAGE_HOSTS.test(u.hostname) || /\/avatar\//.test(u.pathname);
  } catch { return false; }
}

const NAMED: Record<string, string> = { amp: "&", lt: "<", gt: ">", quot: "\"", apos: "'", nbsp: " ", zwnj: "", zwj: "", copy: "©", reg: "®", hellip: "…", mdash: "—", ndash: "–", rarr: "→", larr: "←", bull: "•" };

export function decodeEntities(s: string): string {
  return s.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e: string) => {
    if (e[0] === "#") {
      const n = e[1] === "x" || e[1] === "X" ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      return Number.isFinite(n) && n > 0 && n < 0x110000 ? String.fromCodePoint(n) : m;
    }
    return NAMED[e.toLowerCase()] ?? m;
  });
}

/** "personalyrtb.atlassian.net/browse/PK-7256" — no scheme, no query, cut. */
export function shortUrl(href: string): string {
  try {
    const u = new URL(href);
    const path = u.pathname === "/" ? "" : u.pathname;
    const s = u.hostname.replace(/^www\./, "") + path;
    return s.length > 48 ? s.slice(0, 47) + "…" : s;
  } catch { return href.length > 48 ? href.slice(0, 47) + "…" : href; }
}

// A bracketed or angled address (Gmail wraps long ones across lines), or a
// bare one.
const URL_RX = /\[(https?:\/\/[^\]]+)\]|<(https?:\/\/[^>\s]+)>|(https?:\/\/[^\s<>"\]\[)]+[^\s<>"\]\[).,;:!?'])/g;

export function parseMailText(body: string): MailSeg[] {
  const src = decodeEntities(body).replace(/\r\n/g, "\n").replace(/[ \t]+\n/g, "\n").replace(/\n{3,}/g, "\n\n");
  const out: MailSeg[] = [];
  const pushText = (t: string) => {
    if (!t) return;
    const last = out[out.length - 1];
    if (last?.kind === "text") last.text += t; else out.push({ kind: "text", text: t });
  };
  let at = 0;
  for (let m = URL_RX.exec(src); m; m = URL_RX.exec(src)) {
    const bracketed = m[1] !== undefined || m[2] !== undefined;
    const href = (m[1] ?? m[2] ?? m[3]).replace(/\s+/g, "");
    let before = src.slice(at, m.index);
    at = m.index + m[0].length;
    // The label is the text just before a bracketed address: the rest of its
    // line, or — when the address starts a line — the line above.
    let label = "";
    if (bracketed) {
      const nl = before.lastIndexOf("\n");
      const tail = before.slice(nl + 1);
      if (tail.trim() && tail.trim().length <= MAX_LABEL) {
        label = tail.trim();
        before = before.slice(0, nl + 1) + tail.slice(0, tail.length - tail.trimStart().length);
      } else if (!tail.trim() && nl >= 0) {
        const prevNl = before.lastIndexOf("\n", nl - 1);
        const prev = before.slice(prevNl + 1, nl);
        if (prev.trim() && prev.trim().length <= MAX_LABEL) {
          label = prev.trim();
          before = before.slice(0, prevNl + 1);
        }
      }
      // "Label / PK-7256" style: a separator isn't part of the next label —
      // it stays as text between the two links.
      const sep = /^([/|·•-])\s*/.exec(label);
      if (sep) { before += ` ${sep[1]} `; label = label.slice(sep[0].length); }
    }
    // Two things that met where an address was cut out get a space between.
    const prev = out[out.length - 1];
    if (!before && prev && prev.kind !== "text" && label) before = " ";
    pushText(before);
    // A link right after a picture (an app-store badge and its target) makes
    // the picture clickable instead of printing a tracking address beside it.
    if (!before && prev?.kind === "image" && !prev.href && !isImageUrl(href) && !label) { prev.href = href; continue; }
    if (isImageUrl(href)) out.push({ kind: "image", src: href, alt: label });
    else out.push({ kind: "link", text: label || shortUrl(href), href });
  }
  pushText(src.slice(at));
  // Trim the blank edges the removed addresses leave behind.
  const first = out[0], last = out[out.length - 1];
  if (first?.kind === "text") first.text = first.text.replace(/^\s+/, "");
  if (last?.kind === "text") last.text = last.text.replace(/\s+$/, "");
  return out.filter((s) => s.kind !== "text" || s.text.length > 0);
}
