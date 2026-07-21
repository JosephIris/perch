// The settings-page resident: Monocle Guy in a hard hat, balancing a wrench
// several sizes too big for the job on his nose. Decorative only; geometry
// approved via the icon-refresh mockup. Follows the setup-overlay art
// conventions: body reads currentColor, eye ink is the mascot's #15233b,
// accent pieces read var(--color-accent).

const NS = "http://www.w3.org/2000/svg";
const EYE = "#15233b";
const ACCENT = "var(--color-accent)";
const ACCENT_DEEP = "#4a86b8";
const STEEL = "#9aa6b2";

const BODY_D = "M19.9 10.0 C 19.5 7.9, 17.6 6.3, 15.4 6.5 C 13.9 6.5, 12.5 6.8, 11.4 7.4 C 9.4 7.6, 7.4 6.2, 5.7 6.7 C 5.1 6.9, 5.2 7.6, 5.9 8.1 C 7.0 8.9, 7.7 10.1, 8.5 11.1 C 9.6 12.7, 10.9 14.4, 12.6 14.4 C 14.0 14.4, 15.2 13.9, 16.1 13.0 C 17.1 12.4, 18.0 11.9, 18.6 11.2 C 19.1 10.9, 19.6 10.6, 19.9 10.0 Z";
const BEAK_D = "M17.4 9.4 L 21.9 10.3 L 18.2 11.35 Z";
/* brow pressed flat (the rig's focused position, r = -0.45) */
const BROW_D = "M15.9675 7.6375 C 16.5275 7.745, 17.2325 8.0175, 17.855 8.3825";

export function buildSettingsMascot(): SVGSVGElement {
  const el = <T extends SVGElement>(n: string, at: Record<string, string>): T => {
    const e = document.createElementNS(NS, n) as T;
    for (const k in at) e.setAttribute(k, at[k]);
    return e;
  };

  const svg = el<SVGSVGElement>("svg", {
    viewBox: "2.2 -6.4 22.6 22.9", "aria-hidden": "true",
  });

  /* ground shelf he stands on */
  svg.appendChild(el("path", { d: "M7 15.7 H 24", stroke: "currentColor",
    "stroke-width": ".28", "stroke-linecap": "round", opacity: ".45", fill: "none" }));

  /* legs planted, splayed a touch to brace */
  svg.appendChild(el("path", { d: "M11.3 14.1 V 15.7", stroke: "currentColor",
    "stroke-width": "1", "stroke-linecap": "round", fill: "none",
    transform: "rotate(-6 11.3 14.1)" }));
  svg.appendChild(el("path", { d: "M13.4 14.1 V 15.7", stroke: "currentColor",
    "stroke-width": "1", "stroke-linecap": "round", fill: "none",
    transform: "rotate(6 13.4 14.1)" }));

  /* the bird, pitched back into a circus chin-up; hat rides the head */
  const b = el<SVGGElement>("g", { transform: "rotate(-20 12.6 14.4)" });
  b.appendChild(el("path", { d: BODY_D, fill: "currentColor" }));
  b.appendChild(el("path", { d: BEAK_D, fill: "currentColor" }));
  b.appendChild(el("path", { d: BROW_D, fill: "none", stroke: EYE,
    "stroke-width": ".75", "stroke-linecap": "round" }));
  b.appendChild(el("circle", { cx: "17", cy: "8.6", r: ".95", fill: EYE })); // pupil up, on the wrench
  b.appendChild(el("circle", { cx: "17", cy: "9", r: "1.95", fill: "none",
    stroke: ACCENT, "stroke-width": ".6" }));
  b.appendChild(el("path", { d: "M17.6 10.8 C 17.8 11.7, 17.5 12.5, 16.9 13.1",
    fill: "none", stroke: ACCENT, "stroke-width": ".5", "stroke-linecap": "round" }));
  /* hard hat: tall rounded shell, crest ridge, full brim with a front peak */
  b.appendChild(el("path", {
    d: "M13.9 6.4 C 14.05 5.0, 15.1 4.15, 16.3 4.2 C 17.5 4.25, 18.45 5.1, 18.55 6.15 C 17.0 5.8, 15.4 6.0, 13.9 6.4 Z",
    fill: ACCENT }));
  b.appendChild(el("path", {
    d: "M15.5 4.35 C 15.9 4.08, 16.6 4.08, 17.0 4.3 L 16.95 4.65 C 16.55 4.44, 15.95 4.44, 15.55 4.7 Z",
    fill: ACCENT_DEEP }));
  b.appendChild(el("path", {
    d: "M13.1 6.7 C 15.2 6.1, 17.6 5.85, 19.75 6.1 C 19.95 6.3, 19.9 6.5, 19.7 6.6 C 17.5 6.35, 15.25 6.6, 13.35 7.15 C 13.1 7.05, 13.0 6.85, 13.1 6.7 Z",
    fill: ACCENT_DEEP }));
  svg.appendChild(b);

  /* the wrench, standing upright on the very tip of the beak: box end down
   * for a point contact, open-end head up top (a round head with a straight
   * slot masked through it at 15 degrees), a few degrees off plumb */
  const wr = el<SVGGElement>("g", { transform: "translate(19.94 7.3) rotate(5)" });
  wr.appendChild(el("circle", { cx: "0", cy: "-1.55", r: "1.15", fill: "none",
    stroke: STEEL, "stroke-width": ".8" }));
  wr.appendChild(el("rect", { x: "-0.42", y: "-10.4", width: ".84", height: "7.7", fill: STEEL }));
  const wmId = "settings-wrench-slot";
  const defs = el<SVGDefsElement>("defs", {});
  const mask = el<SVGMaskElement>("mask", { id: wmId, maskUnits: "userSpaceOnUse",
    x: "-4", y: "-14.5", width: "8", height: "16" });
  mask.appendChild(el("rect", { x: "-4", y: "-14.5", width: "8", height: "16", fill: "#fff" }));
  mask.appendChild(el("rect", { x: "-0.5", y: "-13.7", width: "1", height: "2.8", fill: "#000",
    transform: "rotate(-15 0 -11.15)" }));
  defs.appendChild(mask);
  wr.appendChild(defs);
  wr.appendChild(el("circle", { cx: "0", cy: "-11.15", r: "1.7", fill: STEEL,
    mask: `url(#${wmId})` }));
  svg.appendChild(wr);

  /* sway marks flanking the top: it's oscillating, not falling */
  svg.appendChild(el("path", { d: "M18.6 -4.6 Q 18.1 -3.6 18.5 -2.6",
    fill: "none", stroke: "currentColor", "stroke-width": ".25",
    "stroke-linecap": "round", opacity: ".45" }));
  svg.appendChild(el("path", { d: "M23.3 -4.9 Q 23.8 -3.9 23.4 -2.9",
    fill: "none", stroke: "currentColor", "stroke-width": ".25",
    "stroke-linecap": "round", opacity: ".45" }));

  /* one bead of sweat flying off the back of his head, in clear air */
  svg.appendChild(el("path", {
    d: "M9.7 5.6 C 9.28 6.4, 9.15 6.9, 9.5 7.2 C 9.88 7.46, 10.32 7.2, 10.32 6.7 C 10.32 6.26, 10.0 5.95, 9.7 5.6 Z",
    fill: ACCENT, opacity: ".85" }));

  return svg;
}
