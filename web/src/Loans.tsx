import { useCallback, useEffect, useState } from 'react'
import { MoneyInput } from './MoneyInput'
import { askConfirm, askText } from './dialogs'
import { get, post, type Period } from './api'
import { AccountSelect, ErrorBox, ExportButton, PartnerSelect, useAction, useCatalog, usePrintBook } from './common'
import { LoanBookPrint, type LedgerData, type LoanInfo } from './books'
import { useCompanyInfo } from './forms'
import { formatMoney, parseMoney, parseRate, today } from './money'
import { DateInput } from './DateInput'

interface LoanRow {
  id: string; code: string; description: string; lenderCode: string; accountCode: string; currency: string | null
  status: 'Draft' | 'Active' | 'Closed' | 'Cancelled'; contractAmount: string; drawn: string; repaid: string; outstanding: string
  outstandingForeign: string | null; currentRatePercent: string | null; maturityDate: string; dueWithin12Months: boolean; interestAccrued: string
}
interface Candidate { journalLineId: string; voucherNumber: string; postingDate: string; side: string; amount: string; foreignAmount: string | null; description: string | null }
interface Run { id: string; periodStart: string; periodEnd: string; status: 'Active' | 'Reversed'; voucherId: string | null }
interface ReconRow { accountId: string; accountCode: string; lenderId: string; lenderCode: string; ledgerBalance: string; registerBalance: string; revaluationAdjustments: string; difference: string }

const STATUS_VI = { Draft: 'Nháp', Active: 'Hiệu lực', Closed: 'Đã tất toán', Cancelled: 'Đã hủy' } as const

export function LoansView() {
  const { accounts, partners } = useCatalog()
  const [asOf, setAsOf] = useState(today())
  const [rows, setRows] = useState<LoanRow[]>([])
  const [runs, setRuns] = useState<Run[]>([])
  const [periods, setPeriods] = useState<Period[]>([])
  const [recon, setRecon] = useState<ReconRow[] | null>(null)
  const [creating, setCreating] = useState(false)
  const [linking, setLinking] = useState<{ loan: LoanRow; items: Candidate[]; picked: string[] } | null>(null)
  const [rateFor, setRateFor] = useState<LoanRow | null>(null)
  const [periodId, setPeriodId] = useState('')
  const [fxRates, setFxRates] = useState<Record<string, string>>({})
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const company = useCompanyInfo()
  const [book, setBook] = usePrintBook<'S34'>()
  const [loanBook, setLoanBook] = useState<{ ledger: LedgerData; loan: LoanInfo; from: string } | null>(null)
  // Sổ chi tiết tiền vay (S34-DN): the principal account's ledger for the lender, from the start of the year of the chosen date.
  const printLoan = (r: LoanRow) => run(async () => {
    const accountId = accounts.find(a => a.code === r.accountCode)?.id, partner = partners.find(p => p.code === r.lenderCode)
    if (!accountId || !partner) throw new Error('Không tìm thấy tài khoản / bên cho vay của khế ước.')
    const from = asOf.slice(0, 4) + '-01-01'
    const ledger = await get<LedgerData>(`/reports/account-ledger?${new URLSearchParams({ accountId, partnerId: partner.id, from, to: asOf })}`)
    setLoanBook({ ledger, from, loan: { code: r.code, description: r.description, lender: `${partner.code} – ${partner.name}`, ratePercent: r.currentRatePercent, maturityDate: r.maturityDate } })
    setBook('S34')
  })

  const load = useCallback(() => run(async () => {
    setRows(await get<LoanRow[]>(`/loans?asOf=${asOf}`))
    setRuns(await get<Run[]>('/loans/interest-runs'))
    setPeriods(await get<Period[]>('/periods'))
  }), [run, asOf])
  useEffect(() => { load() }, [load])

  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })
  const lastActive = runs.find(r => r.status === 'Active')
  const nextPeriod = periods.find(p => p.status === 'Open' && (!lastActive || p.startDate > lastActive.periodEnd))
  const foreignCurrencies = [...new Set(rows.filter(r => r.status === 'Active' && r.currency).map(r => r.currency!))].sort()

  const accrue = () => act(async () => {
    const id = periodId || nextPeriod?.id
    if (!id) throw new Error('Chọn kỳ trích lãi.')
    const rates = foreignCurrencies.map(c => {
      const r = parseRate(fxRates[c] ?? '')
      if (!r) throw new Error(`Nhập tỷ giá ${c} để quy đổi lãi vay ngoại tệ.`)
      return { currency: c, debitBalanceRate: r, creditBalanceRate: r }
    })
    await post('/loans/interest-runs', { periodId: id, rates })
    setPeriodId('')
  }, 'Đã trích lãi vay và ghi sổ.')

  return (
    <>
      <div className="toolbar">
        <h2>Vay — khế ước, giải ngân, trả nợ, lãi vay</h2>
        <label>Tại ngày <DateInput value={asOf} onChange={e => setAsOf(e.target.value)} /></label>
        <button onClick={() => load()}>Tải lại</button>
        <button className="primary" onClick={() => setCreating(true)}>Thêm khế ước</button>
        <ExportButton fileName={'So_vay_' + asOf} sheet="Sổ theo dõi vay" columns={[{ header: 'Khế ước' }, { header: 'Nội dung', width: 30 }, { header: 'Bên cho vay' }, { header: 'TK' }, { header: 'Tiền' }, { header: 'Trạng thái' },
          ...['Hạn mức', 'Đã giải ngân', 'Đã trả', 'Dư nợ', 'Dư nợ nguyên tệ', 'Lãi suất %'].map(h => ({ header: h, numeric: true })), { header: 'Đáo hạn' }, { header: 'Đến hạn ≤ 12 tháng' }, { header: 'Lãi đã trích', numeric: true }]}
          rows={rows.map(r => [r.code, r.description, r.lenderCode, r.accountCode, r.currency ?? 'VND', STATUS_VI[r.status], r.contractAmount, r.drawn, r.repaid, r.outstanding, r.outstandingForeign, r.currentRatePercent, r.maturityDate, r.dueWithin12Months ? 'Có' : '', r.interestAccrued])} />
      </div>
      {loanBook && book === 'S34' && <LoanBookPrint company={company.name} address={company.address} ledger={loanBook.ledger} loan={loanBook.loan} from={loanBook.from} to={asOf} />}
      <p className="muted">Giải ngân / trả nợ hạch toán bằng chứng từ thông thường (Nợ tiền / Có TK vay chi tiết bên cho vay) rồi gắn vào khế ước. Lãi tính theo dư nợ từng ngày × lãi suất từng ngày ÷ số ngày cơ sở; ngày giải ngân tính lãi, ngày trả nợ không tính [CẦN XÁC MINH theo hợp đồng].</p>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}

      {creating && <NewLoan accounts={accounts} partners={partners} onDone={msg => { setCreating(false); if (msg) { setInfo(msg); load() } }} />}

      <div className="scroll"><table className="grid">
        <thead><tr><th>Khế ước</th><th>Bên cho vay</th><th>TK</th><th>Tiền</th><th>Trạng thái</th><th className="num">Hạn mức</th><th className="num">Đã giải ngân</th>
          <th className="num">Đã trả</th><th className="num">Dư nợ</th><th className="num">Lãi suất %</th><th>Đáo hạn</th><th className="num">Lãi đã trích</th><th>Thao tác</th></tr></thead>
        <tbody>{rows.map(r => (
          <tr key={r.id}>
            <td title={r.description}>{r.code}</td><td>{r.lenderCode}</td><td>{r.accountCode}</td><td>{r.currency ?? 'VND'}</td>
            <td><span className={`badge ${r.status}`}>{STATUS_VI[r.status]}</span>{r.dueWithin12Months && <span className="muted"> · đến hạn ≤ 12 tháng</span>}</td>
            <td className="num">{formatMoney(r.contractAmount)}</td><td className="num">{formatMoney(r.drawn)}</td><td className="num">{formatMoney(r.repaid)}</td>
            <td className="num">{formatMoney(r.outstanding)}{r.outstandingForeign != null && <div className="muted">{formatMoney(r.outstandingForeign)} {r.currency}</div>}</td>
            <td className="num">{formatMoney(r.currentRatePercent)}</td><td>{r.maturityDate}</td><td className="num">{formatMoney(r.interestAccrued)}</td>
            <td style={{ display: 'flex', gap: 6 }}>
              {r.status !== 'Draft' && r.status !== 'Cancelled' && <button disabled={busy} title="Mẫu số S34-DN, Phụ lục III TT 99/2025 (từ đầu năm đến ngày đang chọn)" onClick={() => printLoan(r)}>In S34-DN</button>}
              {r.status === 'Draft' && <>
                <button disabled={busy} onClick={() => act(() => post(`/loans/${r.id}/activate`), `Đã duyệt khế ước ${r.code}.`)}>Duyệt</button>
                <button disabled={busy} onClick={async () => { const reason = await askText('Lý do hủy khế ước:'); if (reason) act(() => post(`/loans/${r.id}/cancel`, { reason }), `Đã hủy ${r.code}.`) }}>Hủy</button>
              </>}
              {r.status === 'Active' && <>
                <button disabled={busy} onClick={() => run(async () => setLinking({ loan: r, items: await get<Candidate[]>(`/loans/${r.id}/candidates`), picked: [] }))}>Gắn giải ngân / trả nợ</button>
                <button disabled={busy} onClick={() => setRateFor(r)}>Đổi lãi suất</button>
                <button disabled={busy} onClick={async () => { if (await askConfirm(`Tất toán khế ước ${r.code}?`)) act(() => post(`/loans/${r.id}/close`), `Đã tất toán ${r.code}.`) }}>Tất toán</button>
              </>}
            </td>
          </tr>))}
        </tbody>
      </table></div>

      {linking && (
        <section className="panel">
          <div className="toolbar"><h3>Gắn bút toán vào khế ước {linking.loan.code}</h3><button onClick={() => setLinking(null)}>Đóng</button></div>
          {linking.items.length === 0 && <p className="muted">Không có bút toán chưa gắn trên TK {linking.loan.accountCode} của {linking.loan.lenderCode}.</p>}
          {linking.items.length > 0 && <table className="grid"><thead><tr><th></th><th>Chứng từ</th><th>Ngày</th><th>Loại</th><th className="num">Số tiền</th><th className="num">Nguyên tệ</th><th>Diễn giải</th></tr></thead>
            <tbody>{linking.items.map(c => <tr key={c.journalLineId}>
              <td><input type="checkbox" aria-label={`Chọn ${c.voucherNumber}`} checked={linking.picked.includes(c.journalLineId)}
                onChange={e => setLinking(l => l && ({ ...l, picked: e.target.checked ? [...l.picked, c.journalLineId] : l.picked.filter(x => x !== c.journalLineId) }))} /></td>
              <td>{c.voucherNumber}</td><td>{c.postingDate}</td><td>{c.side}</td><td className="num">{formatMoney(c.amount)}</td><td className="num">{formatMoney(c.foreignAmount)}</td><td>{c.description}</td>
            </tr>)}</tbody></table>}
          <button className="primary" disabled={busy || linking.picked.length === 0} onClick={() => act(async () => {
            await post(`/loans/${linking.loan.id}/links`, { journalLineIds: linking.picked }); setLinking(null)
          }, 'Đã gắn bút toán vào khế ước.')}>Gắn</button>
        </section>
      )}

      {rateFor && <RateForm loan={rateFor} busy={busy} onCancel={() => setRateFor(null)}
        onSubmit={(effectiveFrom, rate, note) => act(async () => { await post(`/loans/${rateFor.id}/rates`, { effectiveFrom, annualRatePercent: rate, note }); setRateFor(null) },
          `Đã thêm lãi suất mới cho ${rateFor.code}.`)} />}

      <section className="panel">
        <h3>Trích lãi vay theo kỳ</h3>
        <p className="muted">Trích lần lượt từng kỳ; hủy từ kỳ gần nhất trở về trước. Lãi vay ngoại tệ quy đổi theo tỷ giá nhập dưới đây.</p>
        <div className="form-row">
          <select value={periodId || nextPeriod?.id || ''} onChange={e => setPeriodId(e.target.value)} aria-label="Kỳ trích lãi">
            <option value="">— chọn kỳ —</option>
            {periods.filter(p => p.status === 'Open').map(p => <option key={p.id} value={p.id}>{p.startDate.slice(0, 7)}</option>)}
          </select>
          {foreignCurrencies.map(c => <label key={c}>Tỷ giá {c} <MoneyInput kind="rate" value={fxRates[c] ?? ''} onChange={e => setFxRates(x => ({ ...x, [c]: e.target.value }))} /></label>)}
          <button className="primary" disabled={busy} onClick={accrue}>Trích lãi</button>
        </div>
        <table className="grid"><thead><tr><th>Kỳ</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
          <tbody>{runs.map(r => <tr key={r.id}><td>{r.periodStart.slice(0, 7)}</td>
            <td>{r.status === 'Active' ? (r.voucherId ? 'Hiệu lực' : 'Hiệu lực (không phát sinh)') : 'Đã hủy'}</td>
            <td>{r.id === lastActive?.id && <button disabled={busy} onClick={async () => {
              const reason = await askText('Lý do hủy lần trích lãi:')
              if (reason) act(() => post(`/loans/interest-runs/${r.id}/reverse`, { reason }), 'Đã hủy lần trích lãi.')
            }}>Hủy</button>}</td></tr>)}</tbody></table>
      </section>

      <section className="panel">
        <div className="toolbar"><h3>Đối chiếu sổ vay ↔ sổ cái tại {asOf}</h3>
          <button onClick={() => run(async () => setRecon(await get<ReconRow[]>(`/loans/reconciliation?asOf=${asOf}`)))}>Đối chiếu</button></div>
        {recon && <table className="grid"><thead><tr><th>TK</th><th>Bên cho vay</th><th className="num">Sổ cái (dư Có)</th><th className="num">Theo khế ước</th><th className="num">Đánh giá lại tỷ giá</th><th className="num">Chênh lệch</th></tr></thead>
          <tbody>{recon.map(r => <tr key={`${r.accountId}|${r.lenderId}`} className={r.difference !== '0' ? 'warn' : ''}><td>{r.accountCode}</td><td>{r.lenderCode}</td>
            <td className="num">{formatMoney(r.ledgerBalance)}</td><td className="num">{formatMoney(r.registerBalance)}</td>
            <td className="num">{formatMoney(r.revaluationAdjustments)}</td><td className="num">{formatMoney(r.difference)}</td></tr>)}</tbody></table>}
        {recon && <p className="muted">Chênh lệch là bút toán trên TK vay của bên cho vay chưa gắn vào khế ước nào.</p>}
      </section>
    </>
  )
}

function RateForm({ loan, busy, onSubmit, onCancel }: { loan: LoanRow; busy: boolean; onSubmit: (d: string, rate: string, note: string) => void; onCancel: () => void }) {
  const [date, setDate] = useState(today())
  const [rate, setRate] = useState('')
  const [note, setNote] = useState('')
  const parsed = parseMoney(rate)
  return (
    <section className="panel">
      <h3>Lãi suất mới cho {loan.code}</h3>
      <div className="form-row">
        <label>Hiệu lực từ <DateInput value={date} onChange={e => setDate(e.target.value)} /></label>
        <label>Lãi suất %/năm <input inputMode="decimal" value={rate} onChange={e => setRate(e.target.value)} /></label>
        <label>Căn cứ <input value={note} onChange={e => setNote(e.target.value)} placeholder="Phụ lục / thông báo lãi suất số…" /></label>
      </div>
      <div style={{ display: 'flex', gap: 8 }}>
        <button className="primary" disabled={busy || !parsed || !note.trim()} onClick={() => onSubmit(date, parsed!, note)}>Lưu</button>
        <button onClick={onCancel}>Hủy</button>
      </div>
    </section>
  )
}

function NewLoan({ accounts, partners, onDone }: { accounts: ReturnType<typeof useCatalog>['accounts']; partners: ReturnType<typeof useCatalog>['partners']; onDone: (msg?: string) => void }) {
  const [f, setF] = useState({
    code: '', description: '', lenderId: '', principalAccountId: '', interestExpenseAccountId: '', accruedInterestAccountId: '',
    currency: '', contractAmount: '', startDate: today(), maturityDate: '', annualRatePercent: '', dayCount: 'Actual365', interestFrom: '',
  })
  const { busy, error, run } = useAction()
  const set = (k: keyof typeof f, v: string) => setF(x => ({ ...x, [k]: v }))
  const submit = () => run(async () => {
    const amount = parseMoney(f.contractAmount), rate = parseMoney(f.annualRatePercent)
    if (!amount || rate == null) throw new Error('Hạn mức và lãi suất phải là số hợp lệ.')
    const l = await post<{ code: string }>('/loans', { ...f, contractAmount: amount, annualRatePercent: rate, currency: f.currency || null, interestFrom: f.interestFrom || null })
    onDone(`Đã lập khế ước ${l.code} (Nháp). Người khác bấm "Duyệt" để khế ước có hiệu lực.`)
  })
  return (
    <section className="panel">
      <h3>Khế ước vay mới</h3>
      <ErrorBox error={error} />
      <div className="form-grid">
        <label>Số khế ước <input value={f.code} onChange={e => set('code', e.target.value)} /></label>
        <label>Nội dung <input value={f.description} onChange={e => set('description', e.target.value)} /></label>
        <label>Bên cho vay <PartnerSelect partners={partners} value={f.lenderId} onChange={v => set('lenderId', v)} /></label>
        <label>TK gốc vay <AccountSelect accounts={accounts} value={f.principalAccountId} onChange={v => set('principalAccountId', v)} filter={a => a.cashKind === 'None' && a.openItemNature === 'None'} ariaLabel="TK gốc vay" /></label>
        <label>TK chi phí lãi <AccountSelect accounts={accounts} value={f.interestExpenseAccountId} onChange={v => set('interestExpenseAccountId', v)} ariaLabel="TK chi phí lãi" /></label>
        <label>TK lãi phải trả <AccountSelect accounts={accounts} value={f.accruedInterestAccountId} onChange={v => set('accruedInterestAccountId', v)} ariaLabel="TK lãi phải trả" /></label>
        <label>Loại tiền (để trống = VND) <input value={f.currency} onChange={e => set('currency', e.target.value.toUpperCase())} maxLength={3} /></label>
        <label>Hạn mức / số tiền vay <MoneyInput value={f.contractAmount} onChange={e => set('contractAmount', e.target.value)} /></label>
        <label>Ngày bắt đầu <DateInput value={f.startDate} onChange={e => set('startDate', e.target.value)} /></label>
        <label>Ngày đáo hạn <DateInput value={f.maturityDate} onChange={e => set('maturityDate', e.target.value)} /></label>
        <label>Lãi suất %/năm <input inputMode="decimal" value={f.annualRatePercent} onChange={e => set('annualRatePercent', e.target.value)} /></label>
        <label>Cơ sở tính ngày <select value={f.dayCount} onChange={e => set('dayCount', e.target.value)}><option value="Actual365">Thực tế / 365</option><option value="Actual360">Thực tế / 360</option></select></label>
        <label>Tính lãi từ ngày (khế ước chuyển từ hệ thống cũ) <DateInput value={f.interestFrom} onChange={e => set('interestFrom', e.target.value)} /></label>
      </div>
      <div style={{ display: 'flex', gap: 8 }}>
        <button className="primary" disabled={busy || !f.code || !f.lenderId || !f.principalAccountId || !f.maturityDate} onClick={submit}>Lưu nháp</button>
        <button onClick={() => onDone()}>Hủy</button>
      </div>
    </section>
  )
}
