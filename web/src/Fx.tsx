import { useCallback, useEffect, useState } from 'react'
import { askText } from './dialogs'
import { get, post, type OpenItem } from './api'
import { AccountSelect, ErrorBox, PartnerSelect, useAction, useCatalog } from './common'
import { formatMoney, parseMoney, parseRate, today } from './money'

interface BalanceRow {
  accountId: string; accountCode: string; partnerId: string | null; partnerCode: string | null; currency: string
  foreignBalance: string; bookBalance: string; bookRate: string | null; revalued: boolean; note: string | null
}
interface Run { id: string; revaluationDate: string; status: 'Active' | 'Reversed'; voucherId: string | null; rateSource: string; ratesJson: string }

export function FxView() {
  const [asOf, setAsOf] = useState(today())
  const [rows, setRows] = useState<BalanceRow[]>([])
  const [runs, setRuns] = useState<Run[]>([])
  const [rates, setRates] = useState<Record<string, { debit: string; credit: string }>>({})
  const [source, setSource] = useState('')
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()

  const load = useCallback(() => run(async () => {
    setRows(await get<BalanceRow[]>(`/fx/balances?asOf=${asOf}`))
    setRuns(await get<Run[]>('/fx/revaluations'))
  }), [run, asOf])
  useEffect(() => { load() }, [load])

  const currencies = [...new Set(rows.filter(r => r.revalued && r.foreignBalance !== '0').map(r => r.currency))].sort()
  const lastActive = runs.find(r => r.status === 'Active')
  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })

  const revalue = () => act(async () => {
    const payload = currencies.map(c => {
      const r = rates[c] ?? { debit: '', credit: '' }
      const debit = parseRate(r.debit), credit = parseRate(r.credit || r.debit)
      if (!debit || !credit) throw new Error(`Tỷ giá ${c} không hợp lệ (tối đa 6 chữ số thập phân).`)
      return { currency: c, debitBalanceRate: debit, creditBalanceRate: credit }
    })
    await post('/fx/revaluations', { date: asOf, rates: payload, rateSource: source })
  }, `Đã đánh giá lại số dư ngoại tệ ngày ${asOf} và ghi sổ.`)

  return (
    <>
      <div className="toolbar">
        <h2>Ngoại tệ — số dư và đánh giá lại cuối kỳ</h2>
        <label>Tại ngày <input type="date" value={asOf} onChange={e => setAsOf(e.target.value)} /></label>
        <button onClick={() => load()}>Tải lại</button>
      </div>
      <p className="muted">Số dư theo từng tài khoản – đối tượng – loại tiền. Đánh giá lại áp dụng cho tiền, tiền gửi, vay… (không theo dõi công nợ chi tiết); chênh lệch ghi vào TK ánh xạ vai trò FX_REVALUATION. Tỷ giá áp cho số dư Nợ / số dư Có theo chính sách của doanh nghiệp [CẦN XÁC MINH căn cứ TT99].</p>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}

      <table className="grid">
        <thead><tr><th>TK</th><th>Đối tượng</th><th>Loại tiền</th><th className="num">Số dư nguyên tệ</th><th className="num">Số dư quy đổi</th><th className="num">Tỷ giá ghi sổ</th><th>Đánh giá lại</th></tr></thead>
        <tbody>{rows.map(r => (
          <tr key={`${r.accountId}|${r.partnerId}|${r.currency}`}>
            <td>{r.accountCode}</td><td>{r.partnerCode}</td><td>{r.currency}</td>
            <td className="num">{formatMoney(r.foreignBalance)}</td><td className="num">{formatMoney(r.bookBalance)}</td>
            <td className="num">{r.bookRate == null ? '' : formatMoney(r.bookRate)}</td>
            <td>{r.revalued ? 'Có' : 'Không'}{r.note && <span className="muted"> — {r.note}</span>}</td>
          </tr>))}
        </tbody>
      </table>

      <FxPaymentPanel date={asOf} onDone={msg => { setInfo(msg); load() }} />

      <section className="panel">
        <h3>Đánh giá lại ngày {asOf}</h3>
        {currencies.length === 0 && <p className="muted">Không còn số dư nguyên tệ cần tỷ giá; chạy đánh giá lại vẫn xử lý phần quy đổi còn sót trên số dư nguyên tệ bằng 0.</p>}
        <div className="form-grid">
          {currencies.map(c => <div key={c}>
            <label>{c} – tỷ giá cho số dư Nợ (tài sản) <input inputMode="decimal" value={rates[c]?.debit ?? ''} onChange={e => setRates(x => ({ ...x, [c]: { debit: e.target.value, credit: x[c]?.credit ?? '' } }))} /></label>
            <label>{c} – tỷ giá cho số dư Có (nợ phải trả) <input inputMode="decimal" placeholder="để trống = như trên" value={rates[c]?.credit ?? ''} onChange={e => setRates(x => ({ ...x, [c]: { debit: x[c]?.debit ?? '', credit: e.target.value } }))} /></label>
          </div>)}
          <label>Nguồn tỷ giá <input value={source} onChange={e => setSource(e.target.value)} placeholder="Ngân hàng …, ngày giờ niêm yết" /></label>
        </div>
        <button className="primary" disabled={busy || !source.trim()} onClick={revalue}>Đánh giá lại và ghi sổ</button>
      </section>

      <section className="panel">
        <h3>Các lần đánh giá lại</h3>
        <table className="grid"><thead><tr><th>Ngày</th><th>Nguồn tỷ giá</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
          <tbody>{runs.map(r => <tr key={r.id}><td>{r.revaluationDate}</td><td>{r.rateSource}</td>
            <td>{r.status === 'Active' ? (r.voucherId ? 'Hiệu lực' : 'Hiệu lực (không phát sinh)') : 'Đã hủy'}</td>
            <td>{r.id === lastActive?.id && <button disabled={busy} onClick={async () => {
              const reason = await askText('Lý do hủy lần đánh giá lại:')
              if (reason) act(() => post(`/fx/revaluations/${r.id}/reverse`, { reason }), 'Đã hủy lần đánh giá lại.')
            }}>Hủy</button>}</td></tr>)}</tbody></table>
      </section>
    </>
  )
}

interface Preview { currency: string; foreignTotal: string; cashAmount: string; bookAmount: string; difference: string }

/** Thu/chi ngoại tệ theo hóa đơn: cash at the payment rate, invoices at their book rate, the gap as realised FX gain/loss. */
function FxPaymentPanel({ date, onDone }: { date: string; onDone: (msg: string) => void }) {
  const { accounts, partners } = useCatalog()
  const [accountId, setAccountId] = useState('')
  const [partnerId, setPartnerId] = useState('')
  const [cashAccountId, setCashAccountId] = useState('')
  const [rate, setRate] = useState('')
  const [description, setDescription] = useState('')
  const [items, setItems] = useState<OpenItem[]>([])
  const [picked, setPicked] = useState<Record<string, string>>({})
  const [preview, setPreview] = useState<Preview | null>(null)
  const { busy, error, run } = useAction()

  useEffect(() => {
    setPicked({}); setPreview(null)
    if (!accountId || !partnerId) { setItems([]); return }
    get<OpenItem[]>(`/settlement/open-items?accountId=${accountId}&partnerId=${partnerId}&asOf=${date}`)
      .then(list => setItems(list.filter(i => i.foreignCurrency && i.openForeign && i.openForeign !== '0')))
      .catch(() => setItems([]))
  }, [accountId, partnerId, date])

  const payload = () => {
    const r = parseRate(rate)
    if (!r) throw new Error('Tỷ giá thanh toán không hợp lệ (tối đa 6 chữ số thập phân).')
    const list = Object.entries(picked).map(([journalLineId, v]) => {
      const amount = parseMoney(v)
      if (!amount) throw new Error('Số nguyên tệ thanh toán không hợp lệ.')
      return { journalLineId, foreignAmount: amount }
    })
    if (list.length === 0) throw new Error('Chọn ít nhất một hóa đơn.')
    return { date, cashAccountId, paymentRate: r, description, items: list }
  }

  return (
    <section className="panel">
      <h3>Thu / chi ngoại tệ theo hóa đơn</h3>
      <p className="muted">Công nợ được ghi giảm theo tỷ giá ghi sổ của từng hóa đơn; tiền theo tỷ giá thanh toán; chênh lệch ghi vào TK vai trò FX_REALIZED_GAIN / FX_REALIZED_LOSS. Chứng từ được lập ở trạng thái Nháp để gửi duyệt.</p>
      <ErrorBox error={error} />
      <div className="form-grid">
        <label>TK công nợ <AccountSelect accounts={accounts} value={accountId} onChange={setAccountId} filter={a => a.openItemNature !== 'None'} ariaLabel="TK công nợ" /></label>
        <label>Đối tượng <PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>
        <label>TK tiền <AccountSelect accounts={accounts} value={cashAccountId} onChange={setCashAccountId} filter={a => a.cashKind !== 'None'} ariaLabel="TK tiền" /></label>
        <label title="Thu tiền: ghi theo tỷ giá này. Chi tiền: TK tiền ghi theo tỷ giá ghi sổ bình quân gia quyền di động của TK, tỷ giá này không dùng.">
          Tỷ giá thanh toán (khi thu) <input inputMode="decimal" value={rate} onChange={e => setRate(e.target.value)} /></label>
        <label>Diễn giải <input value={description} onChange={e => setDescription(e.target.value)} /></label>
      </div>
      {accountId && partnerId && items.length === 0 && <p className="muted">Đối tượng không có hóa đơn ngoại tệ còn mở.</p>}
      {items.length > 0 && <table className="grid"><thead><tr><th></th><th>Chứng từ</th><th>Hạn</th><th>Loại tiền</th><th className="num">Nguyên tệ còn lại</th><th className="num">Giá trị ghi sổ còn lại</th><th className="num">Thanh toán (nguyên tệ)</th></tr></thead>
        <tbody>{items.map(i => <tr key={i.journalLineId}>
          <td><input type="checkbox" aria-label={`Chọn ${i.voucherNumber}`} checked={i.journalLineId in picked} onChange={e => { setPreview(null); setPicked(p => {
            const n = { ...p }; if (e.target.checked) n[i.journalLineId] = i.openForeign ?? ''; else delete n[i.journalLineId]; return n }) }} /></td>
          <td>{i.voucherNumber}</td><td>{i.dueDate}</td><td>{i.foreignCurrency}</td>
          <td className="num">{formatMoney(i.openForeign)}</td><td className="num">{formatMoney(i.open)}</td>
          <td className="num">{i.journalLineId in picked && <input inputMode="decimal" aria-label="Nguyên tệ thanh toán" value={picked[i.journalLineId]}
            onChange={e => { setPreview(null); setPicked(p => ({ ...p, [i.journalLineId]: e.target.value })) }} />}</td>
        </tr>)}</tbody></table>}
      {preview && <div className="msg ok">{formatMoney(preview.foreignTotal)} {preview.currency}: tiền {formatMoney(preview.cashAmount)}, giá trị ghi sổ {formatMoney(preview.bookAmount)} →
        {preview.difference.startsWith('-') ? ' lỗ ' : ' lãi '}tỷ giá đã thực hiện {formatMoney(preview.difference.replace('-', ''))}</div>}
      <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
        <button disabled={busy || !cashAccountId || !description.trim()} onClick={() => run(async () => setPreview(await post<Preview>('/fx/payments/preview', payload())))}>Xem trước</button>
        <button className="primary" disabled={busy || !preview} onClick={() => run(async () => {
          const v = await post<{ number: string }>('/fx/payments', payload())
          setPicked({}); setPreview(null)
          onDone(`Đã lập chứng từ ${v.number} (Nháp) — gửi duyệt và ghi sổ ở màn hình Chứng từ.`)
        })}>Lập chứng từ nháp</button>
      </div>
    </section>
  )
}