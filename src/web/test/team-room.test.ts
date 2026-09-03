// The team room's rendering decisions that don't need a DOM: which class a
// row wears, what the recipients strip says, what the avatar shows, when the
// New bot dialog may create, and the composer's chip/typeahead arithmetic.

import { test } from "node:test";
import assert from "node:assert/strict";
import { feedRowClass, avatarInitial, recipientsLabel, systemTone } from "../src/team-room.js";
import { removeMention, mentionCandidates } from "../src/mention-input.js";
import { canCreate, canGenerate } from "../src/new-bot-dialog.js";
import { articleFor } from "../src/bot-menu.js";
import type { FeedRow } from "../src/team.js";
import type { TeamEntryView } from "../src/bridge.js";

const entry = (over: Partial<TeamEntryView>): TeamEntryView =>
  ({ seq: 1, ts: "2026-09-02T10:00:00Z", kind: "beat", from: "Ada", text: "hi", ...over });
const rowOf = (e: TeamEntryView, cont = false): FeedRow => ({ kind: "entry", seq: e.seq, entry: e, cont });

test("feedRowClass: message kinds, continuation, your held/pending/failed posts", () => {
  assert.equal(feedRowClass(rowOf(entry({ kind: "beat" }))), "tf-msg tf-msg--beat");
  assert.equal(feedRowClass(rowOf(entry({ kind: "peer" }), true)), "tf-msg tf-msg--peer tf-msg--cont");
  assert.equal(feedRowClass(rowOf(entry({ kind: "note" }))), "tf-msg tf-msg--note");
  assert.equal(feedRowClass(rowOf(entry({ kind: "user", from: "you", delivered: false }))), "tf-msg tf-msg--user tf-msg--held");
  assert.equal(feedRowClass(rowOf(entry({ kind: "user", from: "you" })), true), "tf-msg tf-msg--user tf-msg--pending");
  assert.equal(feedRowClass(rowOf(entry({ kind: "user", from: "you" })), true, true), "tf-msg tf-msg--user tf-msg--pending tf-msg--failed");
  assert.equal(feedRowClass(rowOf(entry({ kind: "system", from: "perch" }))), "tf-sys");
  assert.equal(feedRowClass(rowOf(entry({ kind: "work" }))), "work");
  const fold: FeedRow = { kind: "workfold", seq: 3, from: "Ada", entries: [], summary: "read 2 files", cont: false };
  assert.equal(feedRowClass(fold), "tf-work");
});

test("avatarInitial: first character, upper-cased, never empty", () => {
  assert.equal(avatarInitial("ada"), "A");
  assert.equal(avatarInitial("  bo"), "B");
  assert.equal(avatarInitial("émile"), "É");
  assert.equal(avatarInitial(""), "?");
});

test("recipientsLabel: addressed, everyone — and naming nobody is everyone", () => {
  assert.equal(recipientsLabel(["Ada", "Bo"]), "to Ada, Bo");
  assert.equal(recipientsLabel("everyone"), "to everyone");
  assert.equal(recipientsLabel(undefined), "to everyone");
  assert.equal(recipientsLabel([]), "to everyone");
});

test("systemTone: attention for waiting/permission, error for error, calm otherwise", () => {
  assert.equal(systemTone("waiting"), "attention");
  assert.equal(systemTone("permission"), "attention");
  assert.equal(systemTone("error"), "error");
  assert.equal(systemTone("joined"), "calm");
  assert.equal(systemTone(undefined), "calm");
});

test("removeMention: drops the token and tidies the space it leaves", () => {
  assert.equal(removeMention("@Ada fix the row", "Ada"), "fix the row");
  assert.equal(removeMention("please @bo look", "Bo"), "please look");
  assert.equal(removeMention("@Ada @Bo hi", "Ada"), "@Bo hi");
  assert.equal(removeMention("mail a@b.com", "b"), "mail a@b.com");     // not a mention
  assert.equal(removeMention("@Adam is not @Ada", "Ada"), "@Adam is not");
});

test("mentionCandidates: prefix first, then contains, everyone only once you've started typing it", () => {
  const roster = ["Ada", "Bo", "Cy", "Dana"];
  assert.deepEqual(mentionCandidates("", roster), ["Ada", "Bo", "Cy", "Dana"]);
  assert.deepEqual(mentionCandidates("a", roster), ["Ada", "Dana"]);
  assert.deepEqual(mentionCandidates("d", roster), ["Dana", "Ada"]);
  assert.deepEqual(mentionCandidates("e", roster), ["everyone"]);
  assert.deepEqual(mentionCandidates("ev", roster), ["everyone"]);
  assert.deepEqual(mentionCandidates("zz", roster), []);
});

test("canCreate: a new position needs name, purpose, folder and a brief; existing needs a slug", () => {
  const base = { mode: "new" as const, nicknameError: null, positionName: "Frontend dev", purpose: "owns src/web", referencePath: "C:\\repo", brief: "## Role", positionSlug: null, generating: false };
  assert.equal(canCreate(base), true);
  assert.equal(canCreate({ ...base, brief: "  " }), false);
  assert.equal(canCreate({ ...base, purpose: "" }), false);
  assert.equal(canCreate({ ...base, nicknameError: "Give the bot a nickname" }), false);
  assert.equal(canCreate({ ...base, generating: true }), false);
  assert.equal(canCreate({ ...base, mode: "existing", brief: "", positionSlug: "frontend-dev" }), true);
  assert.equal(canCreate({ ...base, mode: "existing", positionSlug: null }), false);
});

test("canGenerate: new mode with the three inputs, never mid-run", () => {
  const base = { mode: "new" as const, positionName: "Analyst", purpose: "reads the data", referencePath: "C:\\repo", generating: false };
  assert.equal(canGenerate(base), true);
  assert.equal(canGenerate({ ...base, generating: true }), false);
  assert.equal(canGenerate({ ...base, mode: "existing" }), false);
  assert.equal(canGenerate({ ...base, referencePath: "" }), false);
});

test("articleFor: a Frontend dev, an Analyst", () => {
  assert.equal(articleFor("Frontend dev"), "a");
  assert.equal(articleFor("Analyst"), "an");
  assert.equal(articleFor(" engineer"), "an");
});
