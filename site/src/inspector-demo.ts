// The Inspector journal, hand-rebuilt because the app's builders aren't
// exported (they're welded to the IPC layer), but the markup + classes here
// are the app's real ones (turn-prompt / beat / work / imgrow / changes /
// inspector__filter) so the app's real style.css styles it pixel-for-pixel.
//
// Exposed as building blocks so both the static feature card (buildInspector)
// and the animated hero (which appends rows over time) can share them.

export type Row =
  | { kind: "prompt"; text: string }
  | { kind: "beat"; time: string; text: string }
  | { kind: "work"; time: string; verb: string; target: string; note?: string; repeat?: number }
  | { kind: "image"; time: string; svg: string };

export type ChangeFile = { name: string; dir: string; add: number; del: number };

// Fixture story = the storefront "holiday banner" redesign from the product.
export const STORY: Row[] = [
  { kind: "prompt", text: "make the holiday banner match the rest of the shop, and show me before and after" },
  { kind: "beat", time: "00:15", text: "I'll capture the current banner first, then restyle it against the shop tokens." },
  { kind: "work", time: "00:15", verb: "Bash", target: "scripts/capture.mjs", note: "banner-before.png" },
  { kind: "image", time: "00:15", svg: bannerBefore() },
  { kind: "work", time: "00:16", verb: "Read", target: "src/styles/tokens.css", note: "41 lines" },
  { kind: "work", time: "00:16", verb: "Update", target: "src/banner.css", repeat: 2 },
  { kind: "work", time: "00:17", verb: "Update", target: "src/banner.ts", note: "+6 −2" },
  { kind: "work", time: "00:17", verb: "Bash", target: "scripts/capture.mjs", note: "banner-after.png" },
  { kind: "image", time: "00:18", svg: bannerAfter() },
  { kind: "beat", time: "00:19", text: "The gradient is gone and the headline now uses the shop serif. The red stays on the call to action only." },
];

export const CHANGES: ChangeFile[] = [
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

/** Inline bold / code markup into the app's beat spans. */
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

export function rowEl(r: Row): HTMLElement {
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

export type InspectorShell = {
  rail: HTMLElement;
  stream: HTMLElement;
  /** Fill/replace the file-change strip. */
  setChanges(files: ChangeFile[], open: boolean): void;
};

/** The inspector chrome (header, empty changes strip, empty stream, wired
 *  filter bar, vitals). Rows get appended into `.stream` by the caller. */
export function buildInspectorShell(): InspectorShell {
  const rail = el("div", "inspector demo-inspector");

  const head = el("div", "demo-inspector__head");
  const dot = el("span", "demo-inspector__dot");
  dot.dataset.state = "working";
  head.appendChild(dot);
  head.appendChild(el("span", "demo-inspector__title", "holiday banner"));
  rail.appendChild(head);

  const bar = el("button", "changes__bar");
  bar.setAttribute("aria-expanded", "false");
  bar.appendChild(el("span", "changes__caret", "▶"));
  bar.appendChild(el("span", "changes__label", "0 files changed"));
  const loc = el("span", "changes__loc");
  bar.appendChild(loc);
  rail.appendChild(bar);
  const body = el("div", "changes__body");
  rail.appendChild(body);

  const setChanges = (files: ChangeFile[], open: boolean) => {
    const total = files.reduce((a, c) => ({ add: a.add + c.add, del: a.del + c.del }), { add: 0, del: 0 });
    const label = bar.querySelector(".changes__label")!;
    label.textContent = `${files.length} file${files.length === 1 ? "" : "s"} changed`;
    loc.replaceChildren();
    if (files.length) {
      loc.appendChild(el("span", "diff-add", `+${total.add}`));
      loc.append(" ");
      loc.appendChild(el("span", "diff-del", `−${total.del}`));
    }
    bar.setAttribute("aria-expanded", String(open));
    body.replaceChildren();
    for (const c of files) {
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
  };

  const stream = el("div", "inspector__stream demo-inspector__stream");
  rail.appendChild(stream);

  // Filter bar (wired to the stream via CSS class toggles, so it keeps working
  // as rows are appended later).
  const filters = el("div", "inspector__filters");
  const allBtn = filterChip("all", "All", null, "inspector__filter--all");
  filters.appendChild(allBtn);
  const chips: Array<{ cat: string; btn: HTMLElement }> = [];
  const defs: Array<[string, string, string]> = [
    ["user", "You", ""],
    ["claude", "Claude", ""],
    ["actions", "Actions", ""],
    ["skill", "Skills", "inspector__filter--skill"],
    ["images", "Images", "inspector__filter--images"],
  ];
  for (const [cat, label, extra] of defs) {
    const btn = filterChip(cat, label, 0, extra);
    filters.appendChild(btn);
    chips.push({ cat, btn });
  }
  rail.appendChild(filters);

  const recount = () => {
    const n = (sel: string) => stream.querySelectorAll(sel).length;
    const map: Record<string, number> = {
      user: n(".turn-prompt") + n(".turn-interrupt"),
      claude: n(".beat"),
      actions: n(".work"),
      skill: n(".skill"),
      images: n(".imgrow"),
    };
    for (const { cat, btn } of chips) {
      const c = btn.querySelector(".inspector__filter-count")!;
      c.textContent = String(map[cat]);
      c.classList.toggle("inspector__filter-count--zero", map[cat] === 0);
    }
  };
  const sync = () => {
    for (const { cat, btn } of chips)
      stream.classList.toggle(`inspector__stream--hide-${cat}`, btn.getAttribute("aria-pressed") !== "true");
    allBtn.setAttribute("aria-pressed", String(chips.every((c) => c.btn.getAttribute("aria-pressed") === "true")));
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
  // Recount whenever the stream mutates (rows appended during the animation).
  new MutationObserver(recount).observe(stream, { childList: true });

  const vitals = el("div", "demo-inspector__vitals");
  vitals.appendChild(el("span", "demo-inspector__model", "opus"));
  vitals.appendChild(el("span", "demo-inspector__cost", "≈$1.87"));
  const ctx = el("span", "ctx-bar");
  const fill = el("span", "ctx-bar__fill");
  fill.style.width = "31%";
  ctx.appendChild(fill);
  vitals.appendChild(ctx);
  rail.appendChild(vitals);

  return { rail, stream, setChanges };
}

/** Static inspector for the feature card: shell + the full story + changes. */
export function buildInspector(opts: { changesOpen?: boolean } = {}): HTMLElement {
  const sh = buildInspectorShell();
  sh.setChanges(CHANGES, opts.changesOpen ?? true);
  for (const r of STORY) sh.stream.appendChild(rowEl(r));
  return sh.rail;
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
export function bannerBefore(): string {
  return `<svg viewBox="0 0 640 200" width="164" height="51" role="img" aria-label="Before: gradient holiday banner">
    <defs><linearGradient id="hb-g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#c026d3"/><stop offset="1" stop-color="#e11d74"/></linearGradient></defs>
    <rect width="640" height="200" fill="url(#hb-g)"/>
    <text x="48" y="104" font-family="Arial, sans-serif" font-weight="800" font-size="52" fill="#fff">HOLIDAY SALE</text>
    <text x="50" y="140" font-family="Arial, sans-serif" font-size="22" fill="#fbe8f4">UP TO 70% OFF EVERYTHING</text>
  </svg>`;
}
export function bannerAfter(): string {
  return `<svg viewBox="0 0 640 200" width="164" height="51" role="img" aria-label="After: shop-serif holiday banner">
    <rect width="640" height="200" fill="#f6f1e8"/>
    <text x="48" y="92" font-family="Georgia, serif" font-size="46" fill="#1e2a3a">The Holiday Shop</text>
    <text x="50" y="124" font-family="Georgia, serif" font-size="19" fill="#5b6470">Considered gifts, wrapped and shipped by the 22nd</text>
    <rect x="48" y="140" width="120" height="36" rx="6" fill="#b4372f"/>
    <text x="70" y="164" font-family="Arial, sans-serif" font-size="16" fill="#fff">Shop gifts</text>
  </svg>`;
}
