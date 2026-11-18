import { useEffect, useMemo, useState } from 'react'
import { MoneyInput } from './MoneyInput'
import type { AnalysisCode } from './Analysis'
import { CashCountDialog } from './CashCount'
import { useUnsaved } from './leave'
import { SearchSelect } from './combo'
import { get, saveOnce, type OpenItem, type Voucher } from './api'
import { AccountSelect, ErrorBox, PartnerSelect, TYPE_VI, useAction, useCatalog, useRequestKey } from './common'
import { formatMoney, parseMoney, today } from './money'
import { EVENT_VI, ruleLines, ruleNeeds, type Rule, type RuleLine } from './Rules'
import { DateInput } from './DateInput'

// Fallback when the company has no active rule yet (the server then answers that no rule applies).
const DEFAULT_EVENTS: Record<string, string[]> = { CR: ['CUSTOMER_RECEIPT'], BR: ['CUSTOMER_RECEIPT'], CP: ['EXPENSE', 'SUPPLIER_PAYMENT'], BP: ['SUPPLIER_PAYMENT', 'INTERNAL_TRANSFER'] }
const DEFAULT_LINES: Record<string, RuleLine[]> = {
  CUSTOMER_RECEIPT: [{ side: 'D', account: 'DOC:MAIN_ACCOUNT' }, { side: 'C', account: 'ROLE:AR_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }],
  SUPPLIER_PAYMENT: [{ side: 'D', account: 'ROLE:AP_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }],
  EXPENSE: [{ side: 'D', account: 'LINE:COUNTER_ACCOUNT', constraint: 'NOT_CASH_EQUIVALENT' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }],
  INTERNAL_TRANSFER: [{ side: 'D', account: 'LINE:COUNTER_ACCOUNT', constraint: 'CASH_EQUIVALENT' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }],
}
interface EventOption { value: string; label: string; lines: RuleLine[]; fromRule: boolean }

/** Business events offered for a document type: those with a rule in force on the date (Thiết lập → Quy tắc định khoản). */
export function eventsFor(rules: Rule[], type: string, date: string): EventOption[] {
  const inForce = rules.filter(r => r.status === 'Active' && r.documentType === type && r.effectiveFrom <= date && (!r.effectiveTo || r.effectiveTo >= date))
    .sort((a, b) => b.priority - a.priority)
  const out: EventOption[] = []
  for (const r of inForce) if (!out.some(o => o.value === r.businessEvent)) out.push({ value: r.businessEvent, label: EVENT_VI[r.businessEvent] ?? r.description ?? r.businessEvent, lines: ruleLines(r), fromRule: true })
  if (out.length > 0) return out
  return (DEFAULT_EVENTS[type] ?? []).map(e => ({ value: e, label: EVENT_VI[e] ?? e, lines: DEFAULT_LINES[e] ?? [], fromRule: false }))
}
interface DocLine { amount: string; counterAccountId: string; settle: string; description: string; analysisCodeId?: string }

/** Phiếu thu/chi, báo Nợ/Có: định khoản do Posting Rule Engine sinh ra theo nghiệp vụ được chọn. */
export function CashDocsView() {
  const { accounts, partners } = useCatalog()
  const [type, setType] = useState('CR')
  const [event, setEvent] = useState('CUSTOMER_RECEIPT')
  const [date, setDate] = useState(today())
  const [description, setDescription] = useState('')
  const [partnerId, setPartnerId] = useState('')
  const [mainAccountId, setMainAccountId] = useState('')
  const [lines, setLines] = useState<DocLine[]>([{ amount: '', counterAccountId: '', settle: '', description: '' }])
  const [openItems, setOpenItems] = useState<OpenItem[]>([])
  const [created, setCreated] = useState<Voucher | null>(null)
  const [counting, setCounting] = useState(false)
  useUnsaved(lines.some(l => l.amount.trim() || l.description.trim() || l.counterAccountId), 'phiếu thu / chi')
  const { busy, error, setError, run } = useAction()
  const rk = useRequestKey()
  const [codes, setCodes] = useState<AnalysisCode[]>([])
  useEffect(() => { get<AnalysisCode[]>('/analysis-codes').then(setCodes).catch(() => setCodes([])) }, [])
  const codeOptions = useMemo(() => codes.filter(c => c.isActive).map(c => ({ id: c.id, code: c.code, name: `${c.name} (${c.kind})` })), [codes])
  const [rules, setRules] = useState<Rule[]>([])
  const [roleAccounts, setRoleAccounts] = useState<{ roleCode: string; accountId: string }[]>([])
  useEffect(() => {
    get<Rule[]>('/rules').then(setRules).catch(() => setRules([]))
    get<{ roleCode: string; accountId: string }[]>('/rules/role-mappings').then(setRoleAccounts).catch(() => setRoleAccounts([]))
  }, [])
  const events = useMemo(() => eventsFor(rules, type, date), [rules, type, date])
  const evOpt = events.find(e => e.value === event) ?? events[0]
  const needs = ruleNeeds(evOpt?.lines ?? [])
  const ev = { needsPartner: needs.partner, needsCounter: needs.counter }
  const counterLine = evOpt?.lines.find(l => l.account.split('|').some(a => a.trim() === 'LINE:COUNTER_ACCOUNT'))
  // The partner line's side: a credit line (receipt) pays debit open items, a debit line (payment) pays credit ones.
  const partnerLine = evOpt?.lines.find(l => l.partner === 'DOC:PARTNER')
  const cashKind = type === 'CR' || type === 'CP' ? 'Cash' : 'Bank'

  useEffect(() => { setMainAccountId('') }, [type])
  useEffect(() => { if (events.length > 0 && !events.some(e => e.value === event)) setEvent(events[0].value) }, [events, event])
  useEffect(() => {
    const accountId = needs.partnerRole ? roleAccounts.find(m => m.roleCode === needs.partnerRole)?.accountId : undefined
    if (!accountId || !partnerId || !needs.settle || !partnerLine) { setOpenItems([]); return }
    const q = new URLSearchParams({ accountId, partnerId, asOf: date })
    get<OpenItem[]>(`/settlement/open-items?${q}`)
      // Foreign-currency invoices are settled at their book rate through "Ngoại tệ → Thu/chi theo hóa đơn".
      .then(items => setOpenItems(items.filter(i => i.side === (partnerLine.side === 'C' ? 'Debit' : 'Credit') && !i.foreignCurrency)))
      .catch(() => setOpenItems([]))
  }, [roleAccounts, needs.partnerRole, needs.settle, partnerLine, partnerId, date])

  const save = () => run(async () => {
    const amounts = lines.map(l => parseMoney(l.amount))
    if (amounts.some(a => a == null)) { setError(new Error('Có dòng số tiền không hợp lệ.')); return }
    const v = await saveOnce<Voucher>('POST', '/documents', {
      documentType: type, businessEvent: event, voucherDate: date, postingDate: date, description,
      partnerId: partnerId || null, mainAccountId: mainAccountId || null,
      lines: lines.map((l, i) => ({ amount: amounts[i], counterAccountId: l.counterAccountId || null, settleJournalLineId: l.settle || null, description: l.description || null, dueDate: null,
        analysisCodeId: l.analysisCodeId || null })),
    }, rk.key())
    rk.done()
    setCreated(v)
    setLines([{ amount: '', counterAccountId: '', settle: '', description: '' }])
  })

  const upd = (i: number, p: Partial<DocLine>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l)))

  return (
    <>
      <div className="toolbar"><h2>Thu / Chi tiền</h2><button className="primary" onClick={save} disabled={busy}>Lưu nháp</button>
        <button onClick={() => setCounting(true)} title="Đếm tiền theo mệnh giá, so với số dư sổ quỹ, in biên bản">Kiểm kê quỹ…</button></div>
      {counting && <CashCountDialog accounts={accounts} onClose={() => setCounting(false)} />}
      <ErrorBox error={error} />
      {created && <div className="msg ok">Đã tạo {TYPE_VI[created.voucherType]} {created.number} (Nháp, {formatMoney(created.totalDebit)}). Gửi duyệt và ghi sổ ở màn hình Chứng từ.</div>}
      <div className="panel">
        <div className="form-row">
          <label>Loại chứng từ
            <select value={type} onChange={e => setType(e.target.value)}>
              {Object.keys(DEFAULT_EVENTS).map(t => <option key={t} value={t}>{TYPE_VI[t]}</option>)}
            </select>
          </label>
          <label>Nghiệp vụ
            <select value={event} onChange={e => setEvent(e.target.value)}>
              {events.map(e => <option key={e.value} value={e.value}>{e.label}</option>)}
            </select>
          </label>
          <label>Ngày<DateInput value={date} onChange={e => setDate(e.target.value)} /></label>
          <label>{cashKind === 'Cash' ? 'Quỹ' : 'Tài khoản ngân hàng'}
            <AccountSelect accounts={accounts} value={mainAccountId} onChange={setMainAccountId} filter={a => a.cashKind === cashKind} />
          </label>
          {ev.needsPartner && <label>Đối tượng<PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>}
          <label style={{ flex: 1, minWidth: 220 }}>Diễn giải<input value={description} onChange={e => setDescription(e.target.value)} /></label>
        </div>
        <table className="grid">
          <thead><tr><th>#</th><th className="num">Số tiền</th>{ev.needsCounter && <th>TK đối ứng</th>}{needs.settle && <th>Thanh toán cho chứng từ</th>}<th>Diễn giải</th>{codeOptions.length > 0 && <th title="Mã quản trị gắn vào dòng đối ứng (chi phí, doanh thu, công nợ)">Mã phân tích</th>}<th /></tr></thead>
          <tbody>
            {lines.map((l, i) => (
              <tr key={i}>
                <td>{i + 1}</td>
                <td><MoneyInput aria-label="Số tiền" value={l.amount} onChange={e => upd(i, { amount: e.target.value })} /></td>
                {ev.needsCounter && <td><AccountSelect accounts={accounts} value={l.counterAccountId} onChange={id => upd(i, { counterAccountId: id })}
                  filter={a => (counterLine?.constraint === 'CASH_EQUIVALENT' ? a.cashKind !== 'None' : counterLine?.constraint === 'NOT_CASH_EQUIVALENT' ? a.cashKind === 'None' : true)} ariaLabel="TK đối ứng" /></td>}
                {needs.settle && <td>
                  <select value={l.settle} aria-label="Chứng từ công nợ" onChange={e => {
                    const item = openItems.find(o => o.journalLineId === e.target.value)
                    upd(i, { settle: e.target.value, amount: item && !l.amount ? formatMoney(item.open) : l.amount })
                  }}>
                    <option value="">(ứng trước / chưa phân bổ)</option>
                    {openItems.map(o => <option key={o.journalLineId} value={o.journalLineId}>{o.voucherNumber} · {o.postingDate} · còn {formatMoney(o.open)}</option>)}
                  </select></td>}
                <td><input aria-label="Diễn giải dòng" value={l.description} onChange={e => upd(i, { description: e.target.value })} /></td>
                {codeOptions.length > 0 && <td><SearchSelect options={codeOptions} value={l.analysisCodeId ?? ''} onChange={id => upd(i, { analysisCodeId: id })} ariaLabel="Mã phân tích" placeholder="—" width={150} /></td>}
                <td><button aria-label="Xóa dòng" onClick={() => setLines(ls => ls.filter((_, j) => j !== i))}>✕</button></td>
              </tr>
            ))}
          </tbody>
        </table>
        <button style={{ marginTop: 8 }} onClick={() => setLines(ls => [...ls, { amount: '', counterAccountId: '', settle: '', description: '' }])}>+ Thêm dòng</button>
        {evOpt && !evOpt.fromRule && <div className="field-warn">Chưa có quy tắc định khoản đang hiệu lực cho {TYPE_VI[type].toLowerCase()} — khai báo ở Thiết lập → Quy tắc định khoản.</div>}
        <p className="muted">Định khoản được sinh từ quy tắc đang hiệu lực cho nghiệp vụ đã chọn; số tiền thanh toán không được vượt số còn phải thu/trả của chứng từ.</p>
      </div>
    </>
  )
}
