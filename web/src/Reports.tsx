import { useState } from 'react'
import { get, post, type OpenItem, type TrialBalanceRow } from './api'
import { AccountSelect, ErrorBox, ExportButton, Modal, PartnerSelect, TYPE_VI, useAction, useCatalog } from './common'
import { addMoney, formatMoney, parseMoney, parseRate, today } from './money'
import { ConfirmationDialog } from './Confirmation'

const firstOfMonth = () => today().slice(0, 8) + '01'

export function TrialBalanceView() {
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [rollup, setRollup] = useState(true)
  const [data, setData] = useState<{ isBalanced: boolean; rows: TrialBalanceRow[]; groups: TrialBalanceRow[]; totals: Record<string, string> } | null>(null)
  const { busy, error, run } = useAction()
  const load = () => run(async () => setData(await get(`/reports/trial-balance?from=${from}&to=${to}&rollup=${rollup}`)))
  const t = data?.totals
  // Parent (summary) accounts interleaved with their children by account code; they never enter the totals.
  const lines = data ? [...data.rows, ...(data.groups ?? [])].sort((a, b) => (a.accountCode < b.accountCode ? -1 : a.accountCode > b.accountCode ? 1 : (b.isGroup ? 1 : 0) - (a.isGroup ? 1 : 0))) : []
  const amounts = (r: TrialBalanceRow) => [r.openingDebit, r.openingCredit, r.periodDebit, r.periodCredit, r.closingDebit, r.closingCredit]
  return (
    <>
      <div className="toolbar">
        <h2>Bảng cân đối số phát sinh</h2>
        <label>Từ ngày<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <label><input type="checkbox" checked={rollup} onChange={e => setRollup(e.target.checked)} /> Cộng dồn TK tổng hợp</label>
        <button className="primary" onClick={load} disabled={busy}>Xem</button>
        <ExportButton fileName={`BCDPS_${from}_${to}`} sheet="BCĐPS" disabled={!data}
          columns={[{ header: 'TK' }, { header: 'Tên tài khoản', width: 40 }, ...['Dư đầu Nợ', 'Dư đầu Có', 'PS Nợ', 'PS Có', 'Dư cuối Nợ', 'Dư cuối Có'].map(h => ({ header: h, numeric: true }))]}
          rows={[...lines.map(r => [r.accountCode, '  '.repeat(r.level ?? 0) + r.accountName, ...amounts(r)]),
            ...(t ? [['Cộng', '', t.totalOpeningDebit, t.totalOpeningCredit, t.totalPeriodDebit, t.totalPeriodCredit, t.totalClosingDebit, t.totalClosingCredit]] : [])]} />
      </div>
      <ErrorBox error={error} />
      {data && (
        <>
          <div className={`msg ${data.isBalanced ? 'ok' : 'error'}`}>{data.isBalanced ? 'Cân đối: tổng Nợ = tổng Có ở mọi cột.' : 'KHÔNG cân đối — cần kiểm tra ngay.'}</div>
          <div className="scroll">
            <table className="grid">
              <thead><tr><th>TK</th><th>Tên tài khoản</th><th className="num">Dư đầu Nợ</th><th className="num">Dư đầu Có</th><th className="num">PS Nợ</th><th className="num">PS Có</th><th className="num">Dư cuối Nợ</th><th className="num">Dư cuối Có</th></tr></thead>
              <tbody>{lines.map(r => (
                <tr key={r.accountCode + (r.isGroup ? 'g' : '')} style={r.isGroup ? { fontWeight: 600 } : undefined}><td>{r.accountCode}</td>
                  <td style={{ paddingLeft: 8 + 14 * (r.level ?? 0) }}>{r.accountName}</td>
                  {amounts(r).map((v, i) => <td key={i} className="num">{v === '0' ? '' : formatMoney(v)}</td>)}</tr>))}
              </tbody>
              {t && <tfoot><tr><td colSpan={2}>Cộng</td>
                {['totalOpeningDebit', 'totalOpeningCredit', 'totalPeriodDebit', 'totalPeriodCredit', 'totalClosingDebit', 'totalClosingCredit'].map(k => <td key={k} className="num">{formatMoney(t[k])}</td>)}</tr></tfoot>}
            </table>
          </div>
        </>
      )}
    </>
  )
}

interface LedgerRow { postingDate: string; voucherType: string; voucherNumber: string; description: string | null; counterAccounts: string; debit: string; credit: string; runningBalance: string }
interface Ledger { accountCode: string; accountName: string; openingBalance: string; rows: LedgerRow[]; totalDebit: string; totalCredit: string; closingBalance: string }

const signed = (v: string) => (v.startsWith('-') ? `${formatMoney(v.slice(1))} Có` : v === '0' ? '0' : `${formatMoney(v)} Nợ`)

export function AccountLedgerView() {
  const { accounts, partners } = useCatalog()
  const [accountId, setAccountId] = useState('')
  const [partnerId, setPartnerId] = useState('')
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [data, setData] = useState<Ledger | null>(null)
  const { busy, error, run } = useAction()
  const load = () => run(async () => {
    const q = new URLSearchParams({ accountId, from, to, ...(partnerId ? { partnerId } : {}) })
    setData(await get(`/reports/account-ledger?${q}`))
  })
  return (
    <>
      <div className="toolbar">
        <h2>Sổ chi tiết tài khoản / Sổ quỹ</h2>
        <label>Tài khoản<AccountSelect accounts={accounts} value={accountId} onChange={setAccountId} /></label>
        <label>Đối tượng<PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>
        <label>Từ ngày<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" onClick={load} disabled={busy || !accountId}>Xem</button>
        <ExportButton fileName={`So_chi_tiet_${data?.accountCode ?? ''}_${from}_${to}`} sheet={`Sổ TK ${data?.accountCode ?? ''}`} disabled={!data}
          columns={[{ header: 'Ngày' }, { header: 'Số CT' }, { header: 'Loại' }, { header: 'Diễn giải', width: 40 }, { header: 'TK đối ứng' },
            { header: 'Nợ', numeric: true }, { header: 'Có', numeric: true }, { header: 'Số dư (+Nợ / −Có)', numeric: true }]}
          rows={data ? [['', '', '', 'Số dư đầu kỳ', '', '', '', data.openingBalance],
            ...data.rows.map(r => [r.postingDate, r.voucherNumber, TYPE_VI[r.voucherType] ?? r.voucherType, r.description, r.counterAccounts, r.debit, r.credit, r.runningBalance]),
            ['', '', '', 'Cộng phát sinh / số dư cuối kỳ', '', data.totalDebit, data.totalCredit, data.closingBalance]] : []} />
      </div>
      <ErrorBox error={error} />
      {data && (
        <div className="scroll">
          <table className="grid">
            <thead><tr><th>Ngày</th><th>Số CT</th><th>Loại</th><th>Diễn giải</th><th>TK đối ứng</th><th className="num">Nợ</th><th className="num">Có</th><th className="num">Số dư</th></tr></thead>
            <tbody>
              <tr><td colSpan={7}><strong>Số dư đầu kỳ</strong></td><td className="num">{signed(data.openingBalance)}</td></tr>
              {data.rows.map((r, i) => (
                <tr key={i}><td>{r.postingDate}</td><td>{r.voucherNumber}</td><td>{TYPE_VI[r.voucherType] ?? r.voucherType}</td><td>{r.description}</td><td>{r.counterAccounts}</td>
                  <td className="num">{r.debit === '0' ? '' : formatMoney(r.debit)}</td><td className="num">{r.credit === '0' ? '' : formatMoney(r.credit)}</td><td className="num">{signed(r.runningBalance)}</td></tr>))}
            </tbody>
            <tfoot><tr><td colSpan={5}>Cộng phát sinh / Số dư cuối kỳ</td><td className="num">{formatMoney(data.totalDebit)}</td><td className="num">{formatMoney(data.totalCredit)}</td><td className="num">{signed(data.closingBalance)}</td></tr></tfoot>
          </table>
        </div>
      )}
    </>
  )
}

interface AgingRow { partnerId: string; partnerCode: string; partnerName: string; notDue: string; buckets: string[]; openObligations: string; unappliedCredits: string; net: string }

export function ReceivablesView() {
  const { accounts, partners } = useCatalog()
  const openAccounts = accounts.filter(a => a.openItemNature !== 'None')
  const [accountId, setAccountId] = useState('')
  const [asOf, setAsOf] = useState(today())
  const [aging, setAging] = useState<{ bucketLabels: string[]; rows: AgingRow[] } | null>(null)
  const [partnerId, setPartnerId] = useState('')
  const [items, setItems] = useState<OpenItem[]>([])
  const [recon, setRecon] = useState<{ isReconciled: boolean; rows: unknown[] } | null>(null)
  const [allocs, setAllocs] = useState<AllocationRow[]>([])
  const [pickD, setPickD] = useState('')
  const [pickC, setPickC] = useState('')
  const [allocating, setAllocating] = useState(false)
  const [voiding, setVoiding] = useState<AllocationRow | null>(null)
  const [confirming, setConfirming] = useState(false)
  const confirmAccount = accounts.find(a => a.id === accountId)
  const confirmPartner = partners.find(p => p.id === partnerId)
  const { busy, error, run } = useAction()
  const debitItem = items.find(o => o.journalLineId === pickD)
  const creditItem = items.find(o => o.journalLineId === pickC)

  const load = () => run(async () => {
    setAging(await get(`/reports/aging?accountId=${accountId}&asOf=${asOf}`))
    setRecon(await get(`/reports/subledger-reconciliation?asOf=${asOf}`))
    setItems(partnerId ? await get(`/settlement/open-items?accountId=${accountId}&partnerId=${partnerId}&asOf=${asOf}`) : [])
    setAllocs(partnerId ? await get(`/settlement/allocations?accountId=${accountId}&partnerId=${partnerId}`) : [])
    setPickD(''); setPickC('')
  })
  const auto = () => run(async () => { await post('/settlement/auto', { accountId, partnerId, date: asOf }); await load() })

  return (
    <>
      <div className="toolbar">
        <h2>Công nợ</h2>
        <label>Tài khoản công nợ<AccountSelect accounts={openAccounts} value={accountId} onChange={setAccountId} /></label>
        <label>Đối tượng (chi tiết)<PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>
        <label>Đến ngày<input type="date" value={asOf} onChange={e => setAsOf(e.target.value)} /></label>
        <button className="primary" onClick={load} disabled={busy || !accountId}>Xem</button>
        <button onClick={auto} disabled={busy || !accountId || !partnerId}>Tự động bù trừ (FIFO)</button>
        <button onClick={() => setConfirming(true)} disabled={!accountId || !partnerId} title="In biên bản đối chiếu công nợ với đối tượng đang chọn">Biên bản đối chiếu…</button>
        <ExportButton fileName={`Tuoi_no_${asOf}`} sheet="Tuổi nợ" disabled={!aging}
          columns={[{ header: 'Mã' }, { header: 'Đối tượng', width: 36 }, { header: 'Chưa đến hạn', numeric: true },
            ...(aging?.bucketLabels ?? []).map(b => ({ header: `Quá hạn ${b} ngày`, numeric: true })),
            { header: 'Tổng còn nợ', numeric: true }, { header: 'Trả trước chưa phân bổ', numeric: true }, { header: 'Thuần', numeric: true }]}
          rows={(aging?.rows ?? []).map(r => [r.partnerCode, r.partnerName, r.notDue, ...r.buckets, r.openObligations, r.unappliedCredits, r.net])} />
      </div>
      <ErrorBox error={error} />
      {recon && <div className={`msg ${recon.isReconciled ? 'ok' : 'error'}`}>{recon.isReconciled ? 'Sổ chi tiết công nợ khớp sổ cái.' : `Sổ chi tiết lệch sổ cái: ${recon.rows.length} dòng cần kiểm tra.`}</div>}
      {aging && (
        <div className="panel">
          <strong>Tuổi nợ đến {asOf}</strong>
          <table className="grid">
            <thead><tr><th>Đối tượng</th><th className="num">Chưa đến hạn</th>{aging.bucketLabels.map(b => <th key={b} className="num">Quá hạn {b} ngày</th>)}<th className="num">Tổng còn nợ</th><th className="num">Trả trước chưa phân bổ</th><th className="num">Thuần</th></tr></thead>
            <tbody>{aging.rows.map(r => (
              <tr key={r.partnerId} onClick={() => setPartnerId(r.partnerId)} style={{ cursor: 'pointer' }}>
                <td>{r.partnerCode} – {r.partnerName}</td><td className="num">{formatMoney(r.notDue)}</td>
                {r.buckets.map((b, i) => <td key={i} className="num">{formatMoney(b)}</td>)}
                <td className="num">{formatMoney(r.openObligations)}</td><td className="num">{formatMoney(r.unappliedCredits)}</td><td className="num">{formatMoney(r.net)}</td></tr>))}
            </tbody>
          </table>
        </div>
      )}
      {items.length > 0 && (
        <div className="panel">
          <strong>Chứng từ còn mở</strong> <span className="muted">— chọn một dòng Nợ và một dòng Có rồi bấm "Phân bổ" để khớp tay</span>
          <table className="grid">
            <thead><tr><th></th><th>Ngày</th><th>Hạn</th><th>Số CT</th><th>Loại</th><th>Chiều</th><th className="num">Số tiền</th><th className="num">Đã phân bổ</th><th className="num">Còn lại</th></tr></thead>
            <tbody>{items.map(o => {
              const picked = (o.side === 'Debit' ? pickD : pickC) === o.journalLineId
              return (
              <tr key={o.journalLineId} className={picked ? 'selected' : undefined}>
                <td><input type="checkbox" aria-label={`Chọn ${o.voucherNumber}`} checked={picked} disabled={o.open.startsWith('-') || o.open === '0'}
                  onChange={e => (o.side === 'Debit' ? setPickD : setPickC)(e.target.checked ? o.journalLineId : '')} /></td>
                <td>{o.postingDate}</td><td>{o.dueDate}</td><td>{o.voucherNumber}</td><td>{TYPE_VI[o.voucherType] ?? o.voucherType}</td>
                <td>{o.side === 'Debit' ? 'Nợ' : 'Có'}</td><td className="num">{formatMoney(o.amount)}</td><td className="num">{formatMoney(o.allocated)}</td><td className="num">{formatMoney(o.open)}{o.foreignCurrency ? ` (${o.openForeign} ${o.foreignCurrency})` : ''}</td></tr>)})}
            </tbody>
          </table>
          <div className="toolbar">
            <button className="primary" disabled={busy || !pickD || !pickC} onClick={() => setAllocating(true)}>Phân bổ…</button>
          </div>
        </div>
      )}
      {allocating && debitItem && creditItem && <AllocateDialog debit={debitItem} credit={creditItem} asOf={asOf}
        onClose={() => setAllocating(false)} onDone={() => { setAllocating(false); setPickD(''); setPickC(''); load() }} />}
      {allocs.length > 0 && (
        <div className="panel">
          <strong>Lịch sử phân bổ của đối tượng</strong>
          <table className="grid">
            <thead><tr><th>Ngày</th><th>CT bên Nợ</th><th>CT bên Có</th><th className="num">Số tiền</th><th>Cách lập</th><th>Trạng thái</th><th></th></tr></thead>
            <tbody>{allocs.map(a => (
              <tr key={a.id} className={a.isActive ? undefined : 'muted'}>
                <td>{a.allocationDate}</td><td>{a.debitVoucherNumber}</td><td>{a.creditVoucherNumber}</td>
                <td className="num">{formatMoney(a.amount)}{a.foreignAmount ? ` (${a.foreignAmount})` : ''}</td>
                <td>{ORIGIN_VI[a.origin] ?? a.origin}</td>
                <td>{a.isActive ? 'Hiệu lực' : `Đã hủy: ${a.voidReason ?? ''}`}</td>
                <td>{a.isActive && <button onClick={() => setVoiding(a)} disabled={busy}>Hủy phân bổ</button>}</td></tr>))}
            </tbody>
          </table>
        </div>
      )}
      {confirming && confirmAccount && confirmPartner && <ConfirmationDialog account={confirmAccount} partner={confirmPartner} to={asOf} onClose={() => setConfirming(false)} />}
      {voiding && <VoidAllocationDialog alloc={voiding} onClose={() => setVoiding(null)} onDone={() => { setVoiding(null); load() }} />}
    </>
  )
}

interface AllocationRow { id: string; allocationDate: string; debitVoucherNumber: string; creditVoucherNumber: string; amount: string; foreignAmount: string | null; origin: string; isActive: boolean; voidReason: string | null }

const ORIGIN_VI: Record<string, string> = { VOUCHER: 'Theo chứng từ', MANUAL: 'Khớp tay', AUTO_FIFO: 'Tự động FIFO', REVERSAL: 'Đảo chứng từ' }

const minMoney = (a: string, b: string) => (compareMoney(a, b) <= 0 ? a : b)
/** Compares two non-negative decimal strings. */
function compareMoney(a: string, b: string): number {
  const [ai, af = ''] = a.split('.'), [bi, bf = ''] = b.split('.')
  const n = Math.max(af.length, bf.length)
  const x = BigInt(ai + af.padEnd(n, '0')), y = BigInt(bi + bf.padEnd(n, '0'))
  return x < y ? -1 : x > y ? 1 : 0
}

function AllocateDialog({ debit, credit, asOf, onClose, onDone }: { debit: OpenItem; credit: OpenItem; asOf: string; onClose: () => void; onDone: () => void }) {
  const foreign = debit.foreignCurrency ?? null
  const mixed = (debit.foreignCurrency ?? null) !== (credit.foreignCurrency ?? null)
  const [amount, setAmount] = useState(formatMoney(minMoney(debit.open, credit.open)))
  const [foreignAmount, setForeignAmount] = useState('')
  const later = debit.postingDate > credit.postingDate ? debit.postingDate : credit.postingDate
  const [date, setDate] = useState(later > asOf ? later : asOf)
  const { busy, error, setError, run } = useAction()
  const save = () => run(async () => {
    const amt = parseMoney(amount)
    if (!amt || amt === '0') { setError(new Error('Nhập số tiền phân bổ lớn hơn 0.')); return }
    const fa = foreign ? parseRate(foreignAmount) : null
    if (foreign && !fa) { setError(new Error(`Nhập số nguyên tệ ${foreign} phân bổ.`)); return }
    await post('/settlement/allocate', { lineA: debit.journalLineId, lineB: credit.journalLineId, amount: amt, date, foreignAmount: fa })
    onDone()
  })
  return <Modal title="Phân bổ công nợ (khớp tay)" onClose={onClose} busy={busy} onSubmit={mixed ? undefined : save}
    footer={<><button onClick={onClose} disabled={busy}>Hủy</button><button className="primary" disabled={busy || mixed} onClick={save}>Phân bổ</button></>}>
    <ErrorBox error={error} />
    {mixed && <div className="msg error">Hai khoản khác loại tiền ({debit.foreignCurrency ?? 'VND'} và {credit.foreignCurrency ?? 'VND'}): không khớp tay được vì phát sinh chênh lệch tỷ giá — dùng "Thu/chi ngoại tệ theo hóa đơn" ở màn hình Ngoại tệ.</div>}
    <p>Bên Nợ: <b>{debit.voucherNumber}</b> ngày {debit.postingDate}, còn {formatMoney(debit.open)}<br />
      Bên Có: <b>{credit.voucherNumber}</b> ngày {credit.postingDate}, còn {formatMoney(credit.open)}</p>
    <div className="form-row">
      <label>Số tiền<input value={amount} onChange={e => setAmount(e.target.value)} inputMode="decimal" /></label>
      {foreign && <label>Nguyên tệ ({foreign})<input value={foreignAmount} onChange={e => setForeignAmount(e.target.value)} inputMode="decimal" /></label>}
      <label>Ngày phân bổ<input type="date" value={date} min={later} onChange={e => setDate(e.target.value)} /></label>
    </div>
    <p className="muted">Không vượt số còn lại của mỗi bên; ngày phân bổ không trước ngày hạch toán của hai chứng từ và phải thuộc kỳ chưa khóa. Phân bổ không sinh bút toán, chỉ ghi nhận chứng từ nào thanh toán cho chứng từ nào.</p>
  </Modal>
}

function VoidAllocationDialog({ alloc, onClose, onDone }: { alloc: AllocationRow; onClose: () => void; onDone: () => void }) {
  const [reason, setReason] = useState('')
  const { busy, error, run } = useAction()
  const save = () => run(async () => { await post(`/settlement/${alloc.id}/void`, { reason }); onDone() })
  return <Modal title="Hủy phân bổ" onClose={onClose} busy={busy} onSubmit={reason.trim() ? save : undefined}
    footer={<><button onClick={onClose} disabled={busy}>Không hủy</button><button className="primary" disabled={busy || !reason.trim()} onClick={save}>Hủy phân bổ</button></>}>
    <ErrorBox error={error} />
    <p>Phân bổ {formatMoney(alloc.amount)} giữa <b>{alloc.debitVoucherNumber}</b> và <b>{alloc.creditVoucherNumber}</b> ngày {alloc.allocationDate}.
      Sau khi hủy, hai chứng từ trở lại trạng thái còn mở; bản ghi phân bổ và lý do hủy vẫn được lưu trong nhật ký.</p>
    <label>Lý do hủy<input value={reason} onChange={e => setReason(e.target.value)} /></label>
  </Modal>
}

interface JournalRow { postingDate: string; voucherType: string; voucherNumber: string; lineNo: number; accountCode: string; partnerCode: string | null; debit: string; credit: string; description: string | null }

/** Sổ Nhật ký chung: every posted line in date / voucher order. */
export function GeneralJournalView() {
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [rows, setRows] = useState<JournalRow[] | null>(null)
  const { busy, error, run } = useAction()
  return (
    <>
      <div className="toolbar">
        <h2>Sổ Nhật ký chung</h2>
        <label>Từ ngày<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" disabled={busy} onClick={() => run(async () => setRows(await get<JournalRow[]>(`/reports/general-journal?from=${from}&to=${to}`)))}>Xem</button>
        <ExportButton fileName={`So_nhat_ky_chung_${from}_${to}`} sheet="Nhật ký chung" disabled={!rows}
          columns={[{ header: 'Ngày' }, { header: 'Số CT' }, { header: 'Loại' }, { header: 'Diễn giải', width: 40 }, { header: 'TK' }, { header: 'Đối tượng' },
            { header: 'Nợ', numeric: true }, { header: 'Có', numeric: true }]}
          rows={(rows ?? []).map(r => [r.postingDate, r.voucherNumber, TYPE_VI[r.voucherType] ?? r.voucherType, r.description, r.accountCode, r.partnerCode, r.debit, r.credit])} />
      </div>
      <ErrorBox error={error} />
      {rows && <div className="scroll"><table className="grid">
        <thead><tr><th>Ngày</th><th>Số CT</th><th>Loại</th><th>Diễn giải</th><th>TK</th><th>Đối tượng</th><th className="num">Nợ</th><th className="num">Có</th></tr></thead>
        <tbody>{rows.map(r => <tr key={`${r.voucherNumber}-${r.lineNo}`}><td>{r.postingDate}</td><td>{r.voucherNumber}</td><td>{TYPE_VI[r.voucherType] ?? r.voucherType}</td>
          <td>{r.description}</td><td>{r.accountCode}</td><td>{r.partnerCode}</td>
          <td className="num">{r.debit === '0' ? '' : formatMoney(r.debit)}</td><td className="num">{r.credit === '0' ? '' : formatMoney(r.credit)}</td></tr>)}
          {rows.length === 0 && <tr><td colSpan={8} className="muted">Không có bút toán trong khoảng ngày.</td></tr>}</tbody>
        <tfoot><tr><td colSpan={6}>Cộng</td><td className="num">{formatMoney(addMoney(rows.map(r => r.debit)))}</td><td className="num">{formatMoney(addMoney(rows.map(r => r.credit)))}</td></tr></tfoot>
      </table></div>}
    </>
  )
}