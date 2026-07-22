// The Inspector journal — hand-rebuilt because the app's builders aren't
// exported (they're welded to the IPC layer), but the markup + classes here
// are the app's real ones (turn-prompt / beat / work / imgrow / changes /
// inspector__filter…) so the app's real style.css styles it pixel-for-pixel.
//
// Fixture story = the storefront "holiday banner" redesign from the product
// screenshot. Filters are live: click a chip to toggle its kind, matching the
// app's applyFilters (CSS class toggles, no re-render).

type Row =
  | { kind: "prompt"; text: string }
  | { kind: "beat"; time: string; text: string }
  | { kind: "work"; time: string; verb: string; target: string; note?: string; repeat?: number }
  | { kind: "image"; time: string; svg: string };

const STORY: Row[] = [
  { kind: "prompt", text: "make the holiday banner match the rest of the shop, and show me before and after" },
  { kind: "beat", time: "00:15", text: "I'll capture the current banner first, then restyle it against the shop tokens." },
  { kind: "work", time: "00:15", verb: "Bash", target: "scripts/capture.mjs", note: "banner-before.png" },
  { kind: "image", time: "00:15", svg: bannerBefore() },
  { kind: "work", time: "00:16", verb: "Read", target: "src/styles/tokens.css", note: "41 lines" },
  { kind: "work", time: "00:16", verb: "Update", target: "src/banner.css", repeat: 2 },
  { kind: "work", time: "00:17", verb: "Update", target: "src/banner.ts", note: "+6 −2" },
  { kind: "work", time: "00:17", verb: "Bash", target: "scripts/capture.mjs", note: "banner-after.png" },
  { kind: "image", time: "00:18", svg: bannerAfter() },
  { kind: "beat", time: "00:19", text: "The gradient is gone and the headline now uses the shop serif. The red stays on the call-to-action only." },
  { kind: "prompt", text: "love it. tighten the mobile crop a little" },
  { kind: "work", time: "00:21", verb: "Update", target: "src/banner.css", note: "+3 −1" },
];

// Changes strip: three files, aggregate +46 −23.
const CHANGES: Array<{ name: string; dir: string; add: number; del: number }> = [
  { name: "banner.css", dir: "src", add: 15, del: 9 },
  { name: "banner.ts", dir: "src", add: 6, del: 2 },
  { name: "tokens.css", dir: "src/styles", add: 25, del: 12 },
];

const el = (tag: string, cls = "", text = ""): HTMLElement => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text) e.textContent = text;
  return e;
};

/** Inline **bold** / `code` → the app's beat__strong / beat__code spans. */
function inlineInto(host: HTMLElement, text: string): void {
  const re = /\*\*(.+?)\*\*|`(.+?)`/g;
  let last = 0, m: RegExpExecArray | null;
  while ((m = re.exec(text))) {
    if (m.index > last) host.append(text.slice(last, m.index));
    if (m[1]) host.appendChild(el("strong", "beat__strong", m[1]));
    else host.appendChild(el("code", "beat__code", m[2]));
    last = re.lastIndex;
  }
  if (last < text.length) host.append(text.slice(last));
}

function rowEl(r: Row): HTMLElement {
  if (r.kind === "prompt") {
    const d = el("div", "turn-prompt turn-prompt--open");
    d.appendChild(el("span", "turn-prompt__caret", ">"));
    d.appendChild(el("span", "turn-prompt__text", r.text));
    return d;
  }
  if (r.kind === "beat") {
    const d = el("div", "beat beat--open");
    d.appendChild(el("span", "beat__time", r.time));
    const t = el("span", "beat__text");
    inlineInto(t, r.text);
    d.appendChild(t);
    return d;
  }
  if (r.kind === "image") {
    const d = el("div", "imgrow imgrow--shared");
    d.appendChild(el("span", "imgrow__time", r.time));
    const btn = el("button", "imgrow__thumb");
    btn.innerHTML = r.svg;
    d.appendChild(btn);
    return d;
  }
  // work
  const d = el("div", "work" + (r.repeat ? " work--repeat" : ""));
  d.appendChild(el("span", "work__time", r.time));
  d.appendChild(el("span", "work__rail", "│"));
  const what = el("span", "work__what");
  what.appendChild(el("span", "work__verb", r.verb));
  what.appendChild(el("span", "work__target", r.target));
  d.appendChild(what);
  if (r.repeat) d.appendChild(el("span", "work__repeat", `×${r.repeat}`));
  else if (r.note) d.appendChild(el("span", "work__note", r.note));
  return d;
}

/** Build the full inspector rail element (header, changes strip, stream, filter
 *  bar, vitals). Filters are wired live. Returns the rail.
 *  `changesOpen` expands the file-change strip (true for the roomy hero demo,
 *  false in the tight feature card so the stream has room for a banner). */
export function buildInspector(opts: { changesOpen?: boolean } = {}): HTMLElement {
  const changesOpen = opts.changesOpen ?? true;
  const rail = el("div", "inspector demo-inspector");

  // Header — the session's name + a chevron.
  const head = el("div", "demo-inspector__head");
  head.appendChild(dot("working"));
  head.appendChild(el("span", "demo-inspector__title", "holiday banner"));
  rail.appendChild(head);

  // Changes strip (expanded).
  const changesTotal = CHANGES.reduce(
    (a, c) => ({ add: a.add + c.add, del: a.del + c.del }), { add: 0, del: 0 });
  const bar = el("button", "changes__bar");
  bar.setAttribute("aria-expanded", String(changesOpen));
  bar.appendChild(el("span", "changes__caret", "▶"));
  bar.appendChild(el("span", "changes__label", `${CHANGES.length} files changed`));
  const loc = el("span", "changes__loc");
  loc.appendChild(el("span", "diff-add", `+${changesTotal.add}`));
  loc.append(" ");
  loc.appendChild(el("span", "diff-del", `−${changesTotal.del}`));
  bar.appendChild(loc);
  rail.appendChild(bar);
  const body = el("div", "changes__body");
  for (const c of CHANGES) {
    const fr = el("div", "file-row");
    fr.title = `${c.dir}/${c.name}`;
    fr.appendChild(el("span", "file-row__name", c.name));
    fr.appendChild(el("span", "file-row__dir", c.dir));
    const fl = el("span", "file-row__loc");
    fl.appendChild(el("span", "diff-add", `+${c.add}`));
    fl.append(" ");
    fl.appendChild(el("span", "diff-del", `−${c.del}`));
    fr.appendChild(fl);
    body.appendChild(fr);
  }
  rail.appendChild(body);

  // Stream.
  const stream = el("div", "inspector__stream demo-inspector__stream");
  for (const r of STORY) stream.appendChild(rowEl(r));
  rail.appendChild(stream);

  // Filter bar — counts computed from the story.
  const counts = {
    user: STORY.filter((r) => r.kind === "prompt").length,
    claude: STORY.filter((r) => r.kind === "beat").length,
    actions: STORY.filter((r) => r.kind === "work").length,
    skill: 0,
    images: STORY.filter((r) => r.kind === "image").length,
  };
  const filters = el("div", "inspector__filters");
  const allBtn = filterChip("all", "All", null, "inspector__filter--all");
  filters.appendChild(allBtn);
  const chips: Array<{ cat: string; btn: HTMLElement }> = [];
  const defs: Array<[string, string, number, string]> = [
    ["user", "You", counts.user, ""],
    ["claude", "Claude", counts.claude, ""],
    ["actions", "Actions", counts.actions, ""],
    ["skill", "Skills", counts.skill, "inspector__filter--skill"],
    ["images", "Images", counts.images, "inspector__filter--images"],
  ];
  for (const [cat, label, n, extra] of defs) {
    const btn = filterChip(cat, label, n, extra);
    filters.appendChild(btn);
    chips.push({ cat, btn });
  }
  rail.appendChild(filters);

  const sync = () => {
    for (const { cat, btn } of chips) {
      const on = btn.getAttribute("aria-pressed") === "true";
      stream.classList.toggle(`inspector__stream--hide-${cat}`, !on);
    }
    allBtn.setAttribute("aria-pressed",
      String(chips.every((c) => c.btn.getAttribute("aria-pressed") === "true")));
  };
  for (const { btn } of chips)
    btn.addEventListener("click", () => {
      btn.setAttribute("aria-pressed", String(btn.getAttribute("aria-pressed") !== "true"));
      sync();
    });
  allBtn.addEventListener("click", () => {
    const target = !chips.every((c) => c.btn.getAttribute("aria-pressed") === "true");
    for (const { btn } of chips) btn.setAttribute("aria-pressed", String(target));
    sync();
  });

  // Vitals footer.
  const vitals = el("div", "demo-inspector__vitals");
  vitals.appendChild(el("span", "demo-inspector__model", "opus"));
  vitals.appendChild(el("span", "demo-inspector__cost", "≈$1.87"));
  const ctx = el("span", "ctx-bar");
  const fill = el("span", "ctx-bar__fill");
  fill.style.width = "31%";
  ctx.appendChild(fill);
  vitals.appendChild(ctx);
  rail.appendChild(vitals);

  return rail;
}

function dot(state: string): HTMLElement {
  const s = el("span", "demo-inspector__dot");
  s.dataset.state = state;
  return s;
}

function filterChip(cat: string, label: string, n: number | null, extra: string): HTMLElement {
  const b = el("button", "inspector__filter" + (extra ? ` ${extra}` : ""));
  b.dataset.cat = cat;
  b.setAttribute("aria-pressed", "true");
  b.append(label);
  if (n != null) {
    const c = el("span", "inspector__filter-count" + (n === 0 ? " inspector__filter-count--zero" : ""), String(n));
    b.appendChild(c);
  }
  return b;
}

/* --- the two banner previews (before = gradient, after = shop serif) ----- */
function bannerBefore(): string {
  return `<svg viewBox="0 0 640 200" width="150" height="47" role="img" aria-label="Before: gradient holiday banner">
    <defs><linearGradient id="hb-g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#c026d3"/><stop offset="1" stop-color="#e11d74"/></linearGradient></defs>
    <rect width="640" height="200" fill="url(#hb-g)"/>
    <text x="48" y="104" font-family="Arial, sans-serif" font-weight="800" font-size="52" fill="#fff">HOLIDAY SALE</text>
    <text x="50" y="140" font-family="Arial, sans-serif" font-size="22" fill="#fbe8f4">UP TO 70% OFF EVERYTHING</text>
  </svg>`;
}
function bannerAfter(): string {
  return `<svg viewBox="0 0 640 200" width="150" height="47" role="img" aria-label="After: shop-serif holiday banner">
    <rect width="640" height="200" fill="#f6f1e8"/>
    <text x="48" y="92" font-family="Georgia, serif" font-size="46" fill="#1e2a3a">The Holiday Shop</text>
    <text x="50" y="124" font-family="Georgia, serif" font-size="19" fill="#5b6470">Considered gifts, wrapped and shipped by the 22nd</text>
    <rect x="48" y="140" width="120" height="36" rx="6" fill="#b4372f"/>
    <text x="70" y="164" font-family="Arial, sans-serif" font-size="16" fill="#fff">Shop gifts</text>
  </svg>`;
}
