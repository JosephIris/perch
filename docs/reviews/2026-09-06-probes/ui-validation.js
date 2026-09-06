(async () => {
  const original = structuredClone(window.__perch.state);
  const state = structuredClone(original);
  const background = structuredClone(state.sessions[0]);
  background.id = "9715c978-e548-4c40-80fb-afb336a9f20b";
  background.title = "Unopened background terminal";
  background.rootPane.paneId = "e7ee4bfc-471a-4f21-bdf5-810c345ecb57";
  state.sessions.push(background);
  const dispatch = data => chrome.webview.dispatchEvent(new MessageEvent("message", { data }));
  const before = window.__perchTerms.length;
  dispatch(state);
  const beforeOutput = window.__perchTerms.length;
  const packet = btoa("background output\r\n".repeat(400));
  for (let i = 0; i < 40; i++) dispatch({ type: "pane.out", paneId: background.rootPane.paneId, b64: packet });
  await new Promise(resolve => setTimeout(resolve, 800));
  const afterOutput = window.__perchTerms.length;
  const backgroundTerm = window.__perchTerms.find(t => t.buffer.active.getLine(0)?.translateToString().includes("background"));
  const consumed = !!backgroundTerm || window.__perchTerms.some(t => t.buffer.active.length > 1000);
  dispatch(original);
  await new Promise(resolve => setTimeout(resolve, 100));
  const afterClose = window.__perchTerms.length;
  document.documentElement.style.background = "#181818";
  return { before, beforeOutput, afterOutput, consumed, afterClose,
    font: window.__perchTerms[0].options.fontFamily,
    passed: beforeOutput === before && afterOutput === before + 1 && consumed && afterClose === before };
})()
