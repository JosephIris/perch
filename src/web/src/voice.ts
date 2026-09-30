// Dictation. A mic button floats in a pane's corner (and above the project
// chat's composer), and a chord does the same from the keyboard:
//
//   hold (button or chord), talk, let go → the words go in. An agent pane
//     (Claude, Codex) and the project chat get them submitted, tagged
//     "[voice input]" so the model reads for meaning; a plain shell only ever
//     gets a draft — never an Enter.
//   tap → hands-free: it listens until you have been quiet for a moment,
//     shows a countdown on the scope, then sends. Tap again to send sooner.
//   Esc while talking → the words go in as a draft, whatever the pane.
//
// While it listens, the button grows an oscilloscope trace in the pane's
// color tag (the one retro surface in the app, on purpose). The page owns the
// microphone, the scope and where the words go; the host turns a clip into
// text with Whisper on this machine (VoiceController) and fetches the model
// on first use.

import { send, onMessage, chordMod, bytesToB64, modKeyLabel, type VoiceModelMessage } from "./bridge.js";
import { attachTooltip } from "./tooltip.js";

export type VoiceKind = "agent" | "shell" | "chat";

export type VoiceTarget = {
  /** Who is on the other end: decides whether the words are submitted. */
  kind(): VoiceKind;
  /** Put the words in; `submit` sends them (presses Enter). */
  deliver(text: string, submit: boolean): void;
};

export const VOICE_TAG = "[voice input]";
/** A press shorter than this is a tap: hands-free instead of push-to-talk. */
export const TAP_MS = 300;
/** Hands-free sends after this long quiet; the countdown shows after SHOW_QUIET_MS. */
export const QUIET_MS = 2500;
const SHOW_QUIET_MS = 1000;
/** Scope level (0–1) under which the room counts as quiet. */
const QUIET_LEVEL = 0.15;
/** The host transcribes at most 300 s; stop before that. */
const MAX_MS = 280_000;
const TARGET_RATE = 16000;
/** A clip shorter than this is a slip of the finger, not speech. */
const MIN_CLIP_S = 0.3;

type Phase = "idle" | "rec" | "transcribing" | "sending" | "note";
type How = "ptr" | "key";

/** What gets typed for `text`: an agent or the chat is told it was spoken. Pure. */
export function voiceText(text: string, kind: VoiceKind): string {
  const t = text.trim();
  return kind === "shell" ? t : `${t} ${VOICE_TAG}`;
}

/** Whether the words are submitted: never into a plain shell, never after Esc. Pure. */
export function voiceSubmits(kind: VoiceKind, draft: boolean): boolean {
  return !draft && kind !== "shell";
}

/** Mono float samples at `rate` → 16 kHz PCM16 little-endian, averaging each
 *  output sample's span (a cheap low-pass before decimating). Pure. */
export function toPcm16(samples: Float32Array, rate: number): Uint8Array {
  const ratio = rate / TARGET_RATE;
  const n = Math.floor(samples.length / ratio);
  const out = new Int16Array(n);
  for (let i = 0; i < n; i++) {
    const a = Math.floor(i * ratio);
    const b = Math.max(a + 1, Math.min(samples.length, Math.floor((i + 1) * ratio)));
    let sum = 0;
    for (let k = a; k < b; k++) sum += samples[k];
    const v = Math.max(-1, Math.min(1, sum / (b - a)));
    out[i] = v < 0 ? Math.round(v * 32768) : Math.round(v * 32767);
  }
  return new Uint8Array(out.buffer);
}

// ---- the microphone: one, shared, open only while a clip is recording ----

class Mic {
  private ctx: AudioContext | null = null;
  private stream: MediaStream | null = null;
  private nodes: AudioNode[] = [];
  private chunks: Float32Array[] = [];
  private opening: Promise<void> | null = null;
  analyser: AnalyserNode | null = null;
  readonly wave = new Float32Array(1024);

  open(): Promise<void> {
    this.chunks = [];
    this.opening = (async () => {
      this.ctx ??= new AudioContext();
      if (this.ctx.state === "suspended") await this.ctx.resume();
      this.stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
      });
      const src = this.ctx.createMediaStreamSource(this.stream);
      const analyser = this.ctx.createAnalyser();
      analyser.fftSize = 1024;
      // ScriptProcessor, not a worklet: no module file to serve, and it runs
      // the same in WebView2 and WKWebView. It must reach the destination to
      // be pulled; it writes nothing, so what it passes on is silence.
      const proc = this.ctx.createScriptProcessor(4096, 1, 1);
      proc.onaudioprocess = (e) => this.chunks.push(new Float32Array(e.inputBuffer.getChannelData(0)));
      src.connect(analyser);
      src.connect(proc);
      proc.connect(this.ctx.destination);
      this.nodes = [src, analyser, proc];
      this.analyser = analyser;
    })();
    return this.opening;
  }

  /** Scope level 0–1 and the trace's auto-gain peak, read into `wave`. */
  read(): { level: number; peak: number } {
    if (!this.analyser) { this.wave.fill(0); return { level: 0, peak: 0 }; }
    this.analyser.getFloatTimeDomainData(this.wave);
    let s = 0, peak = 0;
    for (const v of this.wave) { s += v * v; peak = Math.max(peak, Math.abs(v)); }
    return { level: Math.min(1, Math.pow(Math.sqrt(s / this.wave.length) * 7, 0.75)), peak };
  }

  /** Stop and hand back everything recorded (null when the mic never opened). */
  async close(): Promise<{ samples: Float32Array; rate: number } | null> {
    try { await this.opening; } catch { /* reported by the caller of open() */ }
    this.opening = null;
    for (const n of this.nodes) { try { n.disconnect(); } catch { /* already */ } }
    this.nodes = [];
    this.analyser = null;
    this.stream?.getTracks().forEach((t) => t.stop());
    this.stream = null;
    if (!this.ctx || this.chunks.length === 0) return null;
    const len = this.chunks.reduce((n, c) => n + c.length, 0);
    const samples = new Float32Array(len);
    let at = 0;
    for (const c of this.chunks) { samples.set(c, at); at += c.length; }
    this.chunks = [];
    return { samples, rate: this.ctx.sampleRate };
  }
}

const mic = new Mic();
const buttons = new Set<VoiceButton>();
const pending = new Map<string, VoiceButton>();
/** The one button recording or transcribing; a second press elsewhere waits. */
let active: VoiceButton | null = null;
let lastFocused: VoiceButton | null = null;
let model: VoiceModelMessage | null = null;

onMessage((msg) => {
  if (msg.type === "voice.model") model = msg;
  else if (msg.type === "voice.result") {
    const b = pending.get(msg.reqId);
    pending.delete(msg.reqId);
    b?.onResult(msg.text, msg.error);
  }
});

const MIC_SVG =
  '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
  '<rect x="8.5" y="3" width="7" height="11.5" rx="3.5"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21M9 21h6"/></svg>';
const STOP_SVG =
  '<svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true"><rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"/></svg>';

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`;
};

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** `#rrggbb` mixed toward white by k. Anything else comes back unchanged. */
function tint(hex: string, k: number): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!m) return hex;
  const n = parseInt(m[1], 16);
  const f = (c: number) => Math.round(c + (255 - c) * k);
  return `rgb(${f(n >> 16)},${f((n >> 8) & 255)},${f(n & 255)})`;
}

export class VoiceButton {
  readonly el: HTMLElement;
  private readonly btn: HTMLButtonElement;
  private readonly canvas: HTMLCanvasElement;
  private readonly g: CanvasRenderingContext2D;
  private readonly labelEl: HTMLElement;
  private readonly hintEl: HTMLElement;
  private phase: Phase = "idle";
  private phaseStart = 0;
  private recStart = 0;
  private quietSince = 0;
  private latched = false;
  private draft = false;
  private holding: How | null = null;
  private note = "";
  private phos = "#FFB000";
  private gain = 1;
  private readonly lastWave = new Float32Array(1024);
  private raf = 0;
  private dpr = 1;

  constructor(private readonly host: HTMLElement, private readonly target: VoiceTarget) {
    host.classList.add("voice-host");
    this.el = document.createElement("div");
    this.el.className = "voice";
    this.el.dataset.phase = "idle";

    const scope = document.createElement("div");
    scope.className = "voice__scope";
    this.canvas = document.createElement("canvas");
    this.g = this.canvas.getContext("2d")!;
    this.labelEl = document.createElement("span");
    this.labelEl.className = "voice__label";
    this.hintEl = document.createElement("span");
    this.hintEl.className = "voice__hint";
    scope.append(this.canvas, this.labelEl, this.hintEl);

    this.btn = document.createElement("button");
    this.btn.type = "button";
    this.btn.className = "voice__btn";
    this.btn.setAttribute("aria-label", "Dictate");
    this.btn.innerHTML = MIC_SVG;
    attachTooltip(this.btn, () => this.phase === "idle"
      ? `Hold to talk, let go to send (${modKeyLabel}+Shift+Space) · tap for hands-free`
      : "");
    this.el.append(scope, this.btn);
    host.appendChild(this.el);

    // Keep the terminal's (or composer's) focus: the words land where you were.
    this.btn.addEventListener("mousedown", (ev) => ev.preventDefault());
    this.btn.addEventListener("pointerdown", (ev) => {
      if (ev.button !== 0) return;
      // No preventDefault here: it would swallow the mousedown that keeps the
      // terminal focused (above) and dismisses the tooltip.
      try { this.btn.setPointerCapture(ev.pointerId); } catch { /* synthetic pointer */ }
      lastFocused = this;
      this.press("ptr");
    });
    this.btn.addEventListener("pointerup", () => this.release("ptr"));
    this.btn.addEventListener("pointercancel", () => this.release("ptr"));
    host.addEventListener("focusin", () => { lastFocused = this; });
    host.addEventListener("pointerdown", () => { lastFocused = this; });
    host.addEventListener("pointerenter", () => this.refreshColor());
    buttons.add(this);
    this.refreshColor();
  }

  dispose() {
    if (active === this) { void mic.close(); active = null; }
    for (const [id, b] of pending) if (b === this) pending.delete(id);
    if (lastFocused === this) lastFocused = null;
    cancelAnimationFrame(this.raf);
    buttons.delete(this);
    this.el.remove();
  }

  contains(node: Node | null): boolean { return !!node && this.host.contains(node); }
  get recording(): boolean { return this.phase === "rec"; }

  press(how: How) {
    if (this.phase === "idle") { this.holding = how; this.start(); }
    else if (this.phase === "rec" && this.latched) void this.stop(false);
  }

  release(how: How) {
    if (this.holding !== how) return;
    this.holding = null;
    if (this.phase !== "rec") return;
    if (performance.now() - this.recStart < TAP_MS) { this.latched = true; this.quietSince = performance.now(); }
    else void this.stop(false);
  }

  /** Esc: stop listening and keep the words as a draft. */
  keepAsDraft() {
    if (this.phase !== "rec") return;
    this.holding = null;
    void this.stop(true);
  }

  /** The pane's color tag (data-color on it or an ancestor), else amber. */
  private refreshColor() {
    const tagged = this.host.closest<HTMLElement>("[data-color]")?.dataset.color;
    const c = tagged != null ? cssVar(`--color-pane-tag-${tagged}`) : "";
    this.phos = c || cssVar("--voice-phosphor") || "#FFB000";
    this.el.style.setProperty("--voice-phos", this.phos);
  }

  private setPhase(p: Phase) {
    this.phase = p;
    this.phaseStart = performance.now();
    this.el.dataset.phase = p;
    this.btn.innerHTML = p === "rec" ? STOP_SVG : MIC_SVG;
    this.btn.setAttribute("aria-label", p === "rec" ? "Stop and send" : "Dictate");
    if (p === "idle") {
      if (active === this) active = null;
      cancelAnimationFrame(this.raf);
      this.raf = 0;
    } else if (!this.raf) {
      this.raf = requestAnimationFrame((t) => this.frame(t));
    }
  }

  private start() {
    if (active && active !== this) return;
    active = this;
    this.latched = false;
    this.draft = false;
    this.gain = 1;
    this.lastWave.fill(0);
    this.refreshColor();
    this.sizeCanvas();
    this.g.clearRect(0, 0, this.canvas.width, this.canvas.height);
    send({ type: "voice.prepare" });
    this.setPhase("rec");
    this.recStart = this.phaseStart;
    this.quietSince = this.recStart;
    mic.open().catch((err) => {
      console.warn("[voice] microphone:", err);
      if (this.phase === "rec") { void mic.close(); this.showNote("NO MICROPHONE ACCESS"); }
    });
  }

  private async stop(asDraft: boolean) {
    if (this.phase !== "rec") return;
    this.draft = asDraft;
    this.setPhase("transcribing");
    const clip = await mic.close();
    // Disposed (or reset) while the mic was closing.
    if ((this.phase as Phase) !== "transcribing") return;
    if (!clip || clip.samples.length < clip.rate * MIN_CLIP_S) { this.showNote("HEARD NOTHING"); return; }
    const reqId = crypto.randomUUID();
    pending.set(reqId, this);
    send({ type: "voice.transcribe", reqId, b64: bytesToB64(toPcm16(clip.samples, clip.rate)) });
  }

  onResult(text: string, error?: string) {
    if (this.phase !== "transcribing") return;
    if (error) { this.showNote(model?.state === "error" ? "MODEL DOWNLOAD FAILED" : "COULDN'T TRANSCRIBE"); return; }
    if (!text.trim()) { this.showNote("HEARD NOTHING"); return; }
    const kind = this.target.kind();
    this.target.deliver(voiceText(text, kind), voiceSubmits(kind, this.draft));
    this.setPhase("sending");
  }

  private showNote(text: string) {
    this.note = text;
    this.setPhase("note");
  }

  private sizeCanvas() {
    this.dpr = window.devicePixelRatio || 1;
    const w = this.canvas.clientWidth || 300, h = this.canvas.clientHeight || 68;
    if (this.canvas.width !== Math.round(w * this.dpr)) {
      this.canvas.width = Math.round(w * this.dpr);
      this.canvas.height = Math.round(h * this.dpr);
    }
  }

  private frame(now: number) {
    this.raf = 0;
    if (this.phase === "idle") return;
    const e = now - this.phaseStart;

    if (this.phase === "rec") {
      const { level, peak } = mic.read();
      this.gain += (Math.min(12, Math.max(1, 0.8 / Math.max(peak, 1e-4))) - this.gain) * 0.1;
      this.lastWave.set(mic.wave);
      if (now - this.recStart > MAX_MS) void this.stop(false);
      else if (this.latched) {
        if (level > QUIET_LEVEL) this.quietSince = now;
        else if (now - this.quietSince > QUIET_MS) void this.stop(false);
      }
    }
    if (this.phase === "sending" && e > 450) { this.setPhase("idle"); return; }
    if (this.phase === "note" && e > 1600) { this.setPhase("idle"); return; }

    this.draw(now, e);
    this.raf = requestAnimationFrame((t) => this.frame(t));
  }

  /** The oscilloscope: a phosphor trace that persists and fades, a sweeping
   *  dot while Whisper works, and a TV power-off when the words go in. */
  private draw(now: number, e: number) {
    const g = this.g, W = this.canvas.width / this.dpr, H = this.canvas.height / this.dpr;
    const mid = H / 2 + 3, amp = H / 2 - 12;
    g.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    g.globalCompositeOperation = "destination-out";
    g.fillStyle = "rgba(0,0,0,0.32)";
    g.fillRect(0, 0, W, H);
    g.globalCompositeOperation = "source-over";
    g.strokeStyle = this.phos;
    g.fillStyle = tint(this.phos, 0.6);
    g.shadowColor = this.phos;
    g.shadowBlur = 8;
    g.lineWidth = 1.6;
    const trace = (k: number) => {
      const wave = this.lastWave;
      g.beginPath();
      for (let i = 0; i < W; i++) {
        const v = Math.max(-1, Math.min(1, wave[Math.floor(i * wave.length / W)] * this.gain)) * k;
        if (i) g.lineTo(i, mid - v * amp); else g.moveTo(i, mid - v * amp);
      }
      g.stroke();
    };
    const blink = now % 1000 < 600 ? "●" : " ";
    let label = "", hint = "";

    if (this.phase === "rec") {
      trace(1);
      const quiet = this.latched ? now - this.quietSince : 0;
      label = `${blink} ${this.latched ? "HANDS-FREE" : "REC"} ${fmt(now - this.recStart)}`;
      if (this.latched && quiet > SHOW_QUIET_MS) {
        const left = QUIET_MS - quiet;
        label += `  · sending in ${(Math.max(0, left) / 1000).toFixed(1)}s`;
        const k = Math.max(0, left / (QUIET_MS - SHOW_QUIET_MS));
        g.fillStyle = this.phos;
        g.fillRect(W / 2 - (W / 2 - 10) * k, H - 3, (W - 20) * k, 1.5);
      }
      hint = this.latched ? "click: send · talk: keep going · esc: draft" : "release: send · esc: draft";
    } else if (this.phase === "transcribing") {
      g.globalAlpha = 0.35;
      g.beginPath(); g.moveTo(0, mid); g.lineTo(W, mid); g.stroke();
      g.globalAlpha = 1;
      g.beginPath(); g.arc((e / 1.6) % W, mid, 2.5, 0, Math.PI * 2); g.fill();
      label = model?.state === "downloading"
        ? `DOWNLOADING SPEECH MODEL ${model.pct ?? 0}%`
        : "DECODING" + ".".repeat(Math.floor(now / 200) % 4);
      hint = model?.state === "downloading" ? "one time · ~140 MB" : "whisper · on this computer";
    } else if (this.phase === "sending") {
      const p = Math.min(1, e / 420);
      if (p < 0.3) trace(1 - p / 0.3);
      else if (p < 0.8) {
        const half = W / 2 * (1 - (p - 0.3) / 0.5);
        g.strokeStyle = tint(this.phos, 0.75);
        g.lineWidth = 2.2;
        g.beginPath(); g.moveTo(W / 2 - half, mid); g.lineTo(W / 2 + half, mid); g.stroke();
      } else {
        g.beginPath(); g.arc(W / 2, mid, 3 * (1 - (p - 0.8) / 0.2), 0, Math.PI * 2); g.fill();
      }
      label = this.draft ? "DRAFT" : this.target.kind() === "shell" ? "DRAFT · ENTER TO RUN" : "SENT";
    } else {
      g.globalAlpha = 0.35;
      g.beginPath(); g.moveTo(0, mid); g.lineTo(W, mid); g.stroke();
      g.globalAlpha = 1;
      label = this.note;
    }
    g.shadowBlur = 0;
    if (this.labelEl.textContent !== label) this.labelEl.textContent = label;
    if (this.hintEl.textContent !== hint) this.hintEl.textContent = hint;
  }
}

/** The pane the chord talks into: the one holding focus, else the last one used. */
function chordTarget(): VoiceButton | null {
  const focused = document.activeElement;
  for (const b of buttons) if (b.contains(focused)) return b;
  return lastFocused && buttons.has(lastFocused) ? lastFocused : null;
}

if (typeof window !== "undefined") {   // absent under the node test runner
  // Capture phase: the chord and the draft-Esc must not reach the terminal —
  // an Esc that reached Claude would interrupt its turn.
  window.addEventListener("keydown", (ev) => {
    if (ev.code === "Space" && ev.shiftKey && chordMod(ev)) {
      ev.preventDefault();
      ev.stopPropagation();
      if (ev.repeat) return;
      (active ?? chordTarget())?.press("key");
      return;
    }
    if (ev.key === "Escape" && active?.recording) {
      ev.preventDefault();
      ev.stopPropagation();
      active.keepAsDraft();
    }
  }, true);
  window.addEventListener("keyup", (ev) => {
    if (ev.code === "Space" && active) active.release("key");
  }, true);
  // A held chord whose keyup went to another window would listen forever.
  window.addEventListener("blur", () => active?.release("key"));
}
