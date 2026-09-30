// Settings → Phone: the pairing QR code the Perch iPhone app scans. The host
// (PhoneServer) does the serving; this only draws what the phone needs to
// find it and prove it was paired: the LAN addresses, the port and the token.
// The link format is part of the phone protocol (docs/PHONE-API.md).

import qrcode from "qrcode-generator";
import type { PhoneInfoMessage } from "./bridge.js";

/** The link the QR code carries. Pure. */
export function pairingUrl(info: Pick<PhoneInfoMessage, "name" | "port" | "token" | "hosts">): string {
  const q = new URLSearchParams({
    v: "1",
    name: info.name,
    port: String(info.port),
    token: info.token,
    hosts: info.hosts.join(","),
  });
  return `perch://pair?${q.toString()}`;
}

/** `text` as a QR code: an SVG of square modules with a four-module quiet
 *  zone, colored by the --qr-* tokens. */
export function qrSvg(text: string): SVGSVGElement {
  const qr = qrcode(0, "M");
  qr.addData(text);
  qr.make();
  const n = qr.getModuleCount();
  const size = n + 8;
  const NS = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(NS, "svg");
  svg.setAttribute("viewBox", `0 0 ${size} ${size}`);
  svg.setAttribute("shape-rendering", "crispEdges");
  svg.setAttribute("class", "phone-pair__qr");
  svg.setAttribute("role", "img");
  svg.setAttribute("aria-label", "Pairing QR code");
  let d = "";
  for (let r = 0; r < n; r++)
    for (let c = 0; c < n; c++)
      if (qr.isDark(r, c)) d += `M${c + 4} ${r + 4}h1v1h-1z`;
  const bg = document.createElementNS(NS, "rect");
  bg.setAttribute("width", String(size));
  bg.setAttribute("height", String(size));
  bg.setAttribute("class", "phone-pair__qr-paper");
  const ink = document.createElementNS(NS, "path");
  ink.setAttribute("d", d);
  ink.setAttribute("class", "phone-pair__qr-ink");
  svg.append(bg, ink);
  return svg;
}

/** Fill Settings → Phone's pairing area from the host's answer. */
export function renderPhonePair(el: HTMLElement, info: PhoneInfoMessage, onNewCode: () => void): void {
  el.replaceChildren();
  if (!info.enabled) return;
  if (!info.listening) {
    const err = document.createElement("p");
    err.className = "phone-pair__error";
    err.textContent = info.error || "Perch couldn't start listening for your phone.";
    el.appendChild(err);
    return;
  }
  if (info.hosts.length === 0) {
    const err = document.createElement("p");
    err.className = "phone-pair__error";
    err.textContent = "This computer isn't on a network your phone can reach.";
    el.appendChild(err);
    return;
  }
  el.appendChild(qrSvg(pairingUrl(info)));

  const text = document.createElement("div");
  text.className = "phone-pair__text";
  const how = document.createElement("p");
  how.className = "phone-pair__how";
  how.textContent = "Open the Perch app on your iPhone and scan this code.";
  const where = document.createElement("p");
  where.className = "phone-pair__where";
  where.textContent = `${info.name} · ${info.hosts[0]}:${info.port}`;
  const newCode = document.createElement("button");
  newCode.type = "button";
  newCode.className = "settings-btn settings-btn--subtle";
  newCode.textContent = "New code";
  newCode.title = "Unpairs every phone; each one scans again";
  newCode.addEventListener("click", onNewCode);
  text.append(how, where, newCode);
  el.appendChild(text);
}
