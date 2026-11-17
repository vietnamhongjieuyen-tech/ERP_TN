// Rebuilds the table of a PDF bank statement from positioned text (as pdf.js returns it), into rows of cells that
// bankfile.parseStatementRows understands. Works on upright coordinates: x from the left, y from the top of the page.
//
// A PDF has no table structure, only words at positions. The header line gives the columns; a line whose date column holds
// a date starts a transaction; wrapped lines (a long description, a bank name on two lines) are folded into their transaction.

import { bankDate } from './bankfile.ts'
import { headerKey } from './sheetread.ts'

export interface TextItem { x: number; y: number; w: number; s: string }
/** The text of one page; rowRules: y positions of the table's horizontal ruling lines when known (pictures read by OCR). */
export type Page = TextItem[] & { rowRules?: number[] }

// Lines that close the table or follow it. "CONG TY …" / "TONG CONG TY …" are company names in a wrapped description, not totals.
const FOOTER = /^(tong(?! cong ty)|cong(?! ty)|so du cuoi|so du dau|total|ending|kiem soat|ke toan|xac nhan)\b/
const AMOUNT = /^[-+(]?\d[\d.,]*\)?$/

/**
 * "Nội dung / Details : …" lines of the debit / credit advices some banks print after the account book (one advice per
 * transaction, in the same order). Used to fill descriptions when the book itself has no description column.
 */
export function adviceDetails(rows: string[][]): string[] {
  const out: string[] = []
  for (const r of rows) {
    if (r.length !== 1) continue
    const m = /^\s*Nội dung\s*(?:\/\s*(?:Details|Remarks|Description))?\s*:\s*(\S.*)$/i.exec(r[0])
    if (m) out.push(m[1].trim())
  }
  return out
}

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

type Role = 'stt' | 'txdate' | 'date' | 'ref' | 'debit' | 'credit' | 'amount' | 'balance' | 'desc'

/**
 * What a column holds, from its header text (all header lines of that column joined, accents removed): banks print the same
 * columns under many captions, in Vietnamese and English, and OCR sometimes runs words together ("sotienghino").
 */
export function columnRole(key: string): Role | null {
  const k = ` ${key} `, has = (...w: string[]) => w.some(x => k.includes(` ${x} `) || (x.includes(' ') && k.includes(x)))
  if (/^ stt\b/.test(k)) return 'stt'
  if (has('ngay giao dich', 'transaction date', 'txn date', 'ngay gd')) return 'txdate'
  if (has('so du', 'balance')) return 'balance'
  if (has('noi dung', 'dien giai', 'description', 'remark', 'remarks') || k.includes('detail')) return 'desc'
  if (has('so tham chieu', 'reference', 'ref no', 'transaction no', 'so but toan', 'ma giao dich', 'so giao dich', 'ma gd')) return 'ref'
  if (has('debit', 'ghi no', 'phat sinh no', 'doanh so no', 'so tien ghi no', 'tien ra') || k.includes('sotienghino') || key === 'no') return 'debit'
  if (has('credit', 'ghi co', 'phat sinh co', 'doanh so co', 'so tien ghi co', 'tien vao') || k.includes('sotienghico') || key === 'co') return 'credit'
  if (has('so tien', 'amount')) return 'amount'
  if (has('ngay', 'date')) return 'date'
  return null
}

const CANONICAL: Partial<Record<Role, string>> = {
  stt: 'STT', txdate: 'Ngày giao dịch', ref: 'Số tham chiếu', debit: 'Ghi nợ', credit: 'Ghi có', amount: 'Số tiền', balance: 'Số dư', desc: 'Nội dung',
}

/** spans: horizontal extent of each column's captions (all header lines), used to place left-aligned text and right-aligned amounts. */
interface Columns { centers: number[]; spans?: [number, number][]; names: string[]; roles: (Role | null)[]; date: number }

const keyOf = (s: string) => headerKey(s.replace(/\/\s*$/, ''))

const hasData = (l: TextItem[]) => l.some(i => bankDate(i.s) !== null || /\d[.,]\d{3}/.test(i.s))

/**
 * A header line: at least three captions, among them a date column and an amount column, or (the date caption lost to OCR)
 * two of debit / credit / balance and a third recognised column. A title like "BANK STATEMENT/DEBIT – CREDIT" is not one.
 */
function isHeader(line: TextItem[]): boolean {
  if (hasData(line)) return false   // "Số dư đầu kỳ / Opening Balance: 1.000.000" names a balance but is not the table header
  if (line.length < 3) return false
  const roles = line.map(i => columnRole(keyOf(i.s)))
  const amount = roles.some(r => r === 'debit' || r === 'credit' || r === 'amount')
  if (amount && roles.some(r => r === 'txdate' || r === 'date')) return true
  return amount && new Set(roles.filter(r => r === 'debit' || r === 'credit' || r === 'balance')).size >= 2 && roles.filter(r => r !== null).length >= 3
}

/**
 * Column of an item: the column whose caption span it overlaps or is closest to (a description starting at the left edge of
 * a wide column is nearer that column than the centre of a narrow neighbour); without spans, the nearest caption centre.
 */
const columnOf = (c: { centers: number[]; spans?: [number, number][] }, it: TextItem) => {
  const x = it.x + it.w / 2
  const score = (i: number) => {
    const centre = Math.abs(c.centers[i] - x)
    if (!c.spans) return centre
    const [l, r] = c.spans[i], gap = Math.max(0, l - (it.x + it.w), it.x - r)
    const overlap = Math.max(0, Math.min(r, it.x + it.w) - Math.max(l, it.x))
    return gap * 1000 - overlap + centre / 1000
  }
  let best = 0
  for (let i = 1; i < c.centers.length; i++) if (score(i) < score(best)) best = i
  return best
}


/**
 * Columns from the header line and the caption lines right above / below it (a bilingual header, a caption split over two
 * lines): each column's role comes from all its captions; recognised columns get a standard name parseStatementRows knows.
 */
function buildColumns(lines: TextItem[][], at: number): { cols: Columns; first: number; last: number } {
  const main = lines[at]
  const base = { centers: main.map(i => i.x + i.w / 2) }
  const near = (a: number, b: number) => Math.abs(lines[a][0].y - lines[b][0].y) <= 24
  let first = at, last = at
  while (first > 0 && at - first < 2 && near(first - 1, first) && !hasData(lines[first - 1])) first--
  while (last + 1 < lines.length && last - at < 2 && near(last + 1, last) && !hasData(lines[last + 1])) last++
  const captions: string[][] = main.map(() => [])
  const spans: [number, number][] = main.map(it => [it.x, it.x + it.w])
  for (let l = first; l <= last; l++) for (const it of lines[l]) {
    const k = columnOf(base, it)
    captions[k].push(it.s.replace(/\/\s*$/, ''))
    spans[k] = [Math.min(spans[k][0], it.x), Math.max(spans[k][1], it.x + it.w)]
  }
  const roles = captions.map(c => columnRole(headerKey(c.join(' '))))
  const names = main.map((it, k) => CANONICAL[roles[k]!] ?? it.s.replace(/\/\s*$/, '').trim())
  const date = roles.indexOf('txdate') >= 0 ? roles.indexOf('txdate') : roles.indexOf('date')
  return { cols: { centers: base.centers, spans, names, roles, date }, first, last }
}

/**
 * Rows for parseStatementRows from the pages of a statement: lines outside the table become one-cell rows (they carry the
 * period and the opening / closing balance), the header becomes a row of column names, every transaction one row of cells.
 */
export function statementRowsFromPdf(pages: Page[]): string[][] {
  const rows: string[][] = []
  let cols: Columns | null = null
  let headerDone = false
  for (const page of pages) {
    const lines = textLines(page)
    let start = 0
    const at = lines.findIndex(isHeader)
    if (at >= 0) {
      const built = buildColumns(lines, at)
      for (const l of lines.slice(0, built.first)) rows.push([l.map(i => i.s).join(' ')])
      cols = built.cols
      start = built.last + 1
    }
    if (!cols) { for (const l of lines) rows.push([l.map(i => i.s).join(' ')]); continue }
    const c: Columns = cols

    const body = lines.slice(start)
    // The date column was not recognised from its caption (OCR lost it): it is the column where the dates are.
    if (c.date < 0) {
      const votes = c.names.map(() => 0)
      for (const l of body) for (const it of l) if (bankDate(it.s) !== null) votes[columnOf(c, it)]++
      const best = votes.indexOf(Math.max(...votes))
      if (votes[best] > 0) { c.date = best; c.roles[best] = 'txdate'; c.names[best] = CANONICAL.txdate! }
    }
    if (!headerDone) { rows.push(c.names); headerDone = true }
    const footer = (i: number) => FOOTER.test(headerKey(body[i].map(x => x.s).join(' ')))
    const inAmountColumn = (it: TextItem) => { const r = c.roles[columnOf(c, it)]; return (r === 'debit' || r === 'credit' || r === 'amount') && AMOUNT.test(it.s.trim()) }
    // A transaction line has a date in the date column and an amount in an amount column. Some layouts print the date on its
    // own line above or below the amounts (photographed statements, centred cells): then every line with an amount is one.
    const strict = (l: TextItem[]) => l.some(i => columnOf(c, i) === c.date && bankDate(i.s) !== null) && l.some(inAmountColumn)
    const loose = (l: TextItem[], i: number) => !footer(i) && l.some(inAmountColumn)
    const strictAnchors = body.map((l, i) => (strict(l) ? i : -1)).filter(i => i >= 0)
    const looseAnchors = body.map((l, i) => (loose(l, i) ? i : -1)).filter(i => i >= 0)
    const anchors = looseAnchors.length > strictAnchors.length ? looseAnchors : strictAnchors
    if (anchors.length === 0) { for (const l of body) rows.push([l.map(i => i.s).join(' ')]); continue }
    const yOf = (i: number) => body[i][0].y
    const spacing = (() => {   // typical distance between neighbouring text lines of the table
      const gaps = body.slice(anchors[0], anchors[anchors.length - 1] + 1).map((_, k) => yOf(anchors[0] + k)).map((y, k, a) => (k ? y - a[k - 1] : 0)).filter(g => g > 0).sort((a, b) => a - b)
      return gaps.length ? gaps[Math.floor(gaps.length / 2)] : 12
    })()
    // Wrapped text is either centred on its transaction line (lines above and below it) or starts on it (lines below only):
    // a plain text line between the header and the first transaction means "centred".
    const centred = body.slice(0, anchors[0]).some((_, i) => !footer(i) && anchors[0] - i <= 2 && yOf(anchors[0]) - yOf(i) <= spacing * 1.6)

    const owner = new Map<number, number>()   // body line → anchor line it belongs to
    for (const a of anchors) owner.set(a, a)
    // Ruled table: a line belongs to the transaction between the same two horizontal rules, wherever it sits in the row.
    const rules = page.rowRules ?? []
    if (rules.length >= 2) {
      const band = (i: number) => rules.filter(r => r < yOf(i) - 1).length
      const anchorOfBand = new Map<number, number[]>()
      for (const a of anchors) anchorOfBand.set(band(a), [...(anchorOfBand.get(band(a)) ?? []), a])
      for (let i = 0; i < body.length; i++) {
        if (owner.has(i) || footer(i)) continue
        const same = anchorOfBand.get(band(i))
        if (same?.length === 1) owner.set(i, same[0])
      }
    }
    if (centred) {
      // Every line between two transactions belongs to one of them; the cells of the upper and the lower transaction are
      // separated by the widest vertical gap. Above the first / below the last transaction, lines belong as long as they follow closely.
      for (let k = 0; k <= anchors.length; k++) {
        const above = k > 0 ? anchors[k - 1] : undefined, below = k < anchors.length ? anchors[k] : undefined
        const between: number[] = []
        for (let i = (above ?? -1) + 1; i < (below ?? body.length); i++) if (!footer(i) && !owner.has(i)) between.push(i)
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
