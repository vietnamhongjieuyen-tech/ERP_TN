// Reads a bank statement exported by a bank (rows of text cells from Excel / CSV / the HTML table some banks save as .xls)
// into the neutral form the statement import takes. Column positions are found from the header texts, so banks with
// different layouts work as long as they have a date column and debit / credit (or one signed amount) columns.

import { headerKey } from './sheetread.ts'

export interface StatementLine { transactionDate: string; amount: string; bankReference: string; description: string }
export interface ParsedStatement {
  fromDate: string | null; toDate: string | null; opening: string | null; closing: string | null
  lines: StatementLine[]; warnings: string[]
}

/** Rows of an HTML table (Vietcombank's ".xls" is an HTML page): tags dropped, entities decoded. */
export function htmlTableRows(html: string): string[][] {
  const text = (s: string) => s.replace(/<[^>]*>/g, '').replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"').replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(Number(n))).replace(/\s+/g, ' ').trim()
  return [...html.matchAll(/<tr[\s>][\s\S]*?<\/tr>/gi)].map(r => [...r[0].matchAll(/<t[dh][\s>][\s\S]*?<\/t[dh]>/gi)].map(c => text(c[0])))
}

/**
 * "1,234,567" / "1.234.567" / "1,234.50" / "1.234,50" / "(1,000)" → canonical decimal text; null when not a number.
 * A single separator followed by exactly three digits is a thousands separator (bank statements are in whole đồng or 2 decimals).
 */
export function bankNumber(raw: string): string | null {
  let s = raw.replace(/\s|VND|đ/gi, '')
  if (s === '') return null
  let neg = false
  if (/^\(.*\)$/.test(s)) { neg = true; s = s.slice(1, -1) }
  if (s.startsWith('-')) { neg = !neg; s = s.slice(1) } else if (s.startsWith('+')) s = s.slice(1)
  if (!/^\d[\d.,]*$/.test(s)) return null
  const lastDot = s.lastIndexOf('.'), lastComma = s.lastIndexOf(',')
  let dec = -1
  if (lastDot >= 0 && lastComma >= 0) dec = Math.max(lastDot, lastComma)
  else {
    const at = Math.max(lastDot, lastComma)
    if (at >= 0 && s.indexOf(s[at]) === at && s.length - at - 1 !== 3) dec = at   // one separator, not a thousands group
  }
  const int = (dec >= 0 ? s.slice(0, dec) : s).replace(/[.,]/g, '').replace(/^0+(?=\d)/, '')
  const frac = dec >= 0 ? s.slice(dec + 1).replace(/0+$/, '') : ''
  if (!/^\d+$/.test(int) || !/^\d*$/.test(frac)) return null
  const v = int + (frac ? '.' + frac : '')
  return neg && v !== '0' ? '-' + v : v
}

/** "03/10/2026", "03-10-2026 14:05", "2026-10-03T…" → "2026-10-03"; null otherwise. */
export function bankDate(raw: string): string | null {
  const s = raw.trim()
  let m = /^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})(?:\D|$)/.exec(s)
  if (m) return `${m[3]}-${m[2].padStart(2, '0')}-${m[1].padStart(2, '0')}`
  m = /^(\d{4})-(\d{2})-(\d{2})(?:\D|$)/.exec(s)
  return m ? `${m[1]}-${m[2]}-${m[3]}` : null
}

const COLS = {
  date: ['ngay giao dich', 'ngay gd', 'ngay', 'ngay hieu luc', 'ngay hach toan', 'transaction date', 'date'],
  debit: ['no', 'ghi no', 'phat sinh no', 'so tien ghi no', 'so tien rut', 'rut', 'chi', 'debit', 'tien ra'],
  credit: ['co', 'ghi co', 'phat sinh co', 'so tien ghi co', 'so tien gui', 'gui', 'thu', 'credit', 'tien vao'],
  amount: ['so tien', 'so tien giao dich', 'amount'],
  ref: ['so tham chieu', 'ma giao dich', 'so giao dich', 'so gd', 'so but toan', 'so chung tu', 'so ct', 'ma gd', 'reference', 'ref', 'transaction no'],
  desc: ['chi tiet giao dich', 'noi dung', 'noi dung giao dich', 'dien giai', 'mo ta', 'noi dung chi tiet', 'description', 'details', 'remark'],
  balance: ['so du', 'so du cuoi', 'so du sau giao dich', 'balance'],
}
const sub = (a: string, b: string) => {   // exact a − b on decimal strings (4 decimals)
  const f = (v: string) => { const n = v.startsWith('-'); const [i, d = ''] = (n ? v.slice(1) : v).split('.'); const x = BigInt(i) * 10000n + BigInt((d + '0000').slice(0, 4)); return n ? -x : x }
  const t = f(a) - f(b), n = t < 0n, x = n ? -t : t
  const d = (x % 10000n).toString().padStart(4, '0').replace(/0+$/, '')
  return (n ? '-' : '') + (x / 10000n).toString() + (d ? '.' + d : '')
}

export function parseStatementRows(rows: string[][]): ParsedStatement {
  const out: ParsedStatement = { fromDate: null, toDate: null, opening: null, closing: null, lines: [], warnings: [] }
  const find = (keys: string[], names: string[]) => keys.findIndex(k => names.includes(k))

  // Header row: has a date column and something to take the amount from.
  let head = -1
  let col = { date: -1, debit: -1, credit: -1, amount: -1, ref: -1, desc: -1, balance: -1 }
  for (let i = 0; i < rows.length && head < 0; i++) {
    const keys = rows[i].map(headerKey)
    const date = find(keys, COLS.date)
    if (date < 0) continue
    let debit = find(keys, COLS.debit), credit = find(keys, COLS.credit)
    // Two-row header "Phát sinh | Phát sinh" over "Nợ | Có": the two "phát sinh" columns are debit then credit.
    const ps = keys.map((k, j) => (k === 'phat sinh' || k === 'so tien' ? j : -1)).filter(j => j >= 0)
    const below = (rows[i + 1] ?? []).map(headerKey).filter(Boolean)
    if (debit < 0 && credit < 0 && ps.length === 2 && below.length >= 2 && COLS.debit.includes(below[0]) && COLS.credit.includes(below[1])) [debit, credit] = ps
    const amount = debit < 0 && credit < 0 ? find(keys, COLS.amount) : -1
    if (debit < 0 && credit < 0 && amount < 0) continue
    head = i
    col = { date, debit, credit, amount, ref: find(keys, COLS.ref), desc: find(keys, COLS.desc), balance: find(keys, COLS.balance) }
  }

  // Statement period and balances written above / below the table ("Số dư đầu kỳ: 1,234,567 VND").
  for (const r of rows) {
    const line = r.join(' ')
    const key = headerKey(line)
    const num = (re: RegExp) => { const m = re.exec(line); return m ? bankNumber(m[1]) : null }
    if (key.includes('so du dau')) out.opening ??= num(/([-+(]?\d[\d.,]*\)?)\s*(?:VND|đ)?\s*$/i)
    if (key.includes('so du cuoi')) out.closing ??= num(/([-+(]?\d[\d.,]*\)?)\s*(?:VND|đ)?\s*$/i)
    const p = /(\d{1,2}[/.-]\d{1,2}[/.-]\d{4})\D+(\d{1,2}[/.-]\d{1,2}[/.-]\d{4})/.exec(line)
    if (p && out.fromDate === null && /tu ngay|sao ke|from/.test(key)) { out.fromDate = bankDate(p[1]); out.toDate = bankDate(p[2]) }
  }
  if (head < 0) { out.warnings.push('Không nhận ra dòng tiêu đề cột (cần cột Ngày và cột Nợ / Có hoặc Số tiền).'); return out }

  let firstBalance: string | null = null, lastBalance: string | null = null
  for (let i = head + 1; i < rows.length; i++) {
    const r = rows[i]
    const date = bankDate(r[col.date] ?? '')
    if (!date) { if (/^tong/.test(headerKey(r.join(' '))) && out.lines.length > 0) break; continue }
    let amount: string | null
    if (col.amount >= 0) amount = bankNumber(r[col.amount] ?? '')
    else {
      const d = bankNumber(r[col.debit] ?? '') ?? '0', c = bankNumber(r[col.credit] ?? '') ?? '0'
      amount = d === '0' && c === '0' ? null : sub(c, d.replace('-', ''))
    }
    if (!amount || amount === '0') { out.warnings.push(`Dòng ${i + 1} (${date}): không đọc được số tiền, đã bỏ qua.`); continue }
    const n = out.lines.length + 1
    out.lines.push({
      transactionDate: date, amount,
      bankReference: (col.ref >= 0 ? r[col.ref] ?? '' : '').trim() || `KHONG-MA-${date}-${n}`,
      description: (col.desc >= 0 ? r[col.desc] ?? '' : '').trim(),
    })
    if (col.balance >= 0) {
      const b = bankNumber(r[col.balance] ?? '')
      if (b) { firstBalance ??= sub(b, amount); lastBalance = b }
    }
  }
  out.opening ??= firstBalance
  out.closing ??= lastBalance
  if (out.lines.length > 0) {
    const dates = out.lines.map(l => l.transactionDate).sort()
    out.fromDate ??= dates[0]
    out.toDate ??= dates[dates.length - 1]
    if (out.opening && out.closing) {
      const end = out.lines.reduce((s, l) => sub(s, l.amount.startsWith('-') ? l.amount.slice(1) : '-' + l.amount), out.opening)
      if (end !== out.closing) out.warnings.push(`Số dư đầu + phát sinh = ${end} khác số dư cuối ${out.closing} ghi trên sao kê.`)
    } else out.warnings.push('Tệp không ghi số dư đầu / cuối kỳ — nhập tay theo sao kê.')
  } else out.warnings.push('Không có giao dịch nào trong tệp.')
  return out
}
