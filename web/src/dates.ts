// Dates are shown and typed as dd/MM/yyyy whatever the browser's language; values travel as ISO yyyy-MM-dd.

const pad = (n: number) => String(n).padStart(2, '0')

/** ISO yyyy-MM-dd → dd/MM/yyyy ('' for empty or malformed). */
export function formatDmy(iso: string | null | undefined): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso ?? '')
  return m ? `${m[3]}/${m[2]}/${m[1]}` : ''
}

/**
 * Typed date → ISO, or null when it is not a real calendar date. Accepts dd/MM/yyyy, d/M/yyyy, with '/', '-', '.' or spaces,
 * and 8 digits without separators (05032026). A 2-digit year means 20yy only when `twoDigitYear` (on leaving the field), so a
 * year still being typed is not taken early.
 */
export function parseDmy(text: string, twoDigitYear = false): string | null {
  const s = text.trim()
  let d: number, mo: number, y: number
  let m = /^(\d{1,2})[/.\-\s](\d{1,2})[/.\-\s](\d{4}|\d{2})$/.exec(s)
  if (m) {
    if (m[3].length === 2 && !twoDigitYear) return null
    ;[d, mo, y] = [Number(m[1]), Number(m[2]), m[3].length === 2 ? 2000 + Number(m[3]) : Number(m[3])]
  } else if ((m = /^(\d{2})(\d{2})(\d{4})$/.exec(s))) {
    ;[d, mo, y] = [Number(m[1]), Number(m[2]), Number(m[3])]
  } else return null
  if (y < 1900 || y > 2999 || mo < 1 || mo > 12 || d < 1) return null
  const last = new Date(Date.UTC(y, mo, 0)).getUTCDate()
  if (d > last) return null
  return `${y}-${pad(mo)}-${pad(d)}`
}
