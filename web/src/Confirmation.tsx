import { useEffect, useState } from 'react'
import { get, session, type Account, type Partner } from './api'
import { ErrorBox, Modal, PrintSheet, printSheet, useAction } from './common'
import { amountInWords, balanceText, formatMoney } from './money'

interface LedgerRow { postingDate: string; voucherNumber: string; description: string | null; debit: string; credit: string; runningBalance: string }
interface Ledger { accountCode: string; accountName: string; openingBalance: string; rows: LedgerRow[]; totalDebit: string; totalCredit: string; closingBalance: string }
interface CompanyInfo { id: string; name: string; taxCode?: string | null }

const d = (s: string) => s.split('-').reverse().join('/')
const abs = (v: string) => (v.startsWith('-') ? v.slice(1) : v)

/** Biên bản đối chiếu công nợ: opening balance, movements and closing balance of one partner on one account, for both parties to sign. */
export function ConfirmationDialog({ account, partner, to, onClose }: { account: Account; partner: Partner; to: string; onClose: () => void }) {
  const [from, setFrom] = useState(to.slice(0, 4) + '-01-01')
  const [ledger, setLedger] = useState<Ledger | null>(null)
  const [company, setCompany] = useState<CompanyInfo | null>(null)
  const { error, run } = useAction()
  useEffect(() => {
    run(async () => {
      const q = new URLSearchParams({ accountId: account.id, partnerId: partner.id, from, to })
      setLedger(await get<Ledger>(`/reports/account-ledger?${q}`))
      setCompany((await get<CompanyInfo[]>('/me/companies')).find(c => c.id === session.companyId) ?? null)
    })
  }, [account.id, partner.id, from, to, run])

  return (
    <Modal title={`Biên bản đối chiếu công nợ — ${partner.code}`} onClose={onClose}
      footer={<><button onClick={onClose}>Đóng</button><button className="primary" disabled={!ledger} onClick={printSheet}>In biên bản</button></>}>
      <ErrorBox error={error} />
      <label>Từ ngày<input type="date" value={from} max={to} onChange={e => e.target.value && setFrom(e.target.value)} /></label>
      {!ledger ? <span className="muted">Đang tải…</span> : <>
        <p>TK {ledger.accountCode} – {ledger.accountName}, từ {d(from)} đến {d(to)}: {ledger.rows.length} chứng từ.</p>
        <p>Số dư đầu kỳ: {balanceText(ledger.openingBalance, partner.name)}<br />
          Phát sinh Nợ {formatMoney(ledger.totalDebit)} · Phát sinh Có {formatMoney(ledger.totalCredit)}<br />
          <b>Số dư cuối kỳ: {balanceText(ledger.closingBalance, partner.name)}</b></p>
        <p className="muted">Bấm "In biên bản" để in (hoặc lưu PDF) kèm bảng chi tiết chứng từ và ô ký hai bên. Địa chỉ, người đại diện để trống cho hai bên ghi tay.</p>
        <ConfirmationPrint ledger={ledger} company={company} partner={partner} from={from} to={to} />
      </>}
    </Modal>
  )
}

function ConfirmationPrint({ ledger: l, company, partner, from, to }: { ledger: Ledger; company: CompanyInfo | null; partner: Partner; from: string; to: string }) {
  const closing = l.closingBalance
  return (
    <PrintSheet>
      <div className="center"><b>CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM</b><br /><b>Độc lập – Tự do – Hạnh phúc</b></div>
      <h1>BIÊN BẢN ĐỐI CHIẾU CÔNG NỢ</h1>
      <div className="center">Từ ngày {d(from)} đến ngày {d(to)} — Tài khoản {l.accountCode} ({l.accountName})</div>
      <p>Hôm nay, ngày ..... tháng ..... năm ......., chúng tôi gồm:</p>
      <p><b>Bên A: {company?.name ?? ''}</b><br />Mã số thuế: {company?.taxCode ?? '................'}<br />
        Địa chỉ: ..........................................................................................<br />
        Đại diện: ......................................... Chức vụ: ...............................</p>
      <p><b>Bên B: {partner.name}</b> (mã {partner.code})<br />Mã số thuế: {partner.taxCode ?? '................'}<br />
        Địa chỉ: ..........................................................................................<br />
        Đại diện: ......................................... Chức vụ: ...............................</p>
      <p>Cùng đối chiếu công nợ theo sổ sách của Bên A như sau:</p>
      <table>
        <thead><tr><th>Ngày</th><th>Số chứng từ</th><th>Diễn giải</th><th>Phát sinh Nợ</th><th>Phát sinh Có</th></tr></thead>
        <tbody>
          <tr><td colSpan={3}><b>Số dư đầu kỳ</b></td>
            <td className="num">{l.openingBalance.startsWith('-') || l.openingBalance === '0' ? '' : formatMoney(l.openingBalance)}</td>
            <td className="num">{l.openingBalance.startsWith('-') ? formatMoney(abs(l.openingBalance)) : ''}</td></tr>
          {l.rows.map((r, i) => <tr key={i}><td>{d(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{r.description}</td>
            <td className="num">{r.debit === '0' ? '' : formatMoney(r.debit)}</td><td className="num">{r.credit === '0' ? '' : formatMoney(r.credit)}</td></tr>)}
          <tr><td colSpan={3}><b>Cộng phát sinh</b></td><td className="num"><b>{formatMoney(l.totalDebit)}</b></td><td className="num"><b>{formatMoney(l.totalCredit)}</b></td></tr>
          <tr><td colSpan={3}><b>Số dư cuối kỳ</b></td>
            <td className="num"><b>{closing.startsWith('-') || closing === '0' ? '' : formatMoney(closing)}</b></td>
            <td className="num"><b>{closing.startsWith('-') ? formatMoney(abs(closing)) : ''}</b></td></tr>
        </tbody>
      </table>
      <p><b>Kết luận:</b> Đến ngày {d(to)}, {balanceText(closing, partner.name)}
        {closing !== '0' && <><br />Bằng chữ: <i>{amountInWords(abs(closing))}</i></>}</p>
      <p>Nếu Bên B có số liệu khác, đề nghị ghi rõ chênh lệch và chứng từ liên quan: ...........................................................</p>
      <p>Biên bản lập thành 02 bản, mỗi bên giữ 01 bản có giá trị như nhau.</p>
      <div className="signs" style={{ gridTemplateColumns: '1fr 1fr' }}>
        <div><b>ĐẠI DIỆN BÊN A</b><br /><i>(Ký, họ tên, đóng dấu)</i></div><div><b>ĐẠI DIỆN BÊN B</b><br /><i>(Ký, họ tên, đóng dấu)</i></div>
      </div>
      <div className="foot">Mẫu in nội bộ HAK Accounting — doanh nghiệp tự điều chỉnh nội dung khi cần. In lúc {new Date().toLocaleString('vi-VN')}.</div>
    </PrintSheet>
  )
}
