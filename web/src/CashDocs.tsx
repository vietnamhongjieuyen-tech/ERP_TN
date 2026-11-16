import { useEffect, useMemo, useState } from 'react'
import type { AnalysisCode } from './Analysis'
import { SearchSelect } from './combo'
import { get, post, type OpenItem, type Voucher } from './api'
import { AccountSelect, ErrorBox, PartnerSelect, TYPE_VI, useAction, useCatalog } from './common'
import { formatMoney, parseMoney, today } from './money'

const EVENTS: Record<string, { value: string; label: string; needsPartner: boolean; needsCounter: boolean }[]> = {
  CR: [{ value: 'CUSTOMER_RECEIPT', label: 'Thu tiền khách hàng', needsPartner: true, needsCounter: false }],
  BR: [{ value: 'CUSTOMER_RECEIPT', label: 'Khách hàng chuyển khoản', needsPartner: true, needsCounter: false }],
  CP: [{ value: 'EXPENSE', label: 'Chi phí', needsPartner: false, needsCounter: true },
       { value: 'SUPPLIER_PAYMENT', label: 'Trả nhà cung cấp', needsPartner: true, needsCounter: false }],
  BP: [{ value: 'SUPPLIER_PAYMENT', label: 'Trả nhà cung cấp', needsPartner: true, needsCounter: false },
       { value: 'INTERNAL_TRANSFER', label: 'Chuyển tiền nội bộ', needsPartner: false, needsCounter: true }],
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
  const { busy, error, setError, run } = useAction()
  const [codes, setCodes] = useState<AnalysisCode[]>([])
  useEffect(() => { get<AnalysisCode[]>('/analysis-codes').then(setCodes).catch(() => setCodes([])) }, [])
  const codeOptions = useMemo(() => codes.filter(c => c.isActive).map(c => ({ id: c.id, code: c.code, name: `${c.name} (${c.kind})` })), [codes])
  const ev = EVENTS[type].find(e => e.value === event) ?? EVENTS[type][0]
  const cashKind = type === 'CR' || type === 'CP' ? 'Cash' : 'Bank'
  const arapRole = event === 'CUSTOMER_RECEIPT' ? 'Receivable' : event === 'SUPPLIER_PAYMENT' ? 'Payable' : null

  useEffect(() => { setEvent(EVENTS[type][0].value); setMainAccountId('') }, [type])
  useEffect(() => {
    const acc = accounts.find(a => a.openItemNature === arapRole)
    if (!acc || !partnerId) { setOpenItems([]); return }
    const q = new URLSearchParams({ accountId: acc.id, partnerId, asOf: date })
    get<OpenItem[]>(`/settlement/open-items?${q}`)
      // Foreign-currency invoices are settled at their book rate through "Ngoại tệ → Thu/chi theo hóa đơn".
      .then(items => setOpenItems(items.filter(i => i.side === (arapRole === 'Receivable' ? 'Debit' : 'Credit') && !i.foreignCurrency)))
      .catch(() => setOpenItems([]))
  }, [accounts, arapRole, partnerId, date])

  const save = () => run(async () => {
    const amounts = lines.map(l => parseMoney(l.amount))
    if (amounts.some(a => a == null)) { setError(new Error('Có dòng số tiền không hợp lệ.')); return }
    const v = await post<Voucher>('/documents', {
      documentType: type, businessEvent: event, voucherDate: date, postingDate: date, description,
      partnerId: partnerId || null, mainAccountId: mainAccountId || null,
      lines: lines.map((l, i) => ({ amount: amounts[i], counterAccountId: l.counterAccountId || null, settleJournalLineId: l.settle || null, description: l.description || null, dueDate: null,
        analysisCodeId: l.analysisCodeId || null })),
    })
    setCreated(v)
    setLines([{ amount: '', counterAccountId: '', settle: '', description: '' }])
  })

  const upd = (i: number, p: Partial<DocLine>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l)))

  return (
    <>
      <div className="toolbar"><h2>Thu / Chi tiền</h2><button className="primary" onClick={save} disabled={busy}>Lưu nháp</button></div>
      <ErrorBox error={error} />
      {created && <div className="msg ok">Đã tạo {TYPE_VI[created.voucherType]} {created.number} (Nháp, {formatMoney(created.totalDebit)}). Gửi duyệt và ghi sổ ở màn hình Chứng từ.</div>}
      <div className="panel">
        <div className="form-row">
          <label>Loại chứng từ
            <select value={type} onChange={e => setType(e.target.value)}>
              {Object.keys(EVENTS).map(t => <option key={t} value={t}>{TYPE_VI[t]}</option>)}
            </select>
          </label>
          <label>Nghiệp vụ
            <select value={event} onChange={e => setEvent(e.target.value)}>
              {EVENTS[type].map(e => <option key={e.value} value={e.value}>{e.label}</option>)}
            </select>
          </label>
          <label>Ngày<input type="date" value={date} onChange={e => setDate(e.target.value)} /></label>
          <label>{cashKind === 'Cash' ? 'Quỹ' : 'Tài khoản ngân hàng'}
            <AccountSelect accounts={accounts} value={mainAccountId} onChange={setMainAccountId} filter={a => a.cashKind === cashKind} />
          </label>
          {ev.needsPartner && <label>Đối tượng<PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>}
          <label style={{ flex: 1, minWidth: 220 }}>Diễn giải<input value={description} onChange={e => setDescription(e.target.value)} /></label>
        </div>
        <table className="grid">
          <thead><tr><th>#</th><th className="num">Số tiền</th>{ev.needsCounter && <th>TK đối ứng</th>}{ev.needsPartner && <th>Thanh toán cho chứng từ</th>}<th>Diễn giải</th>{codeOptions.length > 0 && <th title="Mã quản trị gắn vào dòng đối ứng (chi phí, doanh thu, công nợ)">Mã phân tích</th>}<th /></tr></thead>
          <tbody>
            {lines.map((l, i) => (
              <tr key={i}>
                <td>{i + 1}</td>
                <td><input className="num" aria-label="Số tiền" value={l.amount} onChange={e => upd(i, { amount: e.target.value })}
                  onBlur={() => { const p = parseMoney(l.amount); if (p) upd(i, { amount: formatMoney(p) }) }} /></td>
                {ev.needsCounter && <td><AccountSelect accounts={accounts} value={l.counterAccountId} onChange={id => upd(i, { counterAccountId: id })}
                  filter={a => (event === 'INTERNAL_TRANSFER' ? a.cashKind !== 'None' : a.cashKind === 'None')} ariaLabel="TK đối ứng" /></td>}
                {ev.needsPartner && <td>
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
        <p className="muted">Định khoản được sinh từ quy tắc đang hiệu lực cho nghiệp vụ đã chọn; số tiền thanh toán không được vượt số còn phải thu/trả của chứng từ.</p>
      </div>
    </>
  )
}
