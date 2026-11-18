import { useCallback, useEffect, useState } from 'react'
import { MoneyInput } from './MoneyInput'
import { askText } from './dialogs'
import { get, post, put, type Period } from './api'
import { AccountSelect, ErrorBox, ExportButton, useAction, useCatalog, usePrintBook } from './common'
import { AssetsInUsePrint, FixedAssetBookPrint, FixedAssetCardPrint } from './books'
import { useCompanyInfo } from './forms'
import { addMoney, formatMoney, parseMoney, today } from './money'
import { DateInput } from './DateInput'

interface RegisterRow {
  id: string; code: string; name: string; class: 'FixedAsset' | 'Tool'; status: 'Draft' | 'Active' | 'Disposed' | 'Cancelled'
  assetAccountCode: string; inServiceDate: string; scheduleStartDate: string; usefulLifeMonths: number; originalCost: string; accumulated: string; netBookValue: string; disposalDate: string | null
  residualValue?: string; sourceVoucher?: string | null; sourceDate?: string | null; disposalVoucher?: string | null; disposalReason?: string | null; usedBy?: string | null
}
interface Candidate { journalLineId: string; voucherNumber: string; postingDate: string; accountCode: string; description: string | null; lineAmount: string; assigned: string }
interface Run { id: string; periodStart: string; periodEnd: string; status: 'Active' | 'Reversed'; voucherId: string | null }
interface ReconRow { accountId: string; accountCode: string; kind: string; registerBalance: string; ledgerBalance: string; difference: string }
interface Detail {
  asset: { id: string; code: string; name: string; proration: string; scheduleStartDate: string; openingAccumulated: string; remainingLifeMonths: number | null }
  sources: { journalLineId: string; voucherNumber: string; postingDate: string; accountCode: string; lineAmount: string; amount: string }[]
  schedule: { periodEnd: string; planned: string; cumulative: string; booked: string | null }[]
}

const CLASS_VI = { FixedAsset: 'TSCĐ', Tool: 'CCDC' } as const
const STATUS_VI = { Draft: 'Nháp', Active: 'Đang dùng', Disposed: 'Đã ghi giảm', Cancelled: 'Đã hủy' } as const

/** Amount of a candidate line not yet assigned (exact string arithmetic). */
const remaining = (c: Candidate) => addMoney([c.lineAmount, '-' + c.assigned])

export function AssetsView() {
  const { accounts } = useCatalog()
  const [asOf, setAsOf] = useState(today())
  const [rows, setRows] = useState<RegisterRow[]>([])
  const [runs, setRuns] = useState<Run[]>([])
  const [periods, setPeriods] = useState<Period[]>([])
  const [recon, setRecon] = useState<ReconRow[] | null>(null)
  const [detail, setDetail] = useState<Detail | null>(null)
  const [creating, setCreating] = useState(false)
  const [disposing, setDisposing] = useState<RegisterRow | null>(null)
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const company = useCompanyInfo()
  const [book, setBook] = usePrintBook<'S21' | 'S22' | 'S23'>()
  const fixedAssets = rows.filter(r => r.class === 'FixedAsset' && r.status !== 'Draft')
  const detailRow = detail ? rows.find(r => r.id === detail.asset.id) : undefined

  const load = useCallback(() => run(async () => {
    setRows(await get<RegisterRow[]>(`/assets?asOf=${asOf}`))
    setRuns(await get<Run[]>('/assets/depreciation-runs'))
    setPeriods(await get<Period[]>('/periods'))
  }), [run, asOf])
  useEffect(() => { load() }, [load])

  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); setDetail(null); await load() })
  const lastActive = runs.find(r => r.status === 'Active')
  // Next period after the last run; before the first run, the period of the earliest depreciation start (runs go in period order from there).
  const earliest = rows.filter(r => r.status === 'Active').map(r => r.scheduleStartDate).sort()[0]
  const nextPeriod = lastActive
    ? periods.find(p => p.status === 'Open' && p.startDate > lastActive.periodEnd)
    : periods.find(p => p.status === 'Open' && earliest !== undefined && p.startDate <= earliest && p.endDate >= earliest)
  const [periodId, setPeriodId] = useState('')

  return (
    <>
      <div className="toolbar">
        <h2>Tài sản cố định & công cụ dụng cụ</h2>
        <label>Tại ngày <DateInput value={asOf} onChange={e => setAsOf(e.target.value)} /></label>
        <button onClick={() => load()}>Tải lại</button>
        <button className="primary" onClick={() => { setCreating(true); setDetail(null) }}>Thêm thẻ tài sản</button>
        <ExportButton fileName={'So_TSCD_' + asOf} sheet="Sổ tài sản" columns={[{ header: 'Mã' }, { header: 'Tên', width: 30 }, { header: 'Loại' }, { header: 'Trạng thái' }, { header: 'TK' }, { header: 'Ngày sử dụng' },
          { header: 'Số tháng', numeric: true }, { header: 'Nguyên giá', numeric: true }, { header: 'Hao mòn / đã phân bổ', numeric: true }, { header: 'Còn lại', numeric: true }, { header: 'Ngày ghi giảm' }]}
          rows={rows.map(r => [r.code, r.name, CLASS_VI[r.class], STATUS_VI[r.status], r.assetAccountCode, r.inServiceDate, String(r.usefulLifeMonths), r.originalCost, r.accumulated, r.netBookValue, r.disposalDate])} />
        <button disabled={!fixedAssets.length} title="Mẫu số S21-DN, Phụ lục III TT 99/2025 (TSCĐ đã ghi tăng; mỗi TK một bảng)" onClick={() => setBook('S21')}>In sổ TSCĐ (S21-DN)</button>
        <button disabled={!rows.some(r => r.status !== 'Draft' && r.status !== 'Cancelled')} title="Mẫu số S22-DN, Phụ lục III TT 99/2025 (TSCĐ và CCDC theo nơi sử dụng)" onClick={() => setBook('S22')}>In sổ theo nơi sử dụng (S22-DN)</button>
      </div>
      {book === 'S22' && <AssetsInUsePrint company={company.name} address={company.address} assets={rows.filter(r => r.status !== 'Draft' && r.status !== 'Cancelled')} year={asOf.slice(0, 4)} />}
      {book === 'S21' && <FixedAssetBookPrint company={company.name} address={company.address} assets={fixedAssets} year={asOf.slice(0, 4)} />}
      {book === 'S23' && detail && detailRow && <FixedAssetCardPrint company={company.name} address={company.address} date={asOf} card={{
        asset: detailRow, openingAccumulated: detail.asset.openingAccumulated,
        sources: detail.sources, booked: detail.schedule.filter(x => x.booked != null).map(x => ({ periodEnd: x.periodEnd, amount: x.booked! })) }} />}
      <p className="muted">Nguyên giá lấy từ bút toán đã ghi sổ (hóa đơn mua, chứng từ, số dư đầu) nên sổ tài sản luôn khớp sổ cái. Phương pháp đường thẳng; cách tính tháng đầu/cuối là cấu hình [CẦN XÁC MINH căn cứ pháp lý].</p>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}

      {creating && <NewAsset accounts={accounts} onDone={msg => { setCreating(false); if (msg) { setInfo(msg); load() } }} />}

      <table className="grid">
        <thead><tr><th>Mã</th><th>Tên</th><th>Loại</th><th>Trạng thái</th><th>TK</th><th>Ngày sử dụng</th><th>Tháng</th>
          <th className="num">Nguyên giá</th><th className="num">Hao mòn / đã phân bổ</th><th className="num">Còn lại</th><th>Thao tác</th></tr></thead>
        <tbody>{rows.map(r => (
          <tr key={r.id}>
            <td>{r.code}</td><td>{r.name}{r.usedBy ? <div className="muted">{r.usedBy}</div> : null}</td><td>{CLASS_VI[r.class]}</td>
            <td><span className={`badge ${r.status}`}>{STATUS_VI[r.status]}{r.disposalDate ? ` ${r.disposalDate}` : ''}</span></td>
            <td>{r.assetAccountCode}</td><td>{r.inServiceDate}</td><td>{r.usefulLifeMonths}</td>
            <td className="num">{formatMoney(r.originalCost)}</td><td className="num">{formatMoney(r.accumulated)}</td><td className="num">{formatMoney(r.netBookValue)}</td>
            <td style={{ display: 'flex', gap: 6 }}>
              <button onClick={() => run(async () => setDetail(await get<Detail>(`/assets/${r.id}`)))}>Chi tiết</button>
              {r.status !== 'Cancelled' && <button disabled={busy} title="Phòng, ban hoặc người sử dụng (sổ S22-DN)" onClick={async () => {
                const v = await askText('Nơi sử dụng (phòng, ban hoặc người sử dụng; để trống = bỏ):', r.usedBy ?? '')
                if (v !== null) act(() => put(`/assets/${r.id}/used-by`, { usedBy: v.trim() || null }), `Đã cập nhật nơi sử dụng ${r.code}.`)
              }}>Nơi sử dụng</button>}
              {r.status === 'Draft' && <>
                <button disabled={busy} onClick={() => act(() => post(`/assets/${r.id}/activate`), `Đã ghi tăng ${r.code}.`)}>Ghi tăng</button>
                <button disabled={busy} onClick={async () => { const reason = await askText('Lý do hủy thẻ:'); if (reason) act(() => post(`/assets/${r.id}/cancel`, { reason }), `Đã hủy thẻ ${r.code}.`) }}>Hủy</button>
              </>}
              {r.status === 'Active' && <button disabled={busy} onClick={() => setDisposing(r)}>Ghi giảm</button>}
              {r.status === 'Disposed' && <button disabled={busy} onClick={async () => {
                const reason = await askText('Lý do hủy ghi giảm:')
                if (reason) act(() => post(`/assets/${r.id}/dispose/reverse`, { reason }), `Đã hủy ghi giảm ${r.code}.`)
              }}>Hủy ghi giảm</button>}
            </td>
          </tr>))}
        </tbody>
      </table>

      {disposing && <Dispose row={disposing} accounts={accounts} busy={busy}
        onSubmit={(date, residualAccountId, description) => act(async () => {
          await post(`/assets/${disposing.id}/dispose`, { date, residualAccountId, description }); setDisposing(null)
        }, `Đã ghi giảm ${disposing.code}.`)}
        onCancel={() => setDisposing(null)} />}

      {detail && (
        <section className="panel">
          <div className="toolbar"><h3>{detail.asset.code} – {detail.asset.name}</h3>
            {detailRow?.class === 'FixedAsset' && <button title="Mẫu số S23-DN, Phụ lục III TT 99/2025" onClick={() => setBook('S23')}>In thẻ TSCĐ (S23-DN)</button>}
            <button onClick={() => setDetail(null)}>Đóng</button></div>
          <p className="muted">Bắt đầu khấu hao {detail.asset.scheduleStartDate}; tính tháng đầu/cuối: {detail.asset.proration === 'Daily' ? 'theo ngày' : 'tròn tháng'}
            {detail.asset.remainingLifeMonths ? `; chuyển từ hệ thống cũ: hao mòn đầu ${formatMoney(detail.asset.openingAccumulated)}, còn ${detail.asset.remainingLifeMonths} tháng` : ''}</p>
          <h4>Nguồn nguyên giá</h4>
          <table className="grid"><thead><tr><th>Chứng từ</th><th>Ngày</th><th>TK</th><th className="num">Dòng bút toán</th><th className="num">Gán cho tài sản</th></tr></thead>
            <tbody>{detail.sources.map(x => <tr key={x.journalLineId}><td>{x.voucherNumber}</td><td>{x.postingDate}</td><td>{x.accountCode}</td>
              <td className="num">{formatMoney(x.lineAmount)}</td><td className="num">{formatMoney(x.amount)}</td></tr>)}</tbody></table>
          <h4>Kế hoạch khấu hao / phân bổ</h4>
          <table className="grid"><thead><tr><th>Tháng</th><th className="num">Kế hoạch</th><th className="num">Lũy kế</th><th className="num">Đã ghi sổ</th></tr></thead>
            <tbody>{detail.schedule.map(x => <tr key={x.periodEnd}><td>{x.periodEnd.slice(0, 7)}</td><td className="num">{formatMoney(x.planned)}</td>
              <td className="num">{formatMoney(x.cumulative)}</td><td className="num">{x.booked == null ? '' : formatMoney(x.booked)}</td></tr>)}</tbody></table>
        </section>
      )}

      <section className="panel">
        <h3>Chạy khấu hao / phân bổ theo kỳ</h3>
        <p className="muted">Chạy lần lượt từng kỳ; hủy từ kỳ gần nhất trở về trước. Bút toán sinh ra chỉ đảo được bằng chức năng hủy ở đây.</p>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          <select value={periodId || nextPeriod?.id || ''} onChange={e => setPeriodId(e.target.value)} aria-label="Kỳ khấu hao">
            <option value="">— chọn kỳ —</option>
            {periods.filter(p => p.status === 'Open').map(p => <option key={p.id} value={p.id}>{p.startDate.slice(0, 7)}</option>)}
          </select>
          <button className="primary" disabled={busy || !(periodId || nextPeriod)} onClick={async () => {
            const id = periodId || nextPeriod!.id
            setPeriodId('')
            act(() => post(`/assets/depreciation-runs?periodId=${id}`), 'Đã chạy khấu hao và ghi sổ bút toán.')
          }}>Chạy khấu hao</button>
        </div>
        <table className="grid"><thead><tr><th>Kỳ</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
          <tbody>{runs.map(r => <tr key={r.id}><td>{r.periodStart.slice(0, 7)}</td><td>{r.status === 'Active' ? 'Hiệu lực' : 'Đã hủy'}{r.status === 'Active' && !r.voucherId ? ' (không phát sinh)' : ''}</td>
            <td>{r.id === lastActive?.id && <button disabled={busy} onClick={async () => {
              const reason = await askText('Lý do hủy lần chạy khấu hao:')
              if (reason) act(() => post(`/assets/depreciation-runs/${r.id}/reverse`, { reason }), 'Đã hủy lần chạy khấu hao.')
            }}>Hủy</button>}</td></tr>)}</tbody></table>
      </section>

      <section className="panel">
        <div className="toolbar"><h3>Đối chiếu sổ tài sản ↔ sổ cái tại {asOf}</h3>
          <button onClick={() => run(async () => setRecon(await get<ReconRow[]>(`/assets/reconciliation?asOf=${asOf}`)))}>Đối chiếu</button></div>
        {recon && <table className="grid"><thead><tr><th>TK</th><th>Nội dung</th><th className="num">Sổ tài sản</th><th className="num">Sổ cái</th><th className="num">Chênh lệch</th></tr></thead>
          <tbody>{recon.map(r => <tr key={r.accountId} className={r.difference !== '0' ? 'warn' : ''}><td>{r.accountCode}</td><td>{r.kind}</td>
            <td className="num">{formatMoney(r.registerBalance)}</td><td className="num">{formatMoney(r.ledgerBalance)}</td><td className="num">{formatMoney(r.difference)}</td></tr>)}</tbody></table>}
        {recon && <p className="muted">Số dương = dư Nợ, số âm = dư Có. Chênh lệch là bút toán trên TK tài sản/hao mòn chưa gắn với thẻ tài sản nào.</p>}
      </section>
    </>
  )
}

function Dispose({ row, accounts, busy, onSubmit, onCancel }: {
  row: RegisterRow; accounts: ReturnType<typeof useCatalog>['accounts']; busy: boolean
  onSubmit: (date: string, accountId: string, description: string) => void; onCancel: () => void
}) {
  const [date, setDate] = useState(today())
  const [accountId, setAccountId] = useState('')
  const [description, setDescription] = useState('')
  return (
    <section className="panel">
      <h3>Ghi giảm {row.code} – {row.name}</h3>
      <p className="muted">Hệ thống ghi khấu hao của tháng ghi giảm theo cách tính tháng đã chọn, xóa nguyên giá và hao mòn; giá trị còn lại ghi vào TK chọn dưới đây. Tiền thu thanh lý lập hóa đơn bán riêng.</p>
      <label>Ngày ghi giảm <DateInput value={date} onChange={e => setDate(e.target.value)} /></label>
      <label>TK nhận giá trị còn lại <AccountSelect accounts={accounts} value={accountId} onChange={setAccountId} ariaLabel="TK giá trị còn lại" /></label>
      <label>Căn cứ / diễn giải <input value={description} onChange={e => setDescription(e.target.value)} placeholder="Biên bản thanh lý số…" /></label>
      <div style={{ display: 'flex', gap: 8 }}>
        <button className="primary" disabled={busy || !accountId || !description.trim()} onClick={() => onSubmit(date, accountId, description)}>Ghi giảm</button>
        <button onClick={onCancel}>Hủy</button>
      </div>
    </section>
  )
}

function NewAsset({ accounts, onDone }: { accounts: ReturnType<typeof useCatalog>['accounts']; onDone: (msg?: string) => void }) {
  const [f, setF] = useState({
    code: '', name: '', class: 'FixedAsset' as 'FixedAsset' | 'Tool', assetAccountId: '', accumulatedAccountId: '', expenseAccountId: '',
    proration: 'FullMonth', residualValue: '0', usefulLifeMonths: '60', inServiceDate: today(), note: '',
    takenOver: false, openingAccumulated: '0', remainingLifeMonths: '', scheduleStartDate: '',
  })
  const [candidates, setCandidates] = useState<Candidate[]>([])
  const [picked, setPicked] = useState<Record<string, string>>({})
  const { busy, error, run } = useAction()
  const set = (k: keyof typeof f, v: string | boolean) => setF(x => ({ ...x, [k]: v }))

  useEffect(() => {
    setPicked({})
    if (!f.assetAccountId) { setCandidates([]); return }
    get<Candidate[]>(`/assets/cost-candidates?accountId=${f.assetAccountId}`).then(setCandidates).catch(() => setCandidates([]))
  }, [f.assetAccountId])

  const submit = () => run(async () => {
    const money = (s: string, label: string) => { const m = parseMoney(s); if (m == null) throw new Error(`${label} không hợp lệ.`); return m }
    const costSources = Object.entries(picked).map(([journalLineId, amount]) => ({ journalLineId, amount: money(amount, 'Số tiền gán') }))
    if (costSources.length === 0) throw new Error('Chọn ít nhất một bút toán hình thành nguyên giá.')
    const a = await post<{ code: string }>('/assets', {
      code: f.code, name: f.name, class: f.class, assetAccountId: f.assetAccountId,
      accumulatedAccountId: f.class === 'FixedAsset' ? f.accumulatedAccountId || null : null, expenseAccountId: f.expenseAccountId,
      proration: f.proration, residualValue: money(f.residualValue, 'Giá trị thu hồi'), usefulLifeMonths: Number(f.usefulLifeMonths),
      inServiceDate: f.inServiceDate, costSources, note: f.note || null,
      openingAccumulated: f.takenOver ? money(f.openingAccumulated, 'Hao mòn lũy kế đầu') : '0',
      remainingLifeMonths: f.takenOver ? Number(f.remainingLifeMonths) : null,
      scheduleStartDate: f.takenOver ? f.scheduleStartDate : null,
    })
    onDone(`Đã lập thẻ ${a.code} (Nháp). Người khác bấm "Ghi tăng" để bắt đầu khấu hao.`)
  })

  return (
    <section className="panel">
      <h3>Thẻ tài sản mới</h3>
      <ErrorBox error={error} />
      <div className="form-grid">
        <label>Mã <input value={f.code} onChange={e => set('code', e.target.value)} /></label>
        <label>Tên <input value={f.name} onChange={e => set('name', e.target.value)} /></label>
        <label>Loại <select value={f.class} onChange={e => set('class', e.target.value)}><option value="FixedAsset">TSCĐ</option><option value="Tool">CCDC / chi phí trả trước</option></select></label>
        <label>TK tài sản <AccountSelect accounts={accounts} value={f.assetAccountId} onChange={v => set('assetAccountId', v)} filter={a => a.cashKind === 'None' && a.openItemNature === 'None' && !a.requiresPartner} ariaLabel="TK tài sản" /></label>
        {f.class === 'FixedAsset' && <label>TK hao mòn <AccountSelect accounts={accounts} value={f.accumulatedAccountId} onChange={v => set('accumulatedAccountId', v)} ariaLabel="TK hao mòn" /></label>}
        <label>TK chi phí <AccountSelect accounts={accounts} value={f.expenseAccountId} onChange={v => set('expenseAccountId', v)} ariaLabel="TK chi phí" /></label>
        <label>Ngày đưa vào sử dụng <DateInput value={f.inServiceDate} onChange={e => set('inServiceDate', e.target.value)} /></label>
        <label>Thời gian sử dụng (tháng) <input inputMode="numeric" value={f.usefulLifeMonths} onChange={e => set('usefulLifeMonths', e.target.value)} /></label>
        <label>Giá trị thu hồi ước tính <MoneyInput value={f.residualValue} onChange={e => set('residualValue', e.target.value)} /></label>
        <label>Tính tháng đầu/cuối <select value={f.proration} onChange={e => set('proration', e.target.value)}>
          <option value="FullMonth">Tròn tháng (tính tháng tăng, không tính tháng giảm)</option><option value="Daily">Theo số ngày sử dụng</option></select></label>
        <label><input type="checkbox" checked={f.takenOver} onChange={e => set('takenOver', e.target.checked)} /> Chuyển từ hệ thống cũ (đã khấu hao một phần)</label>
        {f.takenOver && <>
          <label>Hao mòn lũy kế đến ngày chuyển <MoneyInput value={f.openingAccumulated} onChange={e => set('openingAccumulated', e.target.value)} /></label>
          <label>Số tháng còn lại <input inputMode="numeric" value={f.remainingLifeMonths} onChange={e => set('remainingLifeMonths', e.target.value)} /></label>
          <label>Bắt đầu khấu hao trên hệ thống mới <DateInput value={f.scheduleStartDate} onChange={e => set('scheduleStartDate', e.target.value)} /></label>
        </>}
      </div>
      <h4>Bút toán hình thành nguyên giá (phát sinh Nợ TK tài sản, đã ghi sổ)</h4>
      {f.assetAccountId && candidates.length === 0 && <p className="muted">Không có bút toán nào còn chưa gán trên tài khoản này.</p>}
      {candidates.length > 0 && <table className="grid"><thead><tr><th></th><th>Chứng từ</th><th>Ngày</th><th>Diễn giải</th><th className="num">Số tiền</th><th className="num">Đã gán</th><th className="num">Gán cho thẻ này</th></tr></thead>
        <tbody>{candidates.map(c => <tr key={c.journalLineId}>
          <td><input type="checkbox" aria-label={`Chọn ${c.voucherNumber}`} checked={c.journalLineId in picked} onChange={e => setPicked(p => {
            const n = { ...p }; if (e.target.checked) n[c.journalLineId] = remaining(c); else delete n[c.journalLineId]; return n })} /></td>
          <td>{c.voucherNumber}</td><td>{c.postingDate}</td><td>{c.description}</td><td className="num">{formatMoney(c.lineAmount)}</td><td className="num">{formatMoney(c.assigned)}</td>
          <td className="num">{c.journalLineId in picked && <MoneyInput value={picked[c.journalLineId]} aria-label="Số tiền gán"
            onChange={e => setPicked(p => ({ ...p, [c.journalLineId]: e.target.value }))} />}</td>
        </tr>)}</tbody></table>}
      <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
        <button className="primary" disabled={busy || !f.code || !f.name || !f.assetAccountId || !f.expenseAccountId} onClick={submit}>Lưu nháp</button>
        <button onClick={() => onDone()}>Hủy</button>
      </div>
    </section>
  )
}
