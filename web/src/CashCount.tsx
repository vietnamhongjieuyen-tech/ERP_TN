import { useEffect, useMemo, useState } from 'react'
import { get, session, type Account } from './api'
import { AccountSelect, ErrorBox, Modal, PrintSheet, printSheet, useAction } from './common'
import { addMoney, formatMoney, today } from './money'
import { FORM_FOOT, FormHeader } from './forms'

// Bảng kiểm kê quỹ (dùng cho VNĐ), Mẫu số 08a-TT Phụ lục I TT 99/2025: count of notes by denomination against the book balance of a
// cash account on a date. Chênh lệch III = I − II (sổ − thực tế): dương là thiếu quỹ, âm là thừa. Nothing is posted:
// a difference is handled by a voucher the accountant makes after the cause is found.

export const DENOMINATIONS = ['500000', '200000', '100000', '50000', '20000', '10000', '5000', '2000', '1000', '500', '200']

/** Counted amount: Σ denomination × number of notes (whole numbers only). */
export function countedTotal(counts: Record<string, string>): string {
  return DENOMINATIONS.reduce((s, d) => s + BigInt(d) * BigInt(/^\d+$/.test(counts[d] ?? '') ? counts[d] : '0'), 0n).toString()
}

interface Ledger { accountCode: string; accountName: string; closingBalance: string }
interface CompanyInfo { id: string; name: string; taxCode?: string | null }
const d = (s: string) => s.split('-').reverse().join('/')

export function CashCountDialog({ accounts, onClose }: { accounts: Account[]; onClose: () => void }) {
  const cash = useMemo(() => accounts.filter(a => a.cashKind === 'Cash' && a.isPostable && a.isActive), [accounts])
  const [accountId, setAccountId] = useState(cash[0]?.id ?? '')
  const [date, setDate] = useState(today())
  const [counts, setCounts] = useState<Record<string, string>>({})
  const [members, setMembers] = useState('')
  const [reason, setReason] = useState('')
  const [ledger, setLedger] = useState<Ledger | null>(null)
  const [company, setCompany] = useState<CompanyInfo | null>(null)
  const { error, run } = useAction()
  useEffect(() => { if (!accountId && cash[0]) setAccountId(cash[0].id) }, [cash, accountId])
  useEffect(() => {
    if (!accountId) return
    run(async () => {
      const q = new URLSearchParams({ accountId, from: date.slice(0, 4) + '-01-01', to: date })
      setLedger(await get<Ledger>(`/reports/account-ledger?${q}`))
      setCompany((await get<CompanyInfo[]>('/me/companies')).find(c => c.id === session.companyId) ?? null)
    })
  }, [accountId, date, run])

  const counted = countedTotal(counts)
  const book = ledger?.closingBalance ?? '0'
  const diff = addMoney([book, '-' + counted])   // III = I − II: book − counted (positive: cash short)
  const badCount = DENOMINATIONS.some(x => counts[x] && !/^\d+$/.test(counts[x]))

  return (
    <Modal title="Bảng kiểm kê quỹ (dùng cho VNĐ)" onClose={onClose}
      footer={<><button onClick={onClose}>Đóng</button><button className="primary" disabled={!ledger || badCount} onClick={printSheet}>In bảng kiểm kê</button></>}>
      <ErrorBox error={error} />
      <div className="form-row">
        <label>Quỹ<AccountSelect accounts={accounts} value={accountId} onChange={setAccountId} filter={a => a.cashKind === 'Cash'} /></label>
        <label>Kiểm kê đến hết ngày<input type="date" value={date} onChange={e => e.target.value && setDate(e.target.value)} /></label>
      </div>
      <table className="grid">
        <thead><tr><th className="num">Mệnh giá</th><th className="num">Số tờ</th><th className="num">Thành tiền</th></tr></thead>
        <tbody>{DENOMINATIONS.map(x => {
          const n = counts[x] ?? '', ok = n === '' || /^\d+$/.test(n)
          return <tr key={x}><td className="num">{formatMoney(x)}</td>
            <td><input className="num" size={6} aria-label={`Số tờ ${formatMoney(x)}`} value={n} onChange={e => setCounts(c => ({ ...c, [x]: e.target.value.trim() }))}
              style={ok ? undefined : { borderColor: 'var(--danger, #c00)' }} /></td>
            <td className="num">{ok && n ? formatMoney((BigInt(x) * BigInt(n)).toString()) : ''}</td></tr>
        })}</tbody>
        <tfoot>
          <tr><td colSpan={2}><b>Số kiểm kê thực tế</b></td><td className="num"><b>{formatMoney(counted)}</b></td></tr>
          <tr><td colSpan={2}>Số dư theo sổ{ledger ? ` (TK ${ledger.accountCode})` : ''}</td><td className="num">{ledger ? formatMoney(book) : '…'}</td></tr>
          <tr><td colSpan={2}><b>Chênh lệch (sổ − thực tế){ledger && diff !== '0' ? (diff.startsWith('-') ? ' — thừa quỹ' : ' — thiếu quỹ') : ''}</b></td><td className="num"><b>{ledger ? formatMoney(diff) : ''}</b></td></tr>
        </tfoot>
      </table>
      {badCount && <div className="field-warn">Số tờ phải là số nguyên không âm.</div>}
      <label>Thành viên ban kiểm kê (mỗi người một dòng: họ tên – chức vụ)<textarea rows={3} value={members} onChange={e => setMembers(e.target.value)} /></label>
      {ledger && diff !== '0' && <label>Lý do chênh lệch / kiến nghị<textarea rows={2} value={reason} onChange={e => setReason(e.target.value)} /></label>}
      <p className="muted">Theo Mẫu số 08a-TT (Phụ lục I TT 99/2025). Chỉ in, không ghi sổ. Chênh lệch thừa / thiếu xử lý bằng chứng từ riêng sau khi xác định nguyên nhân. Số dư theo sổ gồm các chứng từ đã ghi sổ đến hết ngày kiểm kê.</p>
      {ledger && <CashCountPrint company={company} ledger={ledger} date={date} counts={counts} counted={counted} diff={diff}
        members={members.split('\n').map(s => s.trim()).filter(Boolean)} reason={reason} />}
    </Modal>
  )
}

function CashCountPrint({ company, ledger, date, counts, counted, diff, members, reason }: {
  company: CompanyInfo | null; ledger: Ledger; date: string; counts: Record<string, string>; counted: string; diff: string; members: string[]; reason: string
}) {
  const abs = diff.startsWith('-') ? diff.slice(1) : diff
  return (
    <PrintSheet>
      <FormHeader company={company?.name ?? ''} second={<>Bộ phận: ................</>} formNo="08a - TT" />
      <h1>BẢNG KIỂM KÊ QUỸ</h1>
      <div className="center">(Dùng cho VNĐ)<br />Số: ..........</div>
      <p>Hôm nay, vào ..... giờ ..... ngày {d(date).split('/')[0]} tháng {d(date).split('/')[1]} năm {d(date).split('/')[2]}</p>
      <p>Chúng tôi gồm:</p>
      {members.length > 0
        ? <ul>{members.map((m, i) => <li key={i}>{m}</li>)}</ul>
        : <p>– Ông/Bà: ........................................ đại diện kế toán<br />– Ông/Bà: ........................................ đại diện thủ quỹ<br />– Ông/Bà: ........................................ đại diện ..........</p>}
      <p>Cùng tiến hành kiểm kê quỹ tiền mặt (TK {ledger.accountCode} – {ledger.accountName}) kết quả như sau:</p>
      <table>
        <thead><tr><th>STT</th><th>Diễn giải</th><th>Số lượng (tờ)</th><th>Số tiền</th></tr><tr className="center"><td>A</td><td>B</td><td>1</td><td>2</td></tr></thead>
        <tbody>
          <tr><td>I</td><td>Số dư theo sổ quỹ:</td><td className="center">x</td><td className="num">{formatMoney(ledger.closingBalance)}</td></tr>
          <tr><td>II</td><td>Số kiểm kê thực tế:</td><td className="center">x</td><td className="num">{formatMoney(counted)}</td></tr>
          {DENOMINATIONS.filter(x => counts[x] && counts[x] !== '0').map((x, i) => (
            <tr key={x}><td>{i + 1}</td><td>{i === 0 ? 'Trong đó: - Loại ' : '- Loại '}{formatMoney(x)}</td><td className="num">{formatMoney(counts[x])}</td><td className="num">{formatMoney((BigInt(x) * BigInt(counts[x])).toString())}</td></tr>))}
          <tr><td>III</td><td>Chênh lệch (III = I – II):</td><td className="center">x</td><td className="num"><b>{formatMoney(diff)}</b></td></tr>
        </tbody>
      </table>
      <p>- Lý do: + Thừa: {diff.startsWith('-') ? `${formatMoney(abs)} — ${reason || '..............................'}` : '..............................................'}<br />
        &nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;+ Thiếu: {diff !== '0' && !diff.startsWith('-') ? `${formatMoney(abs)} — ${reason || '..............................'}` : '..............................................'}</p>
      <p>- Kết luận sau khi kiểm kê quỹ: ................................................................................</p>
      <div className="signs" style={{ gridTemplateColumns: '1fr 1fr 1fr' }}>
        <div><b>Kế toán trưởng</b><br /><i>(Ký, họ tên)</i></div><div><b>Thủ quỹ</b><br /><i>(Ký, họ tên)</i></div><div><b>Người chịu trách nhiệm kiểm kê quỹ</b><br /><i>(Ký, họ tên)</i></div>
      </div>
      <div className="foot">{FORM_FOOT} In lúc {new Date().toLocaleString('vi-VN')}.</div>
    </PrintSheet>
  )
}
