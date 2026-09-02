// @mentions in the team room, as pure functions. Recipients are DERIVED from
// the text every time it changes — the chips above the composer are a view of
// what parseMentions found, never a second source of truth — so what you see
// addressed is exactly what gets addressed.
//
// A mention is `@` at the start of the text or after whitespace (or an opening
// bracket/quote), followed by a nickname. `a@b.com` is not a mention; neither
// is `@foo` when nobody on the team is called foo — those stay literal text.
// `@everyone` (and `@all`) address the whole team and win over any per-bot
// mention in the same post.

export type MentionTarget = string[] | "everyone" | null;

export type MentionToken =
  | { kind: "text"; text: string }
  | { kind: "mention"; text: string; nick: string }
  | { kind: "everyone"; text: string };

/* Lead-in group 1 is the boundary char (empty at start of text); group 2 the
 * bare token. A fresh RegExp per call — a shared /g regex carries lastIndex. */
const MENTION_SOURCE = String.raw`(^|[\s(\["'])@([A-Za-z0-9][A-Za-z0-9_-]*)`;

/* Mention-safe nicknames: what the mention regex can find, and what a Claude
 * Code session name can carry. 24 characters is plenty for a first name. */
const NICK_RE = /^[A-Za-z0-9][A-Za-z0-9_-]{0,23}$/;

/* Names the room uses for non-bots; a bot can't take them. */
const RESERVED = new Set(["everyone", "all", "you", "perch", "me"]);

/** Split text into literal runs and mention chips against a roster of
 *  nicknames. Matching is case-insensitive; the chip keeps the text as typed
 *  and carries the roster's own spelling in `nick`. */
export function tokenizeMentions(text: string, roster: string[]): MentionToken[] {
  const byLower = new Map<string, string>();
  for (const n of roster) byLower.set(n.toLowerCase(), n);
  const out: MentionToken[] = [];
  const re = new RegExp(MENTION_SOURCE, "g");
  let last = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    const start = m.index + m[1].length;
    const token = m[2];
    const lower = token.toLowerCase();
    const nick = byLower.get(lower);
    const everyone = nick === undefined && (lower === "everyone" || lower === "all");
    if (nick === undefined && !everyone) continue;   // literal; stays in the run
    if (start > last) out.push({ kind: "text", text: text.slice(last, start) });
    const raw = text.slice(start, start + 1 + token.length);
    out.push(everyone ? { kind: "everyone", text: raw } : { kind: "mention", text: raw, nick: nick! });
    last = start + 1 + token.length;
  }
  if (last < text.length) out.push({ kind: "text", text: text.slice(last) });
  return out;
}

/** Who a post is addressed to. `to` is the roster nicknames in the order first
 *  mentioned (deduped), "everyone" when the group was addressed, or null when
 *  nobody was — the host routes those. The text is returned untouched: the
 *  bots should see the mentions as written. */
export function parseMentions(text: string, roster: string[]): { to: MentionTarget; text: string } {
  const to: string[] = [];
  let everyone = false;
  for (const t of tokenizeMentions(text, roster)) {
    if (t.kind === "everyone") everyone = true;
    else if (t.kind === "mention" && !to.includes(t.nick)) to.push(t.nick);
  }
  return { to: everyone ? "everyone" : to.length > 0 ? to : null, text };
}

/** The mention being typed at the caret, for the typeahead: the index of its
 *  `@` and the letters after it (possibly none). Null when the caret isn't
 *  inside a mention token. */
export function mentionQueryAt(text: string, caret: number): { start: number; query: string } | null {
  const at = Math.max(0, Math.min(caret, text.length));
  const upto = text.slice(0, at);
  const m = /(^|[\s(\["'])@([A-Za-z0-9_-]*)$/.exec(upto);
  if (!m) return null;
  return { start: m.index + m[1].length, query: m[2] };
}

/** Why a nickname can't be used, in one plain sentence, or null when it can.
 *  `taken` is the project's existing nicknames (compared case-insensitively —
 *  "ada" and "Ada" would answer to the same session name). */
export function validateNickname(nick: string, taken: string[]): string | null {
  const n = nick.trim();
  if (n.length === 0) return "Give the bot a nickname";
  if (!NICK_RE.test(n)) return "Use letters, digits, - or _ (up to 24 characters), starting with a letter or digit";
  const lower = n.toLowerCase();
  if (RESERVED.has(lower)) return `${n} is a name the room already uses`;
  if (taken.some((t) => t.toLowerCase() === lower)) return `${n} is already on the team`;
  return null;
}
