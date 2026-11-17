// Ruling-line removal for OCR of photographed / scanned bank statements. Adapted from the HAK OCR engine
// (github.com/saoluucvhak-pixel/HAK_OCR_ENGINE, js/core/table-grid.js): adaptive binarisation, line tracing that tolerates
// a slight slope and gaps (dotted rules), and erasing the traced lines so the OCR engine sees only text.
// Pure functions on grey-level pixel arrays: no browser API, so they run in tests under Node too.

/** RGBA → grey (0 = black). */
export function toGray(rgba: Uint8ClampedArray, w: number, h: number): Uint8Array {
  const g = new Uint8Array(w * h)
  for (let i = 0, j = 0; i < g.length; i++, j += 4) g[i] = (rgba[j] * 299 + rgba[j + 1] * 587 + rgba[j + 2] * 114) / 1000
  return g
}

/** Adaptive (Bradley) threshold: a pixel darker than the mean of its neighbourhood by t is ink (1). Copes with uneven light. */
export function binarize(g: Uint8Array, w: number, h: number, radius = Math.max(8, Math.round(Math.min(w, h) / 40)), t = 0.15): Uint8Array {
  const W = w + 1, s = new Float64Array(W * (h + 1))
  for (let y = 1; y <= h; y++) { let row = 0; for (let x = 1; x <= w; x++) { row += g[(y - 1) * w + x - 1]; s[y * W + x] = s[(y - 1) * W + x] + row } }
  const b = new Uint8Array(w * h)
  for (let y = 0; y < h; y++) {
    const y0 = Math.max(0, y - radius), y1 = Math.min(h - 1, y + radius)
    for (let x = 0; x < w; x++) {
      const x0 = Math.max(0, x - radius), x1 = Math.min(w - 1, x + radius), n = (x1 - x0 + 1) * (y1 - y0 + 1)
      const sum = s[(y1 + 1) * W + x1 + 1] - s[y0 * W + x1 + 1] - s[(y1 + 1) * W + x0] + s[y0 * W + x0]
      b[y * w + x] = g[y * w + x] * n < sum * (1 - t) ? 1 : 0
    }
  }
  return b
}

export interface TracedLine { pts: [number, number][]; len: number; a: number; b: number }

/**
 * Follows ink runs along one axis, allowing ±1 pixel drift per step and gaps of up to `gap` pixels (dotted rules, scan noise).
 * horiz = true: horizontal lines. Points are [x, y]; a / b are the start / end along the main axis.
 */
export function traceLines(b: Uint8Array, w: number, h: number, horiz: boolean, minLen: number, gap = 3): TracedLine[] {
  const seen = new Uint8Array(w * h), out: TracedLine[] = [], L = horiz ? w : h, M = horiz ? h : w
  const ink = (u: number, v: number) => (u < 0 || v < 0 || u >= L || v >= M ? 0 : horiz ? b[v * w + u] : b[u * w + v])
  const mark = (u: number, v: number) => { for (let d = -2; d <= 2; d++) { const vv = v + d; if (vv >= 0 && vv < M && u >= 0 && u < L) seen[horiz ? vv * w + u : u * w + vv] = 1 } }
  const isSeen = (u: number, v: number) => seen[horiz ? v * w + u : u * w + v]
  for (let v0 = 0; v0 < M; v0++) for (let u0 = 0; u0 < L; u0++) {
    if (!ink(u0, v0) || isSeen(u0, v0)) continue
    let k = 0
    while (k < 3 && ink(u0 + k, v0)) k++
    if (k < 2) continue   // at least a short dash to start from (dotted rules have short dashes)
    const pts: [number, number][] = []
    let u = u0, v = v0, miss = 0, last = u0
    while (u < L) {
      const nv = ink(u, v) ? v : ink(u, v - 1) ? v - 1 : ink(u, v + 1) ? v + 1 : -1
      if (nv >= 0) { v = nv; miss = 0; last = u; if (!(u % 4)) pts.push([u, v]); mark(u, v) }
      else if (++miss > gap) break
      u++
    }
    if (last - u0 >= minLen) { if (!pts.length || pts[pts.length - 1][0] !== last) pts.push([last, v]); out.push({ pts, len: last - u0, a: u0, b: last }) }
  }
  out.sort((p, q) => p.a - q.a)
  const merged: TracedLine[] = []
  for (const s of out) {
    const vS = s.pts[0][1]
    const m = merged.find(m => s.a - m.b <= Math.max(12, L * 0.02) && s.a >= m.b - 4 && Math.abs(vS - m.pts[m.pts.length - 1][1]) <= 4)
    if (m) { m.pts = m.pts.concat(s.pts); m.b = Math.max(m.b, s.b); m.len = m.b - m.a }
    else merged.push({ pts: s.pts.slice(), len: s.len, a: s.a, b: s.b })
  }
  return merged.map(m => (horiz ? m : { ...m, pts: m.pts.map(p => [p[1], p[0]] as [number, number]) }))
}

/** Mask of traced lines, `t` pixels either side, extended by `ext` at both ends. */
function drawLines(lines: TracedLine[], w: number, h: number, horiz: boolean, t: number, ext: number): Uint8Array {
  const m = new Uint8Array(w * h)
  for (const ln of lines) {
    const pts = ln.pts.slice()
    if (!pts.length) continue
    const f = pts[0], l = pts[pts.length - 1]
    if (horiz) { pts.unshift([Math.max(0, f[0] - ext), f[1]]); pts.push([Math.min(w - 1, l[0] + ext), l[1]]) }
    else { pts.unshift([f[0], Math.max(0, f[1] - ext)]); pts.push([l[0], Math.min(h - 1, l[1] + ext)]) }
    for (let i = 1; i < pts.length; i++) {
      const p = pts[i - 1], q = pts[i], n = Math.max(Math.abs(q[0] - p[0]), Math.abs(q[1] - p[1]), 1)
      for (let s = 0; s <= n; s++) {
        const x = Math.round(p[0] + (q[0] - p[0]) * s / n), y = Math.round(p[1] + (q[1] - p[1]) * s / n)
        for (let d = -t; d <= t; d++) { const xx = horiz ? x : x + d, yy = horiz ? y + d : y; if (xx >= 0 && yy >= 0 && xx < w && yy < h) m[yy * w + xx] = 1 }
      }
    }
  }
  return m
}

export interface CleanResult { gray: Uint8Array; columnRules: number[]; rowRules: number[]; horizontal: number; vertical: number }

/**
 * Erases the table's ruling lines from a grey image (pixels on a traced line become white) and returns the x positions of
 * the vertical rules, which separate columns. Text strokes are short, so only runs longer than a share of the page are lines.
 */
export function removeRules(gray: Uint8Array, w: number, h: number): CleanResult {
  const b = binarize(gray, w, h)
  const hl = traceLines(b, w, h, true, Math.round(w * 0.15), 6)
  const vl = traceLines(b, w, h, false, Math.max(40, Math.round(h * 0.04)), 6)
  const hm = drawLines(hl, w, h, true, 2, 2), vm = drawLines(vl, w, h, false, 2, 2)
  const out = new Uint8Array(gray)
  for (let i = 0; i < out.length; i++) if (hm[i] || vm[i]) out[i] = 255
  // Vertical rules close together (double rules, a rule traced twice) count once.
  const xs = vl.map(l => l.pts.reduce((s, p) => s + p[0], 0) / l.pts.length).sort((a, c) => a - c)
  const rules: number[] = []
  for (const x of xs) if (!rules.length || x - rules[rules.length - 1] > 8) rules.push(x)
  // Horizontal rules separate table rows: text between two of them is one row, however its lines are spread.
  const ys = hl.map(l => l.pts.reduce((s, p) => s + p[1], 0) / l.pts.length).sort((a, c) => a - c)
  const rowRules: number[] = []
  for (const y of ys) if (!rowRules.length || y - rowRules[rowRules.length - 1] > 6) rowRules.push(y)
  return { gray: out, columnRules: rules, rowRules, horizontal: hl.length, vertical: vl.length }
}
