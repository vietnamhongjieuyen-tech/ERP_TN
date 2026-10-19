// Money stays a decimal string end-to-end (master prompt §28). These helpers never convert to Number.

const DEC = /^-?\d+(\.\d+)?$/

/** "1234567.5" → "1.234.567,5" (Vietnamese grouping). Non-decimal input is returned unchanged. */
export function formatMoney(value: string | null | undefined): string {
  if (value == null || value === '') return ''
  if (!DEC.test(value)) return value
  const neg = value.startsWith('-')
  const [int, frac] = (neg ? value.slice(1) : value).split('.')
  const grouped = int.replace(/\B(?=(\d{3})+(?!\d))/g, '.')
  const trimmed = frac?.replace(/0+$/, '')
  return (neg ? '-' : '') + grouped + (trimmed ? ',' + trimmed : '')
}

/** Accepts "1.234.567,5", "1234567,5" or "1234567.5" and returns the canonical "1234567.5"; null if invalid. */
export function parseMoney(input: string): string | null {
  const s = input.trim().replace(/\s/g, '')
  if (s === '') return null
  let canonical: string
  if (s.includes(',')) canonical = s.replace(/\./g, '').replace(',', '.')
  else if (/^\d{1,3}(\.\d{3})+$/.test(s)) canonical = s.replace(/\./g, '')
  else canonical = s
  if (!/^\d+(\.\d{1,4})?$/.test(canonical)) return null
  return canonical.replace(/^0+(?=\d)/, '')
}

/** Exchange rate (functional units per foreign unit, up to 6 decimals), same separators as parseMoney; null if invalid. */
export function parseRate(input: string): string | null {
  const s = input.trim().replace(/\s/g, '')
  let canonical: string
  if (s.includes(',')) canonical = s.replace(/\./g, '').replace(',', '.')
  else if (/^\d{1,3}(\.\d{3})+$/.test(s)) canonical = s.replace(/\./g, '')
  else canonical = s
  if (!/^\d+(\.\d{1,6})?$/.test(canonical)) return null
  return canonical.replace(/^0+(?=\d)/, '')
}

/** Exact decimal addition on strings (up to 4 fraction digits) using BigInt. */
export function addMoney(values: string[]): string {
  const SCALE = 10000n
  let total = 0n
  for (const v of values) {
    if (!v || !DEC.test(v)) continue
    const neg = v.startsWith('-')
    const [i, f = ''] = (neg ? v.slice(1) : v).split('.')
    const scaled = BigInt(i) * SCALE + BigInt((f + '0000').slice(0, 4))
    total += neg ? -scaled : scaled
  }
  const neg = total < 0n
  const abs = neg ? -total : total
  const frac = (abs % SCALE).toString().padStart(4, '0').replace(/0+$/, '')
  return (neg ? '-' : '') + (abs / SCALE).toString() + (frac ? '.' + frac : '')
}

/** Local calendar date (yyyy-mm-dd). Not toISOString(): that is UTC and gives yesterday before 07:00 in Vietnam. */
export const today = () => {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

const DIGITS = ['không', 'một', 'hai', 'ba', 'bốn', 'năm', 'sáu', 'bảy', 'tám', 'chín']
const SCALES = ['', 'nghìn', 'triệu', 'tỷ', 'nghìn tỷ', 'triệu tỷ', 'tỷ tỷ']

function readGroup(n: number, leading: boolean): string {
  const h = Math.floor(n / 100), t = Math.floor(n / 10) % 10, u = n % 10
  const words: string[] = []
  if (!leading || h > 0) words.push(DIGITS[h], 'trăm')
  if (t === 0) { if (u > 0 && words.length > 0) words.push('linh') }
  else if (t === 1) words.push('mười')
  else words.push(DIGITS[t], 'mươi')
  if (u === 1 && t > 1) words.push('mốt')
  else if (u === 5 && t > 0) words.push('lăm')
  else if (u > 0) words.push(DIGITS[u])
  return words.join(' ')
}

/**
 * Amount in Vietnamese words for printed documents ("Bằng chữ"): "1250000" → "Một triệu hai trăm năm mươi nghìn đồng".
 * Works on the decimal string (no Number), integer part only; a fractional part is read digit by digit after "phẩy".
 */
export function amountInWords(value: string, unit = 'đồng'): string {
  if (!DEC.test(value)) return ''
  const neg = value.startsWith('-')
  const [intRaw, fracRaw] = (neg ? value.slice(1) : value).split('.')
  const int = intRaw.replace(/^0+(?=\d)/, '')
  const frac = (fracRaw ?? '').replace(/0+$/, '')
  if (int.length > 21) return ''
  const groups: number[] = []
  for (let end = int.length; end > 0; end -= 3) groups.unshift(Number(int.slice(Math.max(0, end - 3), end)))
  const parts: string[] = []
  groups.forEach((g, i) => {
    if (g === 0) return
    const scale = SCALES[groups.length - 1 - i]
    parts.push(readGroup(g, parts.length === 0) + (scale ? ' ' + scale : ''))
  })
  let text = parts.length ? parts.join(' ') : 'không'
  if (frac) text += ' phẩy ' + [...frac].map(d => DIGITS[Number(d)]).join(' ')
  text = (neg ? 'âm ' : '') + text + (unit ? ' ' + unit : '')
  return text.charAt(0).toUpperCase() + text.slice(1)
}
/**
 * Balance in words from the company's side (Bên A): a debit balance on the partner's account means the partner owes the
 * company (receivable, or an advance paid to a supplier); a credit balance means the company owes the partner.
 */
export function balanceText(balance: string, partner: string): string {
  if (balance === '0') return 'Hai bên không còn công nợ.'
  return balance.startsWith('-')
    ? `Bên A còn phải trả Bên B (${partner}): ${formatMoney(balance.slice(1))} đồng.`
    : `Bên B (${partner}) còn phải trả Bên A: ${formatMoney(balance)} đồng.`
}

