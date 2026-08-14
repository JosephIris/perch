// Mini mode for the sidebar's Cloud + Local utility areas. Full mode stacks
// the two labeled cards; mini compresses each to a one-line chip and lays
// them side by side, filling the sidebar's width — the color grammar (teal /
// amber, caution washes) carries the state, so the chips stay honest about
// escalation even with the alert rows folded away.
//
// The chevron toggle rides the wrapper's top-right corner, which in full mode
// is the label row of whichever area is up; in mini it joins the chip row.
// Page-local (localStorage) like the projects fold — it's a per-view layout
// preference, and pushing it through the host would buy nothing.

const KEY = "perch.utility.mini";

export function initUtilityMini(): void {
  const wrap = document.getElementById("utility-areas");
  const toggle = document.getElementById("utility-mini-toggle");
  if (!wrap || !toggle) return;

  let mini = false;
  try {
    mini = localStorage.getItem(KEY) === "1";
  } catch {
    /* unavailable storage → start full */
  }

  const apply = () => {
    wrap.classList.toggle("utility-areas--mini", mini);
    const title = mini ? "Expand the cloud and local cards" : "Shrink the cloud and local cards";
    toggle.title = title;
    toggle.setAttribute("aria-label", title);
    toggle.setAttribute("aria-expanded", String(!mini));
  };

  toggle.addEventListener("click", () => {
    mini = !mini;
    try {
      localStorage.setItem(KEY, mini ? "1" : "0");
    } catch {
      /* best-effort */
    }
    apply();
  });
  apply();
}
