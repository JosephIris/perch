// What the dashboard shows, one card per project: its project chats with their
// threads rolled up, its other sessions, its email tabs — and how much of it
// needs you. Pure (no DOM) so the filing and the ordering are tested.

import type { SessionView, ProjectView } from "./bridge.js";
import { groupOf, type ThreadGroup } from "./thread-ui.js";
import { groupByProject, isMailTab } from "./sidebar.js";

export type ChatBlock = {
  chat: SessionView;
  /** Open (unresolved) threads by group. */
  counts: Record<Exclude<ThreadGroup, "resolved">, number>;
  /** Threads to name on the card: waiting on you first, then ready to merge. */
  attention: SessionView[];
};

export type ProjectCard = {
  id: string;
  name: string;
  /** Things blocked on you: sessions and threads waiting or asking permission. */
  needs: number;
  working: number;
  /** Threads with work to merge, sessions with commits to push. */
  ready: number;
  chats: ChatBlock[];
  sessions: SessionView[];
  email: SessionView[];
  /** Tabs asleep (dormant), counted, not listed. */
  asleep: number;
};

const asks = (s: SessionView) => s.agentState === "waiting" || s.agentState === "permission";

export function sessionNeedsYou(s: SessionView): boolean {
  return !s.dormant && asks(s);
}

function card(id: string, name: string, tabs: SessionView[]): ProjectCard {
  const leads = tabs.filter((s) => s.isLead);
  const leadIds = new Set(leads.map((s) => s.id));
  const threads = tabs.filter((s) => s.threadOf && leadIds.has(s.threadOf));
  const rest = tabs.filter((s) => !s.isLead && !threads.includes(s));
  const awake = rest.filter((s) => !s.dormant);
  const email = awake.filter(isMailTab);
  const sessions = awake.filter((s) => !isMailTab(s));

  let needs = 0, working = 0, ready = 0;
  const chats: ChatBlock[] = leads.map((chat) => {
    const counts = { waiting: 0, working: 0, ready: 0, idle: 0 };
    const mine = threads.filter((t) => t.threadOf === chat.id);
    for (const t of mine) {
      const g = groupOf(t);
      if (g !== "resolved") counts[g]++;
    }
    const attention = [
      ...mine.filter((t) => groupOf(t) === "waiting"),
      ...mine.filter((t) => groupOf(t) === "ready"),
    ];
    needs += counts.waiting + (sessionNeedsYou(chat) ? 1 : 0);
    working += counts.working + (chat.agentState === "working" && !chat.dormant ? 1 : 0);
    ready += counts.ready;
    return { chat, counts, attention };
  });
  for (const s of [...sessions, ...email]) {
    if (sessionNeedsYou(s)) needs++;
    else if (s.agentState === "working") working++;
    if (s.ahead > 0) ready++;
  }
  return { id, name, needs, working, ready, chats, sessions, email, asleep: rest.length - awake.length };
}

function hasContent(c: ProjectCard): boolean {
  return c.chats.length + c.sessions.length + c.email.length > 0;
}

/** The cards, most urgent first: needs you, then working, then ready, then
 *  the rest in registration order. A hidden project shows only when it needs
 *  you; a project with nothing open is left out (counted in `quiet`). */
export function buildBoard(sessions: SessionView[], projects: ProjectView[]): { cards: ProjectCard[]; quiet: number } {
  const { groups, other } = groupByProject(sessions, projects);
  const all = groups.map((g) => ({ card: card(g.project.id, g.project.name, g.tabs), hidden: !!g.project.hidden }));
  if (other.length) all.push({ card: card("", "Other", other), hidden: false });
  const shown = all.filter((x) => hasContent(x.card) && (!x.hidden || x.card.needs > 0)).map((x) => x.card);
  const rank = (c: ProjectCard) => (c.needs > 0 ? 0 : c.working > 0 ? 1 : c.ready > 0 ? 2 : 3);
  const order = new Map(shown.map((c, i) => [c, i]));
  shown.sort((a, b) => rank(a) - rank(b) || (a.needs > 0 ? b.needs - a.needs : 0) || order.get(a)! - order.get(b)!);
  return { cards: shown, quiet: all.filter((x) => !x.hidden && !hasContent(x.card)).length };
}
