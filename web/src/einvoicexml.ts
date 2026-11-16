// Reads e-invoice XML files as downloaded from the tax authority's e-invoice portal (one invoice.xml per invoice; element
// names HDon / DLHDon / TTChung / NDHDon / TToan) into the neutral form of the e-invoice import. Only the fields needed to
// reconcile VAT are taken; the signature is not verified here — the portal is the source of the files.

export interface EInvoiceRate { vatRate: string | null; amountBeforeVat: string; vatAmount: string }
export interface EInvoice {
  direction: 'Input' | 'Output'; sellerTaxCode: string; sellerName: string; buyerTaxCode: string | null; buyerName: string | null
  series: string; number: string; issueDate: string; currency: string | null; exchangeRate: string | null
  amountBeforeVat: string; vatAmount: string; totalPayment: string; status: 'Original' | 'Replacement' | 'Adjustment'
  relatedSeries: string | null; relatedNumber: string | null; lookupCode: string | null; rates: EInvoiceRate[]
}

const decode = (s: string) => s.replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, '$1').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"')
  .replace(/&apos;/g, "'").replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(Number(n))).replace(/&amp;/g, '&').trim()
/** Inner XML of the first <tag> inside xml (namespace prefixes ignored); null when absent. */
function inner(xml: string | null, tag: string): string | null {
  if (!xml) return null
  const m = new RegExp(`<(?:\\w+:)?${tag}(?:\\s[^>]*)?>([\\s\\S]*?)</(?:\\w+:)?${tag}>`).exec(xml)
  return m ? m[1] : null
}
const all = (xml: string | null, tag: string) => (xml ? [...xml.matchAll(new RegExp(`<(?:\\w+:)?${tag}(?:\\s[^>]*)?>([\\s\\S]*?)</(?:\\w+:)?${tag}>`, 'g'))].map(m => m[1]) : [])
const text = (xml: string | null, tag: string) => { const v = inner(xml, tag); return v === null ? null : decode(v) }

/** XML numbers use a dot as decimal separator; trailing zeros and a leading "+" are dropped. */
function money(v: string | null): string | null {
  if (v === null) return null
  const s = v.replace(/\s/g, '').replace(/^\+/, '')
  if (!/^-?\d+(\.\d+)?$/.test(s)) return null
  const neg = s.startsWith('-')
  const [i, f = ''] = (neg ? s.slice(1) : s).split('.')
  const out = i.replace(/^0+(?=\d)/, '') + (f.replace(/0+$/, '') ? '.' + f.replace(/0+$/, '') : '')
  return neg && out !== '0' ? '-' + out : out
}
const plus = (a: string, b: string) => {
  const f = (v: string) => { const n = v.startsWith('-'); const [i, d = ''] = (n ? v.slice(1) : v).split('.'); const x = BigInt(i) * 10000n + BigInt((d + '0000').slice(0, 4)); return n ? -x : x }
  const t = f(a) + f(b), n = t < 0n, x = n ? -t : t
  const d = (x % 10000n).toString().padStart(4, '0').replace(/0+$/, '')
  return (n ? '-' : '') + (x / 10000n).toString() + (d ? '.' + d : '')
}
/** "10%" → "10", "8%" → "8", "0%" → "0"; KCT / KKKNT / KHAC (no rate) → null. */
const rate = (v: string | null) => { const m = /^(\d+(?:\.\d+)?)\s*%?$/.exec((v ?? '').trim()); return m ? m[1] : null }

/**
 * One invoice. ownTaxCode decides the direction: an invoice whose seller is the company is an output invoice, anything
 * else an input invoice. Throws an Error naming what is missing when the file is not a readable invoice.
 */
export function parseEInvoiceXml(xml: string, ownTaxCode: string | null): EInvoice {
  const data = inner(xml, 'DLHDon')
  const general = inner(data, 'TTChung'), content = inner(data, 'NDHDon')
  if (!general || !content) throw new Error('không phải tệp hóa đơn điện tử (thiếu DLHDon / TTChung / NDHDon)')
  const seller = inner(content, 'NBan'), buyer = inner(content, 'NMua'), pay = inner(content, 'TToan')
  const sellerTax = text(seller, 'MST'), number = text(general, 'SHDon'), issue = (text(general, 'NLap') ?? '').slice(0, 10)
  const series = (text(general, 'KHMSHDon') ?? '') + (text(general, 'KHHDon') ?? '')
  if (!sellerTax || !number || !series || !/^\d{4}-\d{2}-\d{2}$/.test(issue)) throw new Error('thiếu MST người bán, ký hiệu, số hoặc ngày lập')

  // VAT by rate: the summary table when present, otherwise the goods lines grouped by rate.
  const byRate = new Map<string, EInvoiceRate>()
  const add = (r: string | null, amount: string, vat: string) => {
    const k = r ?? ''
    const cur = byRate.get(k) ?? { vatRate: r, amountBeforeVat: '0', vatAmount: '0' }
    byRate.set(k, { vatRate: r, amountBeforeVat: plus(cur.amountBeforeVat, amount), vatAmount: plus(cur.vatAmount, vat) })
  }
  for (const lt of all(inner(pay, 'THTTLTSuat'), 'LTSuat')) add(rate(text(lt, 'TSuat')), money(text(lt, 'ThTien')) ?? '0', money(text(lt, 'TThue')) ?? '0')
  if (byRate.size === 0) for (const g of all(inner(content, 'DSHHDVu'), 'HHDVu')) add(rate(text(g, 'TSuat')), money(text(g, 'ThTien')) ?? '0', money(text(g, 'TThue')) ?? '0')
  const rates = [...byRate.values()]

  const before = money(text(pay, 'TgTCThue')) ?? rates.reduce((s, r) => plus(s, r.amountBeforeVat), '0')
  const vat = money(text(pay, 'TgTThue')) ?? rates.reduce((s, r) => plus(s, r.vatAmount), '0')
  const total = money(text(pay, 'TgTTTBSo')) ?? plus(before, vat)

  // Replacement / adjustment invoices name the invoice they refer to (TTHDLQuan: TCHDon 1 = replaces, 2 = adjusts).
  const related = inner(general, 'TTHDLQuan')
  const kind = text(related, 'TCHDon')
  const currency = text(general, 'DVTTe')
  const fx = money(text(general, 'TGia'))
  return {
    direction: ownTaxCode && sellerTax === ownTaxCode.trim() ? 'Output' : 'Input',
    sellerTaxCode: sellerTax, sellerName: text(seller, 'Ten') ?? '', buyerTaxCode: text(buyer, 'MST'), buyerName: text(buyer, 'Ten'),
    series, number, issueDate: issue,
    currency: currency && currency !== 'VND' ? currency : null, exchangeRate: currency && currency !== 'VND' ? fx : null,
    amountBeforeVat: before, vatAmount: vat, totalPayment: total,
    status: related ? (kind === '2' ? 'Adjustment' : 'Replacement') : 'Original',
    relatedSeries: related ? (text(related, 'KHMSHDCLQuan') ?? '') + (text(related, 'KHHDCLQuan') ?? '') || null : null,
    relatedNumber: related ? text(related, 'SHDCLQuan') : null,
    lookupCode: text(xml, 'MCCQT') || null,   // absent on invoices issued without a tax-authority code (series K…)
    rates,
  }
}
