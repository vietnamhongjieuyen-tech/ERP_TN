import { PrintSheet } from './common'
import { BookFoot, dmy, FormHeader } from './forms'
import { addMoney, formatMoney } from './money'

// Accounting books printed on the reference forms of TT 99/2025/TT-BTC Phụ lục III (hình thức Nhật ký chung).
// Layouts copied from Công báo 1575+1576: S03a-DN Sổ Nhật ký chung, S03b-DN Sổ Cái, S07-DN Sổ quỹ tiền mặt, S38-DN Sổ chi tiết.

export interface JournalLine { postingDate: string; voucherDate?: string | null; voucherNumber: string; lineNo: number; accountCode: string; debit: string; credit: string; description: string | null }
export interface LedgerLine { postingDate: string; voucherDate?: string | null; voucherNumber: string; voucherType: string; description: string | null; counterAccounts: string; debit: string; credit: string; runningBalance: string }
export interface LedgerData { accountCode: string; accountName: string; openingBalance: string; rows: LedgerLine[]; totalDebit: string; totalCredit: string; closingBalance: string }

const m = (v: string) => (v === '0' || v === '' ? '' : formatMoney(v))
/** Signed balance (debit positive) → [Nợ, Có] cells. */
const sides = (v: string): [string, string] => (v.startsWith('-') ? ['', formatMoney(v.slice(1))] : [m(v), ''])
const year = (from: string, to: string) => (from.slice(0, 4) === to.slice(0, 4) ? from.slice(0, 4) : `${from.slice(0, 4)}–${to.slice(0, 4)}`)
const period = (from: string, to: string) => <div className="center"><i>Từ ngày {dmy(from)} đến ngày {dmy(to)}</i></div>
const letters = (xs: string[]) => <tr className="center">{xs.map(x => <td key={x}>{x}</td>)}</tr>

/** Mẫu số S03a-DN — Sổ Nhật ký chung. */
export function JournalBookPrint({ company, address, rows, from, to }: { company: string; address: string; rows: JournalLine[]; from: string; to: string }) {
  return (
    <PrintSheet>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S03a-DN" />
      <h1>SỔ NHẬT KÝ CHUNG</h1>
      <div className="center"><b>Năm {year(from, to)}</b></div>
      {period(from, to)}
      <div style={{ textAlign: 'right' }}><i>Đơn vị tính: đồng</i></div>
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>Đã ghi Sổ Cái</th><th rowSpan={2}>STT dòng</th>
            <th rowSpan={2}>Số hiệu TK đối ứng</th><th colSpan={2}>Số phát sinh</th></tr>
          <tr><th>Số hiệu</th><th>Ngày, tháng</th><th>Nợ</th><th>Có</th></tr>
          {letters(['A', 'B', 'C', 'D', 'E', 'G', 'H', '1', '2'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>Số trang trước chuyển sang</td><td /><td /><td /><td /><td /></tr>
          {rows.map((r, i) => (
            <tr key={`${r.voucherNumber}-${r.lineNo}`}><td>{dmy(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{r.description}</td>
              <td className="center">x</td><td className="center">{i + 1}</td><td className="center">{r.accountCode}</td><td className="num">{m(r.debit)}</td><td className="num">{m(r.credit)}</td></tr>))}
          <tr><td /><td /><td /><td><b>Cộng chuyển sang trang sau</b></td><td className="center">x</td><td className="center">x</td><td className="center">x</td>
            <td className="num"><b>{formatMoney(addMoney(rows.map(r => r.debit)))}</b></td><td className="num"><b>{formatMoney(addMoney(rows.map(r => r.credit)))}</b></td></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

/** Rows of a ledger split by calendar month: each month's rows, movements, closing balance and cumulative movements of the quarter. */
function months(l: LedgerData) {
  const groups: { key: string; rows: LedgerLine[] }[] = []
  for (const r of l.rows) {
    const key = r.postingDate.slice(0, 7)
    if (groups.at(-1)?.key !== key) groups.push({ key, rows: [] })
    groups.at(-1)!.rows.push(r)
  }
  let quarter = '', qDebit: string[] = [], qCredit: string[] = []
  return groups.map(g => {
    const q = `${g.key.slice(0, 4)}-Q${Math.floor((Number(g.key.slice(5, 7)) - 1) / 3)}`
    if (q !== quarter) { quarter = q; qDebit = []; qCredit = [] }
    const debit = addMoney(g.rows.map(r => r.debit)), credit = addMoney(g.rows.map(r => r.credit))
    qDebit.push(debit); qCredit.push(credit)
    return { ...g, debit, credit, closing: g.rows.at(-1)!.runningBalance, quarterDebit: addMoney(qDebit), quarterCredit: addMoney(qCredit) }
  })
}

/** Mẫu số S03b-DN — Sổ Cái (hình thức Nhật ký chung). Trang sổ / STT dòng of the journal are left for the book's paper numbering. */
export function GeneralLedgerPrint({ company, address, ledger: l, from, to }: { company: string; address: string; ledger: LedgerData; from: string; to: string }) {
  const [od, oc] = sides(l.openingBalance)
  return (
    <PrintSheet>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S03b-DN" />
      <h1>SỔ CÁI</h1>
      <div className="center"><b>(Dùng cho hình thức kế toán Nhật ký chung)<br />Năm {year(from, to)}<br />Tên tài khoản: {l.accountName}<br />Số hiệu: {l.accountCode}</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th><th colSpan={2}>Nhật ký chung</th><th rowSpan={2}>Số hiệu TK đối ứng</th><th colSpan={2}>Số tiền</th></tr>
          <tr><th>Số hiệu</th><th>Ngày tháng</th><th>Trang sổ</th><th>STT dòng</th><th>Nợ</th><th>Có</th></tr>
          {letters(['A', 'B', 'C', 'D', 'E', 'G', 'H', '1', '2'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>- Số dư {from.endsWith('-01-01') ? 'đầu năm' : 'đầu kỳ'}</td><td /><td /><td /><td className="num">{od}</td><td className="num">{oc}</td></tr>
          {months(l).map(g => [
            <tr key={g.key + 'h'}><td /><td /><td /><td>- Số phát sinh trong tháng {g.key.slice(5, 7)}/{g.key.slice(0, 4)}</td><td /><td /><td /><td /><td /></tr>,
            ...g.rows.map((r, i) => (
              <tr key={g.key + i}><td>{dmy(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{r.description}</td><td /><td />
                <td className="center">{r.counterAccounts}</td><td className="num">{m(r.debit)}</td><td className="num">{m(r.credit)}</td></tr>)),
            <tr key={g.key + 'm'}><td /><td /><td /><td><b>- Cộng số phát sinh tháng</b></td><td /><td /><td /><td className="num"><b>{formatMoney(g.debit)}</b></td><td className="num"><b>{formatMoney(g.credit)}</b></td></tr>,
            <tr key={g.key + 'c'}><td /><td /><td /><td>- Số dư cuối tháng</td><td /><td /><td /><td className="num">{sides(g.closing)[0]}</td><td className="num">{sides(g.closing)[1]}</td></tr>,
            <tr key={g.key + 'q'}><td /><td /><td /><td>- Cộng luỹ kế từ đầu quý</td><td /><td /><td /><td className="num">{formatMoney(g.quarterDebit)}</td><td className="num">{formatMoney(g.quarterCredit)}</td></tr>,
          ])}
          {l.rows.length === 0 && <tr><td /><td /><td /><td>- Không phát sinh trong kỳ; số dư cuối kỳ</td><td /><td /><td /><td className="num">{sides(l.closingBalance)[0]}</td><td className="num">{sides(l.closingBalance)[1]}</td></tr>}
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}


/** Mẫu số S07-DN — Sổ quỹ tiền mặt: receipts and payments of a cash account with the running cash balance. */
export function CashBookPrint({ company, address, ledger: l, from, to }: { company: string; address: string; ledger: LedgerData; from: string; to: string }) {
  return (
    <PrintSheet>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S07-DN" />
      <h1>SỔ QUỸ TIỀN MẶT</h1>
      <div className="center"><b>Loại quỹ: {l.accountCode} – {l.accountName}</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th rowSpan={2}>Ngày, tháng chứng từ</th><th colSpan={2}>Số hiệu chứng từ</th><th rowSpan={2}>Diễn giải</th>
            <th colSpan={3}>Số tiền</th><th rowSpan={2}>Ghi chú</th></tr>
          <tr><th>Thu</th><th>Chi</th><th>Thu</th><th>Chi</th><th>Tồn</th></tr>
          {letters(['A', 'B', '', '', 'E', '1', '2', '3', 'G'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td /><td>Số tồn quỹ đầu kỳ</td><td /><td /><td className="num">{formatMoney(l.openingBalance)}</td><td /></tr>
          {l.rows.map((r, i) => {
            const receipt = r.debit !== '0'
            return <tr key={i}><td>{dmy(r.postingDate)}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{receipt ? r.voucherNumber : ''}</td><td>{receipt ? '' : r.voucherNumber}</td>
              <td>{r.description}</td><td className="num">{m(r.debit)}</td><td className="num">{m(r.credit)}</td><td className="num">{formatMoney(r.runningBalance)}</td><td>{r.counterAccounts ? `TK ${r.counterAccounts}` : ''}</td></tr>
          })}
          <tr><td /><td /><td /><td /><td><b>Cộng phát sinh</b></td><td className="num"><b>{formatMoney(l.totalDebit)}</b></td><td className="num"><b>{formatMoney(l.totalCredit)}</b></td><td /><td /></tr>
          <tr><td /><td /><td /><td /><td><b>Số tồn quỹ cuối kỳ</b></td><td /><td /><td className="num"><b>{formatMoney(l.closingBalance)}</b></td><td /></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}
/**
 * Mẫu số S38-DN — Sổ chi tiết các tài khoản, or S31-DN — Sổ chi tiết thanh toán với người mua (người bán) for 131 / 331 (with the
 * "Thời hạn được chiết khấu" column). Both forms are landscape; the balance after each line is shown in Nợ / Có.
 */
export function DetailLedgerPrint({ company, address, ledger: l, from, to, partner, form }:
  { company: string; address: string; ledger: LedgerData; from: string; to: string; partner: string | null; form: 'S38' | 'S31' }) {
  const s31 = form === 'S31'
  const [od, oc] = sides(l.openingBalance), [cd, cc] = sides(l.closingBalance)
  const blank = s31 ? <td /> : null
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo={s31 ? 'S31-DN' : 'S38-DN'} />
      <h1>{s31 ? 'SỔ CHI TIẾT THANH TOÁN VỚI NGƯỜI MUA (NGƯỜI BÁN)' : 'SỔ CHI TIẾT CÁC TÀI KHOẢN'}</h1>
      <div className="center"><b>{s31 ? '(Dùng cho TK: 131, 331)' : '(Dùng cho các TK: 136, 138, 141, 157, 171, 221, 222, 242, 244, 333, 334, 335, 336, 337, 338, 344, 352, 353, 356, 411, 421,...)'}<br />
        Tài khoản: {l.accountCode} – {l.accountName}<br />Đối tượng: {partner ?? 'tất cả'}<br /><i>Loại tiền: VNĐ</i></b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>TK đối ứng</th>{s31 && <th rowSpan={2}>Thời hạn được chiết khấu</th>}
            <th colSpan={2}>Số phát sinh</th><th colSpan={2}>Số dư</th></tr>
          <tr><th>Số hiệu</th><th>Ngày, tháng</th><th>Nợ</th><th>Có</th><th>Nợ</th><th>Có</th></tr>
          {letters(s31 ? ['A', 'B', 'C', 'D', 'E', '1', '2', '3', '4', '5'] : ['A', 'B', 'C', 'D', 'E', '1', '2', '3', '4'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>- Số dư đầu kỳ</td><td />{blank}<td /><td /><td className="num">{od}</td><td className="num">{oc}</td></tr>
          <tr><td /><td /><td /><td>- Số phát sinh trong kỳ</td><td />{blank}<td /><td /><td /><td /></tr>
          {l.rows.map((r, i) => {
            const [bd, bc] = sides(r.runningBalance)
            return <tr key={i}><td>{dmy(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{r.description}</td><td className="center">{r.counterAccounts}</td>{blank}
              <td className="num">{m(r.debit)}</td><td className="num">{m(r.credit)}</td><td className="num">{bd}</td><td className="num">{bc}</td></tr>
          })}
          <tr><td /><td /><td /><td><b>- Cộng số phát sinh</b></td><td className="center">x</td>{s31 && <td className="center">x</td>}
            <td className="num"><b>{formatMoney(l.totalDebit)}</b></td><td className="num"><b>{formatMoney(l.totalCredit)}</b></td><td className="center">x</td><td className="center">x</td></tr>
          <tr><td /><td /><td /><td><b>- Số dư cuối kỳ</b></td><td className="center">x</td>{s31 && <td className="center">x</td>}
            <td className="center">x</td><td className="center">x</td><td className="num"><b>{cd}</b></td><td className="num"><b>{cc}</b></td></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}