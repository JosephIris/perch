// An email as designed (its HTML), drawn the way a mail client does: in a
// frame of its own, so its styles can't reach Perch's and Perch's can't
// flatten it. The frame is sandboxed with no scripts — an email's script
// never runs — and what could still act on its own (forms, embeds, meta
// refresh, event attributes, javascript: links) is stripped first. Links open
// in the browser; remote pictures load (the owner's choice); the quoted
// history of a reply folds behind a "···" like the plain view.
//
// Designed emails assume a white page, so the frame is one: a light "paper"
// card in the dark app, as Outlook and Apple Mail draw them.

import { send } from "./bridge.js";

const DROP = "script, iframe, frame, frameset, object, embed, applet, form, base, meta, link, noscript, audio, video";

/** The email's HTML with everything that could act on its own removed. */
export function sanitizeMailHtml(html: string): string {
  const doc = new DOMParser().parseFromString(html, "text/html");
  doc.querySelectorAll(DROP).forEach((n) => n.remove());
  for (const n of doc.querySelectorAll("*")) {
    for (const a of [...n.attributes]) {
      const name = a.name.toLowerCase();
      const v = a.value.trim().toLowerCase();
      if (name.startsWith("on")) n.removeAttribute(a.name);
      else if ((name === "href" || name === "src" || name === "action" || name === "xlink:href") && /^(javascript|vbscript|data:text\/html)/.test(v)) n.removeAttribute(a.name);
    }
  }
  return doc.body ? doc.body.innerHTML : "";
}

const FRAME_STYLE = `
  html { background: #fff; }
  body { margin: 0; padding: 20px 24px; color: #1f1f1f; background: #fff;
         font: 14px/1.5 "Segoe UI", -apple-system, "Helvetica Neue", Arial, sans-serif; overflow-wrap: anywhere; }
  img { max-width: 100%; height: auto; }
  table { max-width: 100% !important; }
  a { color: #0b61c3; }
  .gmail_quote, blockquote.gmail_quote { display: none; }
  body.show-quote .gmail_quote { display: block; }
`;

/** The frame for one message. `onQuote` gets whether it has quoted history. */
export function mailFrame(html: string): { frame: HTMLIFrameElement; toggleQuote: () => void; hasQuote: () => boolean } {
  const frame = document.createElement("iframe");
  frame.className = "mail__frame";
  // Same origin so Perch can size it and route its links; no scripts, so
  // nothing in the email runs.
  frame.setAttribute("sandbox", "allow-same-origin");
  frame.setAttribute("referrerpolicy", "no-referrer");
  frame.title = "Email";
  frame.srcdoc = `<!doctype html><html><head><meta charset="utf-8"><style>${FRAME_STYLE}</style></head><body>${sanitizeMailHtml(html)}</body></html>`;

  const fit = () => {
    const d = frame.contentDocument;
    if (d?.documentElement) frame.style.height = `${d.documentElement.scrollHeight}px`;
  };
  frame.addEventListener("load", () => {
    const d = frame.contentDocument;
    if (!d) return;
    // A picture that can't load leaves no broken-image mark behind.
    for (const img of d.querySelectorAll("img")) {
      if (img.complete && img.naturalWidth === 0) img.style.display = "none";
      else img.addEventListener("error", () => { img.style.display = "none"; }, { once: true });
    }
    fit();
    // Pictures arrive after load and grow the page.
    new ResizeObserver(fit).observe(d.documentElement);
    d.addEventListener("click", (ev) => {
      const a = (ev.target as Element | null)?.closest?.("a[href]") as HTMLAnchorElement | null;
      if (!a) return;
      ev.preventDefault();
      const href = a.getAttribute("href") ?? "";
      if (/^(https?:|mailto:)/i.test(href)) send({ type: "url.open", url: href });
    });
  });
  return {
    frame,
    toggleQuote: () => { frame.contentDocument?.body.classList.toggle("show-quote"); fit(); },
    hasQuote: () => !!frame.contentDocument?.querySelector(".gmail_quote"),
  };
}
