import { useCallback, useEffect, useState } from 'react'
import { htmlTableRows, parseStatementRows } from './bankfile'
import { readSheetFile } from './sheetread'
import { askDate, askText } from './dialogs'
import { get, post, session } from './api'
import { AccountSelect, ErrorBox, PartnerSelect, PrintSheet, printSheet, useAction, useCatalog } from './common'
import { addMoney, amountInWords, formatMoney, parseMoney, today } from './money'

interface PaymentOrder { id: string; number: string; partnerId: string; amount: string; requestedDate: string; description: string; status: string; executedDate: string | null; bankReference: string | null; voucherId: string | null
  bankAccountId?: string; beneficiaryAccountNo?: string | null; beneficiaryBank?: string | null }

const PO_STATUS: Record<string, string> = {
  Created: 'Đã lập', Approved: 'Đã duyệt', SentToBank: 'Đã gửi NH', ExecutedByBank: 'NH đã thực hiện', Posted: 'Đã hạch toán', Reconciled: 'Đã đối chiếu sao kê', Cancelled: 'Đã hủy',
}

export function BankingView() {
  const [tab, setTab] = useState<'orders' | 'recon'>('orders')
  return (
    <>
      <div className="toolbar">
        <h2>Ngân hàng</h2>
        <button className={tab === 'orders' ? 'primary' : ''} onClick={() => setTab('orders')}>Lệnh thanh toán</button>
        <button className={tab === 'recon' ? 'primary' : ''} onClick={() => setTab('recon')}>Sao kê &amp; đối chiếu</button>
      </div>
      {tab === 'orders' ? <PaymentOrders /> : <Reconciliation />}
    </>
  )
}

function PaymentOrders() {
  const { accounts, partners } = useCatalog()
  const [list, setList] = useState<PaymentOrder[]>([])
  const [form, setForm] = useState({ bankAccountId: '', partnerId: '', amount: '', requestedDate: today(), description: '', beneficiaryAccountNo: '', beneficiaryBank: '' })
  const [info, setInfo] = useState('')
  const { busy, error, setError, run } = useAction()
  const load = useCallback(() => run(async () => setList(await get<PaymentOrder[]>('/payment-orders'))), [run])
  useEffect(() => { load() }, [load])
  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })
  const partnerName = (id: string) => partners.find(p => p.id === id)?.code ?? ''
  const [printing, setPrinting] = useState<PaymentOrder | null>(null)
  const [company, setCompany] = useState('')
  useEffect(() => { get<{ id: string; name: string }[]>('/me/companies').then(cs => setCompany(cs.find(c => c.id === session.companyId)?.name ?? '')).catch(() => undefined) }, [])
  useEffect(() => { if (printing) { printSheet(); setPrinting(null) } }, [printing])

  const create = () => {
    const amount = parseMoney(form.amount)
    if (!amount) { setError(new Error('Số tiền không hợp lệ.')); return }
    act(() => post('/payment-orders', { ...form, amount, settleJournalLineId: null, businessEvent: 'SUPPLIER_PAYMENT' }), 'Đã lập lệnh thanh toán.')
  }

  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="panel">
        <div className="form-row">
          <label>Tài khoản chi<AccountSelect accounts={accounts} value={form.bankAccountId} onChange={v => setForm(f => ({ ...f, bankAccountId: v }))} filter={a => a.cashKind === 'Bank'} /></label>
          <label>Nhà cung cấp<PartnerSelect partners={partners} value={form.partnerId} onChange={v => setForm(f => ({ ...f, partnerId: v }))} /></label>
          <label>Số tiền<input className="num" value={form.amount} onChange={e => setForm(f => ({ ...f, amount: e.target.value }))} onBlur={() => { const p = parseMoney(form.amount); if (p) setForm(f => ({ ...f, amount: formatMoney(p) })) }} /></label>
          <label>Ngày đề nghị<input type="date" value={form.requestedDate} onChange={e => setForm(f => ({ ...f, requestedDate: e.target.value }))} /></label>
          <label>Số TK thụ hưởng<input value={form.beneficiaryAccountNo} onChange={e => setForm(f => ({ ...f, beneficiaryAccountNo: e.target.value }))} /></label>
          <label>Ngân hàng thụ hưởng<input value={form.beneficiaryBank} onChange={e => setForm(f => ({ ...f, beneficiaryBank: e.target.value }))} /></label>
          <label style={{ flex: 1, minWidth: 220 }}>Nội dung<input value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} /></label>
          <button className="primary" onClick={create} disabled={busy} style={{ alignSelf: 'flex-end' }}>Lập lệnh</button>
        </div>
      </div>
      {printing && (() => {
        const p = printing, payee = partners.find(x => x.id === p.partnerId), acc = accounts.find(a => a.id === p.bankAccountId)
        const [y, m, day] = p.requestedDate.split('-')
        return (
          <PrintSheet>
            <h1>ỦY NHIỆM CHI</h1>
            <div className="center">Số: <b>{p.number}</b> — <i>Ngày {day} tháng {m} năm {y}</i></div>
            <table>
              <tbody>
                <tr><td style={{ width: '50%' }}><b>Đơn vị trả tiền:</b> {company}<br />Số tài khoản: ....................................<br />Tại ngân hàng: {acc ? acc.name : '....................................'}</td>
                  <td><b>Đơn vị thụ hưởng:</b> {payee?.name}<br />Số tài khoản: {p.beneficiaryAccountNo || '....................................'}<br />Tại ngân hàng: {p.beneficiaryBank || '....................................'}</td></tr>
                <tr><td colSpan={2}>Số tiền bằng số: <b>{formatMoney(p.amount)} đồng</b><br />Số tiền bằng chữ: <i>{amountInWords(p.amount)}</i><br />Nội dung thanh toán: {p.description}</td></tr>
              </tbody>
            </table>
            <div className="signs" style={{ gridTemplateColumns: '1fr 1fr' }}>
              <div><b>ĐƠN VỊ TRẢ TIỀN</b><br />Kế toán trưởng &nbsp;&nbsp;&nbsp;&nbsp;&nbsp; Chủ tài khoản<br /><i>(Ký, họ tên, đóng dấu)</i></div>
              <div><b>NGÂN HÀNG</b><br />Giao dịch viên &nbsp;&nbsp;&nbsp;&nbsp;&nbsp; Kiểm soát<br /><i>(Ký, họ tên)</i></div>
            </div>
            <div className="foot">Bản in từ lệnh thanh toán {p.number} của HAK Accounting. Ngân hàng có thể yêu cầu dùng mẫu ủy nhiệm chi riêng của họ.</div>
          </PrintSheet>
        )
      })()}
      <div className="scroll">
        <table className="grid">
          <thead><tr><th>Số</th><th>Ngày</th><th>NCC</th><th>Nội dung</th><th className="num">Số tiền</th><th>Trạng thái</th><th>Mã GD NH</th><th>Thao tác</th></tr></thead>
          <tbody>{list.map(p => (
            <tr key={p.id}>
              <td>{p.number}</td><td>{p.requestedDate}</td><td>{partnerName(p.partnerId)}</td><td>{p.description}</td>
              <td className="num">{formatMoney(p.amount)}</td><td><span className="badge">{PO_STATUS[p.status] ?? p.status}</span></td><td>{p.bankReference}</td>
              <td style={{ display: 'flex', gap: 4 }}>
                <button onClick={() => setPrinting(p)} title="In ủy nhiệm chi để ký và gửi ngân hàng">In UNC</button>
                {p.status === 'Created' && <button onClick={() => act(() => post(`/payment-orders/${p.id}/approve`), `Đã duyệt ${p.number}.`)}>Duyệt</button>}
                {p.status === 'Approved' && <button onClick={() => act(() => post(`/payment-orders/${p.id}/send`), `Đã ghi nhận gửi ngân hàng ${p.number}.`)}>Đã gửi NH</button>}
                {p.status === 'SentToBank' && <button onClick={async () => {
                  const ref = await askText('Mã giao dịch ngân hàng:'); if (!ref) return
                  const date = await askDate('Ngày ngân hàng thực hiện', today()); if (!date) return
                  act(() => post(`/payment-orders/${p.id}/execute`, { executedDate: date, bankReference: ref }),
                    `Đã ghi nhận ngân hàng thực hiện ${p.number}; đã tạo báo Nợ (Nháp) — duyệt và ghi sổ ở màn hình Chứng từ.`)
                }}>NH đã thực hiện</button>}
                {p.status === 'ExecutedByBank' && <button onClick={() => act(() => post(`/payment-orders/${p.id}/recreate-voucher`), 'Đã lập lại chứng từ hạch toán.')}>Lập lại chứng từ</button>}
                {['Created', 'Approved', 'SentToBank'].includes(p.status) && <button className="danger" onClick={async () => {
                  const reason = await askText('Lý do hủy lệnh:'); if (reason) act(() => post(`/payment-orders/${p.id}/cancel`, { reason }), `Đã hủy ${p.number}.`)
                }}>Hủy</button>}
              </td>
            </tr>))}
            {list.length === 0 && <tr><td colSpan={8} className="muted">Chưa có lệnh thanh toán.</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}

interface Candidate { journalLineId: string; voucherNumber: string; postingDate: string; amount: string; score: number }
interface StatementOnly { id: string; transactionDate: string; amount: string; bankReference: string; description: string; status: string; candidates: Candidate[] }
interface BookOnly { journalLineId: string; voucherNumber: string; postingDate: string; signedAmount: string; description: string | null }
interface Report { bookBalance: string; statementBalance: string | null; statementOnly: StatementOnly[]; bookOnly: BookOnly[]; unexplainedDifference: string | null }
interface Match { id: string; origin: string; score: number; transactionDate: string; amount: string; bankReference: string; voucherNumber: string }

/** "yyyy-mm-dd;-12.000.000;REF;nội dung" → signed canonical amount; null when invalid. */
function parseSigned(s: string): string | null {
  const t = s.trim()
  const neg = t.startsWith('-')
  const p = parseMoney(neg ? t.slice(1) : t)
  return p == null ? null : (neg ? '-' + p : p)
}

function Reconciliation() {
  const { accounts } = useCatalog()
  const [bankAccountId, setBankAccountId] = useState('')
  const [asOf, setAsOf] = useState(today())
  const [csv, setCsv] = useState('')
  const [hdr, setHdr] = useState({ fromDate: today().slice(0, 8) + '01', toDate: today(), opening: '0', closing: '0', sourceName: 'sao-ke.csv' })
  const [report, setReport] = useState<Report | null>(null)
  const [matches, setMatches] = useState<Match[]>([])
  const [info, setInfo] = useState('')
  const [draft, setDraft] = useState<{ lineId: string; accountId: string; description: string; reason: string } | null>(null)
  const { busy, error, setError, run } = useAction()

  const load = () => run(async () => {
    setReport(await get<Report>(`/reports/bank-reconciliation?bankAccountId=${bankAccountId}&asOf=${asOf}`))
    setMatches(await get<Match[]>(`/bank-reconciliation/matches?bankAccountId=${bankAccountId}`))
  })

  // A statement file saved from internet banking: columns are recognised from their headers and the result is put into the
  // boxes below for review before it is imported.
  const readFile = (file: File | undefined) => run(async () => {
    if (!file) return
    const head = new TextDecoder().decode((await file.slice(0, 200).arrayBuffer())).replace(/^\uFEFF/, '').trimStart()
    const rows = head.startsWith('<') ? htmlTableRows(await file.text()) : await readSheetFile(file)
    const st = parseStatementRows(rows)
    if (st.lines.length === 0) throw new Error(st.warnings.join(' ') || 'Không đọc được giao dịch nào từ tệp.')
    setHdr(h => ({ fromDate: st.fromDate ?? h.fromDate, toDate: st.toDate ?? h.toDate, opening: st.opening ? formatMoney(st.opening) : h.opening,
      closing: st.closing ? formatMoney(st.closing) : h.closing, sourceName: file.name }))
    setCsv(st.lines.map(l => `${l.transactionDate};${l.amount};${l.bankReference.replace(/;/g, ',')};${l.description}`).join('\n'))
    setInfo(`Đã đọc ${st.lines.length} giao dịch từ ${file.name}. Kiểm tra số dư và nội dung bên dưới rồi bấm "Nhập & tự khớp".${st.warnings.length ? ' Lưu ý: ' + st.warnings.join(' ') : ''}`)
  })

  const importCsv = () => run(async () => {
    const lines = csv.split(/\r?\n/).map(l => l.trim()).filter(Boolean).map((l, i) => {
      const [transactionDate, amount, bankReference, ...desc] = l.split(';')
      const a = parseSigned(amount ?? '')
      if (!/^\d{4}-\d{2}-\d{2}$/.test(transactionDate ?? '') || !a || !bankReference) throw new Error(`Dòng ${i + 1} sai định dạng: ngày;số tiền;mã GD;nội dung`)
      return { transactionDate, amount: a, bankReference: bankReference.trim(), description: desc.join(';').trim() }
    })
    const opening = parseSigned(hdr.opening), closing = parseSigned(hdr.closing)
    if (!opening || !closing) { setError(new Error('Số dư đầu/cuối không hợp lệ.')); return }
    const sum = addMoney([opening, ...lines.map(l => l.amount)])
    if (sum !== closing) { setError(new Error(`Số dư đầu + phát sinh = ${formatMoney(sum)} khác số dư cuối ${formatMoney(closing)}.`)); return }
    const st = await post<{ id: string }>('/bank-statements', { bankAccountId, fromDate: hdr.fromDate, toDate: hdr.toDate, openingBalance: opening, closingBalance: closing, sourceName: hdr.sourceName, sourceContent: csv, lines })
    const r = await post<{ matched: number; pending: number; unmatched: number }>(`/bank-statements/${st.id}/auto-match`)
    setInfo(`Đã nhập ${lines.length} giao dịch. Tự khớp ${r.matched}, chờ xác nhận ${r.pending}, chưa khớp ${r.unmatched}.`)
    setCsv('')
    await load()
  })

  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="toolbar">
        <label>Tài khoản ngân hàng<AccountSelect accounts={accounts} value={bankAccountId} onChange={setBankAccountId} filter={a => a.cashKind === 'Bank'} /></label>
        <label>Đến ngày<input type="date" value={asOf} onChange={e => setAsOf(e.target.value)} /></label>
        <button className="primary" onClick={load} disabled={busy || !bankAccountId}>Xem đối chiếu</button>
      </div>
      <details className="panel">
        <summary>Nhập sao kê (chọn tệp của ngân hàng, hoặc dán CSV: <code>yyyy-mm-dd;số tiền (+ vào / − ra);mã GD;nội dung</code>)</summary>
        <div className="form-row" style={{ marginTop: 8 }}>
          <label>Tệp sao kê tải từ ngân hàng (.xls, .xlsx, .csv)<input type="file" accept=".xls,.xlsx,.csv,.txt,.htm,.html"
            onChange={e => { readFile(e.target.files?.[0]); e.target.value = '' }} /></label>
        </div>
        <div className="form-row">
          <label>Từ ngày<input type="date" value={hdr.fromDate} onChange={e => setHdr(h => ({ ...h, fromDate: e.target.value }))} /></label>
          <label>Đến ngày<input type="date" value={hdr.toDate} onChange={e => setHdr(h => ({ ...h, toDate: e.target.value }))} /></label>
          <label>Số dư đầu<input className="num" value={hdr.opening} onChange={e => setHdr(h => ({ ...h, opening: e.target.value }))} /></label>
          <label>Số dư cuối<input className="num" value={hdr.closing} onChange={e => setHdr(h => ({ ...h, closing: e.target.value }))} /></label>
          <label>Tên file<input value={hdr.sourceName} onChange={e => setHdr(h => ({ ...h, sourceName: e.target.value }))} /></label>
        </div>
        <textarea aria-label="Nội dung sao kê" rows={6} style={{ width: '100%' }} value={csv} onChange={e => setCsv(e.target.value)} placeholder="2026-03-06;-12.000.000;FT26065ABC123;TT hoa don" />
        <button className="primary" onClick={importCsv} disabled={busy || !bankAccountId || !csv.trim()}>Nhập &amp; tự khớp</button>
      </details>
      {report && (
        <>
          <div className={`msg ${report.unexplainedDifference === '0' ? 'ok' : 'error'}`}>
            Số dư sổ: <strong>{formatMoney(report.bookBalance)}</strong> · Số dư sao kê: <strong>{report.statementBalance == null ? 'chưa có sao kê' : formatMoney(report.statementBalance)}</strong>
            {report.unexplainedDifference != null && <> · Chênh lệch chưa giải thích: <strong>{formatMoney(report.unexplainedDifference)}</strong></>}
          </div>
          <div className="panel">
            <strong>Giao dịch chỉ có trên sao kê ({report.statementOnly.length})</strong>
            <table className="grid">
              <thead><tr><th>Ngày</th><th>Mã GD</th><th>Nội dung</th><th className="num">Số tiền</th><th>Trạng thái / ứng viên</th></tr></thead>
              <tbody>{report.statementOnly.map(l => (
                <tr key={l.id}><td>{l.transactionDate}</td><td>{l.bankReference}</td><td>{l.description}</td><td className="num">{formatMoney(l.amount)}</td>
                  <td>{l.candidates.length === 0 ? <span className="muted">Chưa có bút toán tương ứng </span> : l.candidates.map(c => (
                    <button key={c.journalLineId} onClick={() => run(async () => { await post('/bank-reconciliation/confirm', { statementLineId: l.id, journalLineId: c.journalLineId }); await load() })}>
                      Khớp {c.voucherNumber} ({c.postingDate}, điểm {c.score})</button>))}
                    <button onClick={() => run(async () => {
                      const s = await get<{ accountId: string | null; reason: string }>(`/bank-statement-lines/${l.id}/suggestion`)
                      setDraft({ lineId: l.id, accountId: s.accountId ?? '', description: l.description, reason: s.reason })
                    })}>Tạo chứng từ</button>
                    {draft?.lineId === l.id && <div className="form-row" style={{ marginTop: 6 }}>
                      <span className="muted">{draft.reason}</span>
                      <label>TK đối ứng<AccountSelect accounts={accounts} value={draft.accountId} onChange={id => setDraft(d => d && { ...d, accountId: id })} filter={a => a.id !== bankAccountId && !a.requiresPartner} /></label>
                      <label>Diễn giải<input value={draft.description} onChange={e => setDraft(d => d && { ...d, description: e.target.value })} /></label>
                      <button className="primary" disabled={busy || !draft.accountId} onClick={() => run(async () => {
                        const v = await post<{ number: string }>(`/bank-statement-lines/${l.id}/voucher`, { counterAccountId: draft.accountId, description: draft.description || null })
                        setDraft(null); setInfo(`Đã tạo chứng từ nháp ${v.number}. Gửi duyệt, ghi sổ ở màn hình Chứng từ, rồi bấm Xem đối chiếu và Khớp.`)
                      })}>Tạo nháp</button>
                      <button onClick={() => setDraft(null)}>Hủy</button></div>}</td></tr>))}
              </tbody>
            </table>
          </div>
          <div className="panel">
            <strong>Bút toán chỉ có trên sổ ({report.bookOnly.length})</strong>
            <table className="grid">
              <thead><tr><th>Ngày</th><th>Số CT</th><th>Diễn giải</th><th className="num">Số tiền</th></tr></thead>
              <tbody>{report.bookOnly.map(b => <tr key={b.journalLineId}><td>{b.postingDate}</td><td>{b.voucherNumber}</td><td>{b.description}</td><td className="num">{formatMoney(b.signedAmount)}</td></tr>)}</tbody>
            </table>
          </div>
          <div className="panel">
            <strong>Đã đối chiếu ({matches.length})</strong>
            <table className="grid">
              <thead><tr><th>Ngày</th><th>Mã GD</th><th>Số CT</th><th className="num">Số tiền</th><th>Cách khớp</th><th /></tr></thead>
              <tbody>{matches.map(m => (
                <tr key={m.id}><td>{m.transactionDate}</td><td>{m.bankReference}</td><td>{m.voucherNumber}</td><td className="num">{formatMoney(m.amount)}</td>
                  <td>{m.origin === 'Auto' ? `Tự động (điểm ${m.score})` : 'Thủ công'}</td>
                  <td><button className="danger" onClick={async () => { const reason = await askText('Lý do hủy đối chiếu:'); if (reason) run(async () => { await post(`/bank-reconciliation/${m.id}/unmatch`, { reason }); await load() }) }}>Hủy khớp</button></td></tr>))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  )
}
