// The terminal's font stack. Settings holds ONE family name ("Cascadia Code"
// by default, a Windows font); handed to xterm alone, a Mac without it falls
// back to WebKit's proportional default and every glyph sits in a cell sized
// for the widest letter. So the setting always goes first and the bundled
// Geist Mono chain follows it.

export const DEFAULT_TERM_FONT =
  '"Geist Mono Variable", "Cascadia Code", "Cascadia Mono", Consolas, monospace';

export function termFontStack(family?: string): string {
  const f = family?.trim();
  if (!f) return DEFAULT_TERM_FONT;
  // A single bare name gets quoted; a stack or a quoted name is kept as written.
  const first = f.includes(",") || /^["']/.test(f) ? f : `"${f}"`;
  return `${first}, ${DEFAULT_TERM_FONT}`;
}
