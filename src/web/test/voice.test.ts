// Dictation's pure rules: who gets an Enter, what the words are tagged with,
// and the clip the host's Whisper receives.
//
// The one rule that must never break is the shell: a misheard sentence sent
// to Claude costs a correction, a misheard command run in a shell can do
// damage — so a plain shell never gets an Enter, whatever the press.

import { test } from "node:test";
import assert from "node:assert/strict";
import { voiceText, voiceSubmits, toPcm16, VOICE_TAG } from "../src/voice.js";

test("an agent and the project chat get the words submitted; a shell never does", () => {
  assert.equal(voiceSubmits("agent", false), true);
  assert.equal(voiceSubmits("chat", false), true);
  assert.equal(voiceSubmits("shell", false), false);
});

test("Esc keeps the words as a draft everywhere", () => {
  assert.equal(voiceSubmits("agent", true), false);
  assert.equal(voiceSubmits("chat", true), false);
  assert.equal(voiceSubmits("shell", true), false);
});

test("an agent is told the words were spoken; a shell command stays bare", () => {
  assert.equal(voiceText("  commit it but don't push ", "agent"), `commit it but don't push ${VOICE_TAG}`);
  assert.equal(voiceText("move the card", "chat"), `move the card ${VOICE_TAG}`);
  assert.equal(voiceText(" git status ", "shell"), "git status");
});

test("a 48 kHz clip becomes 16 kHz PCM16 little-endian, a third as many samples", () => {
  const rate = 48000;
  const samples = new Float32Array(rate);   // one second
  for (let i = 0; i < samples.length; i++) samples[i] = 0.5;
  const bytes = toPcm16(samples, rate);
  assert.equal(bytes.length, 16000 * 2);
  const first = bytes[0] | (bytes[1] << 8);
  assert.equal(first, Math.round(0.5 * 32767));
});

test("the resampler clamps out-of-range samples instead of wrapping", () => {
  const bytes = toPcm16(new Float32Array([2, 2, 2, -2, -2, -2]), 48000);
  const view = new DataView(bytes.buffer);
  assert.equal(view.getInt16(0, true), 32767);
  assert.equal(view.getInt16(2, true), -32768);
});
