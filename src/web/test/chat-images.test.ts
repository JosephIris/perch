// A thread's screenshots, found where it names them — the shapes seen in real
// project chats: backticked paths, a list of paths, and bare names after one.

import { test } from "node:test";
import assert from "node:assert/strict";
import { findImagePaths } from "../src/chat-images.js";

test("backticked and plain absolute paths", () => {
  assert.deepEqual(findImagePaths("Screenshot: `C:/tmp/mag-top5.png`.\n\nTo look: `C:/tmp/a-top.png` and `C:/tmp/b.png`."),
    ["C:/tmp/mag-top5.png", "C:/tmp/a-top.png", "C:/tmp/b.png"]);
  assert.deepEqual(findImagePaths("saved to C:\\Users\\j\\shots\\x.jpg"), ["C:\\Users\\j\\shots\\x.jpg"]);
  assert.deepEqual(findImagePaths("mac: /Users/j/Desktop/shot.PNG done"), ["/Users/j/Desktop/shot.PNG"]);
});

test("bare names after a path are in its folder", () => {
  assert.deepEqual(findImagePaths("Screens: C:/tmp/acreal_56_similarity_head.png, acreal_56_similarity.png, acreal_57_note.png; URLs"),
    ["C:/tmp/acreal_56_similarity_head.png", "C:/tmp/acreal_56_similarity.png", "C:/tmp/acreal_57_note.png"]);
});

test("no folder, a web address, or no picture: nothing", () => {
  assert.deepEqual(findImagePaths("see logo.png"), []);
  assert.deepEqual(findImagePaths("https://cdn.example.com/a/b.png and http://localhost:5099/x.png"), []);
  assert.deepEqual(findImagePaths("C:/tmp/report.html and C:/tmp/data.csv"), []);
});

test("each once, at most eight", () => {
  assert.deepEqual(findImagePaths("C:/t/a.png then again C:/t/a.png"), ["C:/t/a.png"]);
  const many = Array.from({ length: 12 }, (_, i) => `C:/t/s${i}.png`).join(" ");
  assert.equal(findImagePaths(many).length, 8);
});
