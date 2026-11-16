import { useCallback, useEffect, useState } from 'react'
import { askText } from './dialogs'
import { ApiError, get, post, session } from './api'
import { ErrorBox, useAction } from './common'
import { formatMoney, parseMoney, today } from './money'

const DIFF_VI: Record<string, string> = {
  MissingInBooks: 'Có HĐĐT, chưa ghi sổ', MissingEInvoice: 'Có trong sổ, không có HĐĐT', AmountDifference: 'Lệch tiền hàng', VatDifference: 'Lệch thuế',
  PeriodDifference: 'Lệch kỳ', CancelledButRecorded: 'HĐĐT đã hủy/thay thế nhưng vẫn ghi sổ', PartnerTaxCodeMissing: 'Thiếu MST đối tượng', DeclarationDifference: 'Lệch tờ khai',
}
const MEASURE_VI: Record<string, string> = { None: '(không so sánh)', InputAmountBeforeVat: 'Tiền hàng đầu vào', InputVat: 'Thuế GTGT đầu vào', OutputAmountBeforeVat: 'Doanh số đầu ra', OutputVat: 'Thuế GTGT đầu ra' }
const firstOfMonth = () => today().slice(0, 8) + '01'

/** Thuế GTGT: đối chiếu 3 nguồn HĐĐT – sổ – tờ khai, bảng kê từ sổ, nhập dữ liệu HĐĐT, ghi nhận số đã kê khai. Không lập tờ khai chính thức. */
export function VatView() {
  const [tab, setTab] = useState<'rec' | 'listing' | 'import' | 'decl'>('rec')
  const tabs = { rec: 'Đối chiếu 3 nguồn', listing: 'Bảng kê từ sổ', import: 'Nhập dữ liệu HĐĐT', decl: 'Số đã kê khai' } as const
  return (
    <>
      <div className="toolbar"><h2>Thuế GTGT</h2>
        {(Object.keys(tabs) as (keyof typeof tabs)[]).map(k => <button key={k} className={tab === k ? 'primary' : ''} onClick={() => setTab(k)}>{tabs[k]}</button>)}
      </div>
      {tab === 'rec' && <Reconciliation />}
      {tab === 'listing' && <Listing />}
      {tab === 'import' && <ImportEInvoices />}
      {tab === 'decl' && <Declarations />}
    </>
  )
}

interface Diff { type: string; direction: string | null; key: string; eInvoiceAmount: string | null; booksAmount: string | null; eInvoiceVat: string | null; booksVat: string | null; difference: string; suggestion: string }

function Reconciliation() {
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [data, setData] = useState<{ matched: number; isClean: boolean; differences: Diff[] } | null>(null)
  const [sum, setSum] = useState<{ outputVat: string; inputVat: string; netPayable: string; note: string } | null>(null)
  const { busy, error, run } = useAction()
  const load = () => run(async () => { setData(await get(`/vat/reconciliation?from=${from}&to=${to}`)); setSum(await get(`/vat/payable-summary?from=${from}&to=${to}`)) })
  const m = (v: string | null) => (v == null ? '' : formatMoney(v))
  return (
    <>
      <div className="toolbar">
        <label>Từ ngày<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" disabled={busy} onClick={load}>Đối chiếu</button>
      </div>
      <ErrorBox error={error} />
      {sum && <div className="panel">Thuế đầu ra <strong>{formatMoney(sum.outputVat)}</strong> − đầu vào <strong>{formatMoney(sum.inputVat)}</strong> = <strong>{formatMoney(sum.netPayable)}</strong> <span className="muted">· {sum.note}</span></div>}
      {data && <>
        <div className={`msg ${data.isClean ? 'ok' : 'error'}`}>{data.isClean ? `Khớp hoàn toàn (${data.matched} hóa đơn).` : `${data.matched} hóa đơn khớp; ${data.differences.length} sai lệch cần xử lý.`}</div>
        <div className="scroll"><table className="grid">
          <thead><tr><th>Loại sai lệch</th><th>Hóa đơn / chỉ tiêu</th><th className="num">Tiền HĐĐT</th><th className="num">Tiền sổ</th><th className="num">Thuế HĐĐT</th><th className="num">Thuế sổ</th><th className="num">Chênh lệch</th><th>Hướng xử lý</th></tr></thead>
          <tbody>{data.differences.map((d, i) => <tr key={i}><td>{DIFF_VI[d.type] ?? d.type}</td><td>{d.key}</td><td className="num">{m(d.eInvoiceAmount)}</td><td className="num">{m(d.booksAmount)}</td>
            <td className="num">{m(d.eInvoiceVat)}</td><td className="num">{m(d.booksVat)}</td><td className="num">{m(d.difference)}</td><td style={{ whiteSpace: 'normal', minWidth: 280 }}>{d.suggestion}</td></tr>)}</tbody>
        </table></div>
      </>}
    </>
  )
}

interface ListRow { invoiceId: string; number: string; counterpartyTaxCode: string | null; counterpartyName: string; series: string; invoiceNo: string; invoiceDate: string; postingDate: string; purpose: string; amountBeforeVat: string; vatAmount: string; byRate: { rate: string; amount: string; vat: string }[] }

function Listing() {
  const [direction, setDirection] = useState<'Input' | 'Output'>('Input')
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [rows, setRows] = useState<ListRow[] | null>(null)
  const { busy, error, run } = useAction()
  const load = () => run(async () => setRows(await get(`/vat/listing?direction=${direction}&from=${from}&to=${to}`)))
  return (
    <>
      <div className="toolbar">
        <label>Loại<select value={direction} onChange={e => setDirection(e.target.value as typeof direction)}><option value="Input">Đầu vào</option><option value="Output">Đầu ra</option></select></label>
        <label>Từ ngày<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" disabled={busy} onClick={load}>Xem</button>
      </div>
      <ErrorBox error={error} />
      {rows && <div className="scroll"><table className="grid">
        <thead><tr><th>Ký hiệu/Số HĐ</th><th>Ngày HĐ</th><th>Ngày HT</th><th>MST</th><th>Tên</th><th>Thuế suất</th><th className="num">Tiền hàng</th><th className="num">Thuế</th></tr></thead>
        <tbody>{rows.flatMap(r => r.byRate.map(b => <tr key={r.invoiceId + b.rate}><td>{r.series}/{r.invoiceNo}{r.purpose !== 'Normal' ? ` (${r.purpose})` : ''}</td><td>{r.invoiceDate}</td><td>{r.postingDate}</td>
          <td>{r.counterpartyTaxCode ?? <span className="muted">thiếu MST</span>}</td><td>{r.counterpartyName}</td><td>{b.rate === 'KCT' ? 'KCT' : b.rate + '%'}</td><td className="num">{formatMoney(b.amount)}</td><td className="num">{formatMoney(b.vat)}</td></tr>))}</tbody>
      </table></div>}
    </>
  )
}

function ImportEInvoices() {
  const [text, setText] = useState('')
  const [result, setResult] = useState<{ imported: number; duplicates: number; statusChanges: number; rejected: string[] } | null>(null)
  const { busy, error, setError, run } = useAction()
  const send = () => run(async () => {
    try { JSON.parse(text) } catch { setError(new Error('Nội dung không phải JSON hợp lệ.')); return }
    // Send the raw text: the server fingerprints the exact file content to block re-import.
    const res = await fetch('/api/vat/einvoices/import', { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${session.token}`, 'X-Company-Id': session.companyId ?? '' }, body: text })
    const body = await res.json()
    if (!res.ok) throw new ApiError(res.status, body.code, body.message, body.errors ?? [])
    setResult(body)
  })
  return (
    <>
      <ErrorBox error={error} />
      {result && <div className={`msg ${result.rejected.length ? 'error' : 'ok'}`}>Đã nhập {result.imported}, trùng {result.duplicates}, đổi trạng thái {result.statusChanges}, từ chối {result.rejected.length}.
        {result.rejected.length > 0 && <ul>{result.rejected.map((r, i) => <li key={i}>{r}</li>)}</ul>}</div>}
      <div className="panel">
        <p className="muted">Dán JSON theo <code>schemas/e-invoice-import.schema.json</code>: {'{ "sourceName": "...", "invoices": [ { "direction": "Input", "sellerTaxCode": "...", "series": "...", "number": "...", "issueDate": "2026-10-01", "amountBeforeVat": "...", "vatAmount": "...", "totalPayment": "...", "status": "Original", "rates": [...] } ] }'}. Số tiền dạng chuỗi.</p>
        <textarea aria-label="Dữ liệu hóa đơn điện tử" rows={12} style={{ width: '100%', fontFamily: 'monospace' }} value={text} onChange={e => setText(e.target.value)} />
        <button className="primary" disabled={busy || !text.trim()} onClick={send}>Nhập</button>
      </div>
    </>
  )
}

interface Decl { id: string; periodFrom: string; periodTo: string; status: string; filingReference: string | null; lines: { indicatorCode: string; label: string; declaredAmount: string; measure: string; vatRate: string | null }[] }
interface LineDraft { code: string; label: string; amount: string; measure: string; rate: string }

function Declarations() {
  const [list, setList] = useState<Decl[]>([])
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [lines, setLines] = useState<LineDraft[]>([{ code: '', label: '', amount: '', measure: 'None', rate: '' }])
  const [info, setInfo] = useState('')
  const { busy, error, setError, run } = useAction()
  const load = useCallback(() => run(async () => setList(await get<Decl[]>('/vat/declarations'))), [run])
  useEffect(() => { load() }, [load])
  const upd = (i: number, p: Partial<LineDraft>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l)))
  const save = () => run(async () => {
    const parsed = lines.map(l => ({ ...l, a: parseMoney(l.amount) ?? (l.amount.trim() === '0' ? '0' : null) }))
    if (parsed.some(l => !l.code || l.a == null)) { setError(new Error('Mỗi chỉ tiêu cần mã và số tiền hợp lệ.')); return }
    await post('/vat/declarations', { periodFrom: from, periodTo: to, lines: parsed.map(l => ({ indicatorCode: l.code, label: l.label, declaredAmount: l.a, measure: l.measure, vatRate: l.rate ? parseMoney(l.rate) : null })) })
    setInfo('Đã lưu số đã kê khai.'); await load()
  })
  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="panel">
        <p className="muted">Nhập số liệu đã kê khai theo mẫu tờ khai đang dùng (mã & tên chỉ tiêu do kế toán nhập — hệ thống không mô phỏng mẫu tờ khai). Chỉ tiêu có “so với” sẽ được đối chiếu với sổ.</p>
        <div className="form-row">
          <label>Kỳ từ<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
          <label>đến<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
          <button className="primary" style={{ alignSelf: 'flex-end' }} disabled={busy} onClick={save}>Lưu</button>
        </div>
        <table className="grid">
          <thead><tr><th>Mã chỉ tiêu</th><th>Tên chỉ tiêu</th><th className="num">Số đã kê khai</th><th>So với</th><th className="num">Thuế suất</th><th /></tr></thead>
          <tbody>{lines.map((l, i) => <tr key={i}>
            <td><input aria-label="Mã chỉ tiêu" size={8} value={l.code} onChange={e => upd(i, { code: e.target.value })} /></td>
            <td><input aria-label="Tên chỉ tiêu" value={l.label} onChange={e => upd(i, { label: e.target.value })} /></td>
            <td><input className="num" aria-label="Số đã kê khai" value={l.amount} onChange={e => upd(i, { amount: e.target.value })} /></td>
            <td><select aria-label="So với" value={l.measure} onChange={e => upd(i, { measure: e.target.value })}>{Object.entries(MEASURE_VI).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></td>
            <td><input className="num" aria-label="Thuế suất" size={4} value={l.rate} placeholder="tất cả" onChange={e => upd(i, { rate: e.target.value })} /></td>
            <td><button aria-label="Xóa dòng" onClick={() => setLines(ls => ls.filter((_, j) => j !== i))}>✕</button></td></tr>)}</tbody>
        </table>
        <button style={{ marginTop: 8 }} onClick={() => setLines(ls => [...ls, { code: '', label: '', amount: '', measure: 'None', rate: '' }])}>+ Thêm chỉ tiêu</button>
      </div>
      <table className="grid">
        <thead><tr><th>Kỳ</th><th>Trạng thái</th><th>Chỉ tiêu</th><th /></tr></thead>
        <tbody>{list.map(d => <tr key={d.id}><td>{d.periodFrom} → {d.periodTo}</td><td>{d.status === 'Filed' ? `Đã nộp (${d.filingReference})` : 'Nháp'}</td>
          <td style={{ whiteSpace: 'normal' }}>{d.lines.map(l => `${l.indicatorCode}: ${formatMoney(l.declaredAmount)}`).join('; ')}</td>
          <td>{d.status !== 'Filed' && <button onClick={async () => { const ref = await askText('Mã giao dịch / số tham chiếu nộp tờ khai:'); if (ref) run(async () => { await post(`/vat/declarations/${d.id}/filed`, { filingReference: ref, filedDate: today() }); await load() }) }}>Đánh dấu đã nộp</button>}</td></tr>)}</tbody>
      </table>
    </>
  )
}
