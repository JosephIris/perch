/** At most one small input chunk crosses the bridge until the native write
 *  completes. A large paste waits in the page, not the host dispatcher/pipe. */
export class InputPump {
  private pending: Uint8Array[] = [];
  private offset = 0;
  private sequence = 0;
  private waiting = false;
  private disposed = false;
  private paused = false;
  pause(): void { this.paused = true; }
  resume(): void { this.paused = false; this.pump(); }
  constructor(private readonly send: (bytes: Uint8Array, sequence: number) => void) {}
  enqueue(bytes: Uint8Array): void {
    if (this.disposed || !bytes.length) return;
    this.pending.push(bytes);
    this.pump();
  }
  ack(sequence: number, failed = false): void {
    if (!this.waiting || sequence !== this.sequence) return;
    this.waiting = false;
    if (failed) { this.pending = []; this.offset = 0; return; }
    this.pump();
  }
  private pump(): void {
    if (this.disposed || this.paused || this.waiting || !this.pending.length) return;
    const head = this.pending[0];
    const end = Math.min(head.length, this.offset + 16 * 1024);
    const bytes = head.subarray(this.offset, end);
    this.offset = end;
    if (end === head.length) { this.pending.shift(); this.offset = 0; }
    this.waiting = true;
    this.send(bytes, ++this.sequence);
  }
  dispose(): void { this.disposed = true; this.pending = []; }
}
