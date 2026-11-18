import { useEffect, useState } from 'react'
import { MoneyInput } from './MoneyInput'
import { goTo, takeParams } from './nav'
import { get, post, type OpenItem, type TrialBalanceRow } from './api'
import { AccountSelect, ErrorBox, ExportButton, Modal, PartnerSelect, TYPE_VI, useAction, useCatalog, usePrintBook } from './common'
import { BankBookPrint, CashBookPrint, CashDetailBookPrint, CostBookPrint, ForeignLedgerPrint, ledgerCurrency, DetailLedgerPrint, GeneralLedgerPrint, JournalBookPrint, SpecialJournalPrint, TrialBalancePrint, type SpecialJournal } from './books'
import { useCompanyInfo } from './forms'
import { addMoney, formatMoney, parseMoney, parseRate, today } from './money'
import { ConfirmationDialog } from './Confirmation'
import { DateInput } from './DateInput'

const firstOfMonth = () => today().slice(0, 8) + '01'

export function TrialBalanceView() {
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [rollup, setRollup] = useState(true)
  const [data, setData] = useState<{ isBalanced: boolean; rows: TrialBalanceRow[]; groups: TrialBalanceRow[]; totals: Record<string, string> } | null>(null)
  const [shown, setShown] = useState({ from, to })
  const company = useCompanyInfo()
  const [book, setBook] = usePrintBook<'S06'>()
  const { busy, error, run } = useAction()
  const load = () => run(async () => { setData(await get(`/reports/trial-balance?from=${from}&to=${to}&rollup=${rollup}`)); setShown({ from, to }) })
  const t = data?.totals
  // Parent (summary) accounts interleaved with their children by account code; they never enter the totals.
  const lines = data ? [...data.rows, ...(data.groups ?? [])].sort((a, b) => (a.accountCode < b.accountCode ? -1 : a.accountCode > b.accountCode ? 1 : (b.isGroup ? 1 : 0) - (a.isGroup ? 1 : 0))) : []
  const amounts = (r: TrialBalanceRow) => [r.openingDebit, r.openingCredit, r.periodDebit, r.periodCredit, r.closingDebit, r.closingCredit]
  return (
    <>
      <div className="toolbar">
        <h2>Bảng cân đối số phát sinh</h2>
        <label>Từ ngày<DateInput value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<DateInput value={to} onChange={e => setTo(e.target.value)} /></label>
        <label><input type="checkbox" checked={rollup} onChange={e => setRollup(e.target.checked)} /> Cộng dồn TK tổng hợp</label>
        <button className="primary" onClick={load} disabled={busy}>Xem</button>
        <ExportButton fileName={`BCDPS_${from}_${to}`} sheet="BCĐPS" disabled={!data}
          columns={[{ header: 'TK' }, { header: 'Tên tài khoản', width: 40 }, ...['Dư đầu Nợ', 'Dư đầu Có', 'PS Nợ', 'PS Có', 'Dư cuối Nợ', 'Dư cuối Có'].map(h => ({ header: h, numeric: true }))]}
          rows={[...lines.map(r => [r.accountCode, '  '.repeat(r.level ?? 0) + r.accountName, ...amounts(r)]),
            ...(t ? [['Cộng', '', t.totalOpeningDebit, t.totalOpeningCredit, t.totalPeriodDebit, t.totalPeriodCredit, t.totalClosingDebit, t.totalClosingCredit]] : [])]} />
        <button disabled={!data} title="Mẫu số S06-DN, Phụ lục III TT 99/2025" onClick={() => setBook('S06')}>In (S06-DN)</button>
      </div>
      {data && t && book === 'S06' && <TrialBalancePrint company={company.name} address={company.address} lines={lines} totals={t} from={shown.from} to={shown.to} />}
      <ErrorBox error={error} />
      {data && (
        <>
          <div className={`msg ${data.isBalanced ? 'ok' : 'error'}`}>{data.isBalanced ? 'Cân đối: tổng Nợ = tổng Có ở mọi cột.' : 'KHÔNG cân đối — cần kiểm tra ngay.'}</div>
          <div className="scroll">
            <table className="grid">
              <thead><tr><th>TK</th><th>Tên tài khoản</th><th className="num">Dư đầu Nợ</th><th className="num">Dư đầu Có</th><th className="num">PS Nợ</th><th className="num">PS Có</th><th className="num">Dư cuối Nợ</th><th className="num">Dư cuối Có</th></tr></thead>
              <tbody>{lines.map(r => (
                <tr key={r.accountCode + (r.isGroup ? 'g' : '')} style={r.isGroup ? { fontWeight: 600 } : undefined} className={r.isGroup ? undefined : 'link'}
                  title={r.isGroup ? undefined : 'Bấm để mở sổ chi tiết tài khoản này'} onClick={r.isGroup ? undefined : () => goTo('ledger', { accountId: r.accountId, from, to })}><td>{r.accountCode}</td>
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

interface LedgerRow { voucherId: string; postingDate: string; voucherDate?: string | null; voucherType: string; voucherNumber: string; description: string | null; counterAccounts: string; debit: string; credit: string; runningBalance: string; foreignCurrency?: string | null; foreignAmount?: string | null }
interface Ledger { accountCode: string; accountName: string; openingBalance: string; rows: LedgerRow[]; totalDebit: string; totalCredit: string; closingBalance: string; foreignClosing?: { currency: string; closing: string }[] }

const signed = (v: string) => (v.startsWith('-') ? `${formatMoney(v.slice(1))} Có` : v === '0' ? '0' : `${formatMoney(v)} Nợ`)

export function AccountLedgerView() {
  const { accounts, partners } = useCatalog()
  const [boot] = useState(() => takeParams('ledger'))   // opened from a trial-balance row
  const [accountId, setAccountId] = useState(boot?.accountId ?? '')
  const [partnerId, setPartnerId] = useState(boot?.partnerId ?? '')
  const [from, setFrom] = useState(boot?.from ?? firstOfMonth())
  const [to, setTo] = useState(boot?.to ?? today())
  const [data, setData] = useState<Ledger | null>(null)
  const company = useCompanyInfo()
  const [book, setBook] = usePrintBook<'S03b' | 'S07' | 'S07a' | 'S08' | 'S31' | 'S32' | 'S33' | 'S36' | 'S38'>()
  const acc = accounts.find(a => a.id === accountId)
  // The detail book that fits the account: cash book, payment ledger of a customer / supplier account, or the general detail ledger.
  const detail: 'S07' | 'S08' | 'S31' | 'S38' = acc?.cashKind === 'Cash' ? 'S07' : acc?.cashKind === 'Bank' ? 'S08' : acc && acc.openItemNature !== 'None' ? 'S31' : 'S38'
  const DETAIL_VI = { S07: 'Sổ quỹ tiền mặt (S07-DN)', S08: 'Sổ tiền gửi (S08-DN)', S31: 'Sổ chi tiết thanh toán (S31-DN)', S38: 'Sổ chi tiết (S38-DN)' }
  const { busy, error, run } = useAction()
  const load = () => run(async () => {
    const q = new URLSearchParams({ accountId, from, to, ...(partnerId ? { partnerId } : {}) })
    setData(await get(`/reports/account-ledger?${q}`))
  })
  useEffect(() => { if (boot?.accountId) load() }, [])   // eslint-disable-line react-hooks/exhaustive-deps -- once, with the parameters handed over
  return (
    <>
      <div className="toolbar">
        <h2>Sổ chi tiết tài khoản / Sổ quỹ</h2>
        <label>Tài khoản<AccountSelect accounts={accounts} value={accountId} onChange={setAccountId} /></label>
        <label>Đối tượng<PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>
        <label>Từ ngày<DateInput value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<DateInput value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" onClick={() => load()} disabled={busy || !accountId}>Xem</button>
        <ExportButton fileName={`So_chi_tiet_${data?.accountCode ?? ''}_${from}_${to}`} sheet={`Sổ TK ${data?.accountCode ?? ''}`} disabled={!data}
          columns={[{ header: 'Ngày' }, { header: 'Số CT' }, { header: 'Loại' }, { header: 'Diễn giải', width: 40 }, { header: 'TK đối ứng' },
            { header: 'Nợ', numeric: true }, { header: 'Có', numeric: true }, { header: 'Số dư (+Nợ / −Có)', numeric: true }]}
          rows={data ? [['', '', '', 'Số dư đầu kỳ', '', '', '', data.openingBalance],
            ...data.rows.map(r => [r.postingDate, r.voucherNumber, TYPE_VI[r.voucherType] ?? r.voucherType, r.description, r.counterAccounts, r.debit, r.credit, r.runningBalance]),
            ['', '', '', 'Cộng phát sinh / số dư cuối kỳ', '', data.totalDebit, data.totalCredit, data.closingBalance]] : []} />
        <button disabled={!data || !!partnerId} title={partnerId ? 'Sổ Cái in cho cả tài khoản: bỏ chọn đối tượng' : 'Mẫu số S03b-DN, Phụ lục III TT 99/2025'} onClick={() => setBook('S03b')}>In Sổ Cái (S03b-DN)</button>
        <button disabled={!data} title="Mẫu theo Phụ lục III TT 99/2025" onClick={() => setBook(detail)}>In {DETAIL_VI[detail]}</button>
        {data && ledgerCurrency(data) && (detail === 'S31' || detail === 'S38') && <button title="Mẫu S32-DN (131, 331) / S33-DN (các TK thanh toán khác), Phụ lục III TT 99/2025"
          onClick={() => setBook(detail === 'S31' ? 'S32' : 'S33')}>In sổ ngoại tệ ({detail === 'S31' ? 'S32' : 'S33'}-DN)</button>}
        {detail === 'S38' && <button disabled={!data} title="Mẫu số S36-DN, Phụ lục III TT 99/2025 (TK chi phí: phần Chia ra theo TK đối ứng)" onClick={() => setBook('S36')}>In sổ chi phí (S36-DN)</button>}
        {detail === 'S07' && <button disabled={!data} title="Mẫu số S07a-DN, Phụ lục III TT 99/2025 (có TK đối ứng)" onClick={() => setBook('S07a')}>In sổ chi tiết quỹ (S07a-DN)</button>}
      </div>
      {data && book === 'S03b' && <GeneralLedgerPrint company={company.name} address={company.address} ledger={data} from={from} to={to} />}
      {data && (book === 'S32' || book === 'S33') && <ForeignLedgerPrint company={company.name} address={company.address} ledger={data} from={from} to={to} form={book}
        partner={partners.find(p => p.id === partnerId)?.name ?? null} />}
      {data && book === 'S36' && <CostBookPrint company={company.name} address={company.address} ledger={data} from={from} to={to} />}
      {data && book === 'S07a' && <CashDetailBookPrint company={company.name} address={company.address} ledger={data} from={from} to={to} />}
      {data && book === 'S07' && <CashBookPrint company={company.name} address={company.address} ledger={data} from={from} to={to} />}
      {data && book === 'S08' && <BankBookPrint company={company.name} address={company.address} ledger={data} from={from} to={to} bankName={acc?.bankName} bankAccountNo={acc?.bankAccountNo} />}
      {data && (book === 'S31' || book === 'S38') && <DetailLedgerPrint company={company.name} address={company.address} ledger={data} from={from} to={to} form={book}
        partner={partners.find(p => p.id === partnerId)?.name ?? null} />}
      <ErrorBox error={error} />
      {data && (
        <div className="scroll">
          <table className="grid">
            <thead><tr><th>Ngày</th><th>Số CT</th><th>Loại</th><th>Diễn giải</th><th>TK đối ứng</th><th className="num">Nợ</th><th className="num">Có</th><th className="num">Số dư</th></tr></thead>
            <tbody>
              <tr><td colSpan={7}><strong>Số dư đầu kỳ</strong></td><td className="num">{signed(data.openingBalance)}</td></tr>
              {data.rows.map((r, i) => (
                <tr key={i} className="link" title="Bấm để mở chứng từ" onClick={() => goTo('vouchers', { voucherId: r.voucherId, date: r.postingDate })}><td>{r.postingDate}</td><td>{r.voucherNumber}</td><td>{TYPE_VI[r.voucherType] ?? r.voucherType}</td><td>{r.description}</td><td>{r.counterAccounts}</td>
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

  // pid: the partner to show (a click on an aging row passes it before the state has updated).
  const load = (pid: string = partnerId) => run(async () => {
    setAging(await get(`/reports/aging?accountId=${accountId}&asOf=${asOf}`))
    setRecon(await get(`/reports/subledger-reconciliation?asOf=${asOf}`))
    setItems(pid ? await get(`/settlement/open-items?accountId=${accountId}&partnerId=${pid}&asOf=${asOf}`) : [])
    setAllocs(pid ? await get(`/settlement/allocations?accountId=${accountId}&partnerId=${pid}`) : [])
    setPickD(''); setPickC('')
  })
  const auto = () => run(async () => { await post('/settlement/auto', { accountId, partnerId, date: asOf }); await load() })

  return (
    <>
      <div className="toolbar">
        <h2>Công nợ</h2>
        <label>Tài khoản công nợ<AccountSelect accounts={openAccounts} value={accountId} onChange={setAccountId} /></label>
        <label>Đối tượng (chi tiết)<PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>
        <label>Đến ngày<DateInput value={asOf} onChange={e => setAsOf(e.target.value)} /></label>
        <button className="primary" onClick={() => load()} disabled={busy || !accountId}>Xem</button>
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
              <tr key={r.partnerId} onClick={() => { setPartnerId(r.partnerId); load(r.partnerId) }} style={{ cursor: 'pointer' }} title="Bấm để xem chứng từ còn mở của đối tượng này">
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
                <td>{o.postingDate}</td><td>{o.dueDate}</td><td><a href="#" title="Mở chứng từ" onClick={e => { e.preventDefault(); goTo('vouchers', { voucherId: o.voucherId, date: o.postingDate }) }}>{o.voucherNumber}</a></td><td>{TYPE_VI[o.voucherType] ?? o.voucherType}</td>
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
      <label>Số tiền<MoneyInput value={amount} onChange={e => setAmount(e.target.value)} /></label>
      {foreign && <label>Nguyên tệ ({foreign})<MoneyInput kind="rate" value={foreignAmount} onChange={e => setForeignAmount(e.target.value)} /></label>}
      <label>Ngày phân bổ<DateInput value={date} min={later} onChange={e => setDate(e.target.value)} /></label>
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

interface JournalRow { voucherId: string; postingDate: string; voucherDate?: string | null; voucherType: string; voucherNumber: string; lineNo: number; accountCode: string; partnerCode: string | null; debit: string; credit: string; description: string | null }

/** Sổ Nhật ký chung: every posted line in date / voucher order. */
export function GeneralJournalView() {
  const [from, setFrom] = useState(firstOfMonth())
  const [to, setTo] = useState(today())
  const [rows, setRows] = useState<JournalRow[] | null>(null)
  const company = useCompanyInfo()
  const [book, setBook] = usePrintBook<'S03a' | 'special'>()
  const [shown, setShown] = useState({ from, to })
  const { accounts } = useCatalog()
  // Nhật ký đặc biệt: the account the journal is kept for (cash / bank, payable, receivable); empty = every account of that kind.
  const [special, setSpecial] = useState<SpecialJournal>('S03a1')
  const [focus, setFocus] = useState('')
  const kindOf = (a: (typeof accounts)[number]) => special === 'S03a1' || special === 'S03a2' ? a.cashKind !== 'None' : special === 'S03a3' ? a.openItemNature === 'Payable' : a.openItemNature === 'Receivable'
  const candidates = accounts.filter(a => a.isPostable && kindOf(a))
  const focusCodes = focus ? [focus] : candidates.map(a => a.code)
  const { busy, error, run } = useAction()
  return (
    <>
      <div className="toolbar">
        <h2>Sổ Nhật ký chung</h2>
        <label>Từ ngày<DateInput value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<DateInput value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" disabled={busy} onClick={() => run(async () => { setRows(await get<JournalRow[]>(`/reports/general-journal?from=${from}&to=${to}`)); setShown({ from, to }) })}>Xem</button>
        <ExportButton fileName={`So_nhat_ky_chung_${from}_${to}`} sheet="Nhật ký chung" disabled={!rows}
          columns={[{ header: 'Ngày' }, { header: 'Số CT' }, { header: 'Loại' }, { header: 'Diễn giải', width: 40 }, { header: 'TK' }, { header: 'Đối tượng' },
            { header: 'Nợ', numeric: true }, { header: 'Có', numeric: true }]}
          rows={(rows ?? []).map(r => [r.postingDate, r.voucherNumber, TYPE_VI[r.voucherType] ?? r.voucherType, r.description, r.accountCode, r.partnerCode, r.debit, r.credit])} />
        <button disabled={!rows} title="Mẫu số S03a-DN, Phụ lục III TT 99/2025" onClick={() => setBook('S03a')}>In sổ (S03a-DN)</button>
      </div>
      <div className="toolbar">
        <label>Nhật ký đặc biệt<select value={special} onChange={e => { setSpecial(e.target.value as SpecialJournal); setFocus('') }}>
          <option value="S03a1">S03a1-DN Nhật ký thu tiền</option><option value="S03a2">S03a2-DN Nhật ký chi tiền</option>
          <option value="S03a3">S03a3-DN Nhật ký mua hàng</option><option value="S03a4">S03a4-DN Nhật ký bán hàng</option></select></label>
        <label>Tài khoản<select value={focus} onChange={e => setFocus(e.target.value)}><option value="">Tất cả ({candidates.map(a => a.code).join(', ') || 'chưa có'})</option>
          {candidates.map(a => <option key={a.id} value={a.code}>{a.code} – {a.name}</option>)}</select></label>
        <button disabled={!rows || !focusCodes.length} title="Mẫu theo Phụ lục III TT 99/2025; cột TK đối ứng: các TK dùng nhiều nhất trong kỳ" onClick={() => setBook('special')}>In nhật ký đặc biệt</button>
      </div>
      {rows && book === 'S03a' && <JournalBookPrint company={company.name} address={company.address} rows={rows} from={shown.from} to={shown.to} />}
      {rows && book === 'special' && <SpecialJournalPrint company={company.name} address={company.address} form={special} lines={rows} focusAccounts={focusCodes} from={shown.from} to={shown.to} />}
      <ErrorBox error={error} />
      {rows && <div className="scroll"><table className="grid">
        <thead><tr><th>Ngày</th><th>Số CT</th><th>Loại</th><th>Diễn giải</th><th>TK</th><th>Đối tượng</th><th className="num">Nợ</th><th className="num">Có</th></tr></thead>
        <tbody>{rows.map(r => <tr key={`${r.voucherNumber}-${r.lineNo}`} className="link" title="Bấm để mở chứng từ" onClick={() => goTo('vouchers', { voucherId: r.voucherId, date: r.postingDate })}><td>{r.postingDate}</td><td>{r.voucherNumber}</td><td>{TYPE_VI[r.voucherType] ?? r.voucherType}</td>
          <td>{r.description}</td><td>{r.accountCode}</td><td>{r.partnerCode}</td>
          <td className="num">{r.debit === '0' ? '' : formatMoney(r.debit)}</td><td className="num">{r.credit === '0' ? '' : formatMoney(r.credit)}</td></tr>)}
          {rows.length === 0 && <tr><td colSpan={8} className="muted">Không có bút toán trong khoảng ngày.</td></tr>}</tbody>
        <tfoot><tr><td colSpan={6}>Cộng</td><td className="num">{formatMoney(addMoney(rows.map(r => r.debit)))}</td><td className="num">{formatMoney(addMoney(rows.map(r => r.credit)))}</td></tr></tfoot>
      </table></div>}
    </>
  )
}