// Execute the real Workspace methods with inert imports and tiny stage doubles.
// This measures lifecycle/queue behavior, not native rendering or a live agent.
import { createRequire } from "node:module";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const requireWeb = createRequire(new URL("../../../src/web/package.json", import.meta.url));
const ts = requireWeb("typescript");
const source = readFileSync(new URL("../../../src/web/src/workspace.ts", import.meta.url), "utf8");
const compiled = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
}).outputText;
const sent = [];
const module = { exports: {} };
vm.runInNewContext(compiled, {
  module, exports: module.exports,
  require: () => ({ send: (msg) => sent.push(msg) }),
}, { filename: "workspace.review.cjs" });
const { Workspace } = module.exports;

const workspace = Object.create(Workspace.prototype);
workspace.stages = new Map();
workspace.pendingBytes = new Map();
workspace.emptyState = { hidden: true };

// R03: A host-started bot without a mounted stage queues output with no ACK.
const chunk = Buffer.alloc(8192, "x").toString("base64");
for (let i = 0; i < 32; i++) workspace.feed("cold-bot", chunk);
console.log(`R03 cold bot: queued bytes=${workspace.pendingBytes.get("cold-bot").length * 8192}, acknowledgements=${sent.filter(m => m.type === "pane.ack").length}`);
workspace.render([], null, null);
console.log(`R03 after removing all sessions: pending queues=${workspace.pendingBytes.size}`);

// R05/R06: Sleeping the last active tab keeps native/terminal resources mounted.
let disposed = 0;
let visibilityCalls = 0;
const stage = {
  container: { style: {}, remove() {} },
  panes: new Map([["pane", {
    dispose() { disposed++; },
    setVisible() { visibilityCalls++; },
  }]]),
};
workspace.stages.set("session", stage);
workspace.activeSessionId = "session";
workspace.render([{ id: "session", dormant: true }], null, null);
console.log(`R05 empty workspace: stage display=${stage.container.style.display}, native visibility calls=${visibilityCalls}`);
console.log(`R06 asleep tab: retained stages=${workspace.stages.size}, disposed panes=${disposed}`);
