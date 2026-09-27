// Pictures pasted into a project chat's box or a thread's go with the message
// as file references Claude opens. One line (a thread's message is typed into
// its terminal, where a newline sends early), and drawn back as chips.

import { test } from "node:test";
import assert from "node:assert/strict";
import { pastesImage, withImages, splitImages } from "../src/image-attach.js";

test("a screenshot pastes as a picture; a document's text + picture pastes as text", () => {
  assert.ok(pastesImage(["image/png"], false));
  assert.ok(!pastesImage(["text/plain", "image/png"], true));
  assert.ok(!pastesImage(["text/plain"], true));
  assert.ok(!pastesImage([], false));
});

test("the message names each picture, on one line", () => {
  const one = withImages("what's wrong here?", ["C:\\p\\images\\paste-1.png"]);
  assert.equal(one, "what's wrong here? [Image: C:\\p\\images\\paste-1.png] (open the image to see it).");
  assert.ok(!one.includes("\n"));
  assert.equal(withImages("", ["a.png", "b.png"]), "[Image: a.png] [Image: b.png] (open the images to see them).");
  assert.equal(withImages("just text", []), "just text");
});

test("a sent message splits back into its words and its pictures", () => {
  assert.deepEqual(splitImages(withImages("look", ["a.png", "b.png"])), { text: "look", images: ["a.png", "b.png"] });
  assert.deepEqual(splitImages(withImages("", ["a.png"])), { text: "", images: ["a.png"] });
  assert.deepEqual(splitImages("no pictures here"), { text: "no pictures here", images: [] });
});
