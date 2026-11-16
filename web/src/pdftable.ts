// Rebuilds the table of a PDF bank statement from positioned text (as pdf.js returns it), into rows of cells that
// bankfile.parseStatementRows understands. Works on upright coordinates: x from the left, y from the top of the page.
//
// A PDF has no table structure, only words at positions. The header line gives the columns; a line whose date column holds
// a date starts a transaction; wrapped lines (a long description, a bank name on two lines) are folded into their transaction.

import { bankDate } from './bankfile.ts'
import { headerKey } from './sheetread.ts'

export interface TextItem { x: number; y: number; w: number; s: string }

const DATE_HEADERS = ['ngay giao dich', 'ngay gd', 'ngay', 'ngay hieu luc', 'ngay hach toan', 'transaction date', 'date']
const AMOUNT_HEADERS = ['no', 'co', 'ghi no', 'ghi co', 'phat sinh no', 'phat sinh co', 'so tien', 'debit', 'credit', 'so tien ghi no', 'so tien ghi co']
// Lines that close the table or follow it. "CONG TY …" / "TONG CONG TY …" are company names in a wrapped description, not totals.
const FOOTER = /^(tong(?! cong ty)|cong(?! ty)|so du cuoi|so du dau|total|ending|kiem soat|ke toan|xac nhan)\b/
const AMOUNT = /^[-+(]?\d[\d.,]*\)?$/

/** Text lines of one page: items whose tops are within 4 units share a line; items are ordered left to right. */
export function textLines(items: TextItem[]): TextItem[][] {
  const lines: TextItem[][] = []
  let top = -Infinity
  for (const it of [...items].filter(i => i.s.trim() !== '').sort((a, b) => a.y - b.y || a.x - b.x)) {
    if (it.y - top > 4) { lines.push([]); top = it.y }
    lines[lines.length - 1].push(it)
  }
  return lines.map(l => l.sort((a, b) => a.x - b.x))
}

interface Columns { centers: number[]; names: string[]; date: number }

function findHeader(line: TextItem[]): Columns | null {
  const keys = line.map(i => headerKey(i.s.replace(/\/\s*$/, '')))
  const date = keys.findIndex(k => DATE_HEADERS.includes(k))
  if (date < 0 || !keys.some(k => AMOUNT_HEADERS.includes(k))) return null
  return { centers: line.map(i => i.x + i.w / 2), names: line.map(i => i.s.replace(/\/\s*$/, '').trim()), date }
}

/** Column of an item: the header whose centre is nearest to the item's centre. */
const columnOf = (c: Columns, it: TextItem) => {
  const x = it.x + it.w / 2
  let best = 0
  for (let i = 1; i < c.centers.length; i++) if (Math.abs(c.centers[i] - x) < Math.abs(c.centers[best] - x)) best = i
  return best
}

/**
 * Rows for parseStatementRows from the pages of a statement: lines outside the table become one-cell rows (they carry the
 * period and the opening / closing balance), the header becomes a row of column names, every transaction one row of cells.
 */
export function statementRowsFromPdf(pages: TextItem[][]): string[][] {
  const rows: string[][] = []
  let cols: Columns | null = null
  let headerDone = false
  for (const page of pages) {
    const lines = textLines(page)
    let start = 0
    const at = lines.findIndex(l => findHeader(l) !== null)
    if (at >= 0) {
      for (const l of lines.slice(0, at)) rows.push([l.map(i => i.s).join(' ')])
      cols = findHeader(lines[at])
      if (!headerDone) { rows.push(cols!.names); headerDone = true }
      start = at + 1
    }
    if (!cols) { for (const l of lines) rows.push([l.map(i => i.s).join(' ')]); continue }
    const c: Columns = cols

    let body = lines.slice(start)
    // A second header line in another language sits right under the header, aligned with it: not a transaction, not wrapped text.
    const first = body[0]
    if (first && first.length >= 3 && first.every(i => bankDate(i.s) === null && !/\d/.test(i.s) && c.centers.some(x => Math.abs(x - (i.x + i.w / 2)) <= 25))) {
      rows.push([first.map(i => i.s).join(' ')])
      body = body.slice(1)
    }
    // A transaction line has a date in the date column and an amount somewhere else (a printed-on date in a footer has none).
    const isAnchor = (l: TextItem[]) => l.some(i => columnOf(c, i) === c.date && bankDate(i.s) !== null)
      && l.some(i => columnOf(c, i) !== c.date && AMOUNT.test(i.s.trim()))
    const anchors = body.map((l, i) => (isAnchor(l) ? i : -1)).filter(i => i >= 0)
    if (anchors.length === 0) { for (const l of body) rows.push([l.map(i => i.s).join(' ')]); continue }
    const yOf = (i: number) => body[i][0].y
    const spacing = (() => {   // typical distance between neighbouring text lines of the table
      const gaps = body.slice(anchors[0], anchors[anchors.length - 1] + 1).map((_, k) => yOf(anchors[0] + k)).map((y, k, a) => (k ? y - a[k - 1] : 0)).filter(g => g > 0).sort((a, b) => a - b)
      return gaps.length ? gaps[Math.floor(gaps.length / 2)] : 12
    })()
    const footer = (i: number) => FOOTER.test(headerKey(body[i].map(x => x.s).join(' ')))
    // Wrapped text is either centred on its transaction line (lines above and below it) or starts on it (lines below only):
    // a plain text line between the header and the first transaction means "centred".
    const centred = body.slice(0, anchors[0]).some((_, i) => !footer(i) && anchors[0] - i <= 2 && yOf(anchors[0]) - yOf(i) <= spacing * 1.6)

    const owner = new Map<number, number>()   // body line → anchor line it belongs to
    for (const a of anchors) owner.set(a, a)
    if (centred) {
      // Every line between two transactions belongs to one of them; the cells of the upper and the lower transaction are
      // separated by the widest vertical gap. Above the first / below the last transaction, lines belong as long as they follow closely.
      for (let k = 0; k <= anchors.length; k++) {
        const above = k > 0 ? anchors[k - 1] : undefined, below = k < anchors.length ? anchors[k] : undefined
        const between: number[] = []
        for (let i = (above ?? -1) + 1; i < (below ?? body.length); i++) if (!footer(i)) between.push(i)
        if (between.length === 0) continue
        if (above === undefined) {
          let last = yOf(below!)
          for (const i of [...between].reverse()) { if (last - yOf(i) > spacing * 1.6) break; owner.set(i, below!); last = yOf(i) }
        } else if (below === undefined) {
          let last = yOf(above)
          for (const i of between) { if (yOf(i) - last > spacing * 1.6) break; owner.set(i, above); last = yOf(i) }
        } else {
          const ys = [yOf(above), ...between.map(yOf), yOf(below)]
          let cut = 0
          for (let j = 1; j < ys.length - 1; j++) if (ys[j + 1] - ys[j] > ys[cut + 1] - ys[cut] + 0.5) cut = j
          between.forEach((i, j) => owner.set(i, j < cut ? above : below))
        }
      }
    } else {
      for (let i = 0; i < body.length; i++) {
        if (owner.has(i) || footer(i)) continue
        const prev = [...anchors].reverse().find(a => a < i)
        if (prev === undefined) continue
        // Too far below its transaction: a signature block or a note under the table, not wrapped text.
        const reach = spacing * 1.6 * (1 + [...owner.entries()].filter(([l, o]) => o === prev && l !== prev && l < i).length)
        if (yOf(i) - yOf(prev) <= reach) owner.set(i, prev)
      }
    }

    for (let i = 0; i < body.length; i++) {
      if (!owner.has(i)) { if (i < anchors[0] || i > anchors[anchors.length - 1]) rows.push([body[i].map(x => x.s).join(' ')]); continue }
      if (owner.get(i) !== i) continue
      const cells: string[][] = c.names.map(() => [])
      const parts = [...owner.entries()].filter(([, o]) => o === i).map(([l]) => l).sort((a, b) => a - b)
      for (const l of parts) for (const it of body[l]) {
        const k = columnOf(c, it)
        // Dates and amounts come from the transaction line only; wrapped lines add text (descriptions, names, long references).
        if (l !== i && /^[-+(]?\d[\d.,]*[.,]\d+\)?$/.test(it.s.trim())) continue
        cells[k].push(it.s.trim())
      }
      rows.push(cells.map(x => x.join(' ')))
    }
  }
  return rows
}
