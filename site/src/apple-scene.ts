// A little brand moment for the Windows-first section: Monocle Guy eyeing an
// apple (a Mac stand-in) with visible disapproval. Reuses the real mascot rig
// for the bird; the apple, the furrowed brow, and the "nope" ring are drawn
// here and animated in CSS.

import { pose, buildScene, REST_T } from "../../src/web/src/setup-overlay.js";

export function buildAppleScene(): HTMLElement {
  const wrap = document.createElement("div");
  wrap.className = "apple-scene";

  // The mascot, cropped to a portrait facing right (toward the apple). Held at
  // its rest pose so the overlaid brow stays pinned to the head; the motion in
  // this scene comes from the furrowing brow + the bobbing apple.
  const scene = buildScene();
  scene.svg.setAttribute("viewBox", "126 60 150 98");
  scene.svg.classList.add("apple-scene__bird");
  scene.apply(pose(REST_T));
  wrap.appendChild(scene.svg);

  // The apple: a silver Mac-ish silhouette with a leaf and a bite.
  const apple = document.createElement("div");
  apple.className = "apple-scene__apple";
  apple.innerHTML = `<svg viewBox="0 0 72 78" width="58" height="63" role="img" aria-label="an apple">
    <path d="M36 22 C 23 9, 7 16, 9 33 C 11 51, 23 72, 36 72 C 49 72, 61 51, 63 33 C 65 16, 49 9, 36 22 Z" fill="#c4c8ce"/>
    <path d="M45 17 C 50 8, 59 6, 61 4 C 59 13, 53 19, 46 20 Z" fill="#9aa0a8"/>
    <path d="M36 22 C 36 17, 37 13, 39 10" stroke="#7c8189" stroke-width="2.6" fill="none" stroke-linecap="round"/>
    <path d="M63 33 C 56 30, 51 36, 53 43 C 55 49, 62 49, 63 41 Z" fill="#131316"/>
  </svg>`;
  wrap.appendChild(apple);

  // The furrowed brow (the frown), pinned over the monocle.
  const frown = document.createElement("div");
  frown.className = "apple-scene__frown";
  frown.innerHTML = `<svg viewBox="0 0 44 18" width="30" height="12"><path d="M5 13 C 14 4, 30 4, 39 13" stroke="var(--accent)" stroke-width="3.6" fill="none" stroke-linecap="round"/></svg>`;
  wrap.appendChild(frown);

  return wrap;
}
