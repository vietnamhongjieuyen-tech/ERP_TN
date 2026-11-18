import { PrintSheet } from './common'
import { BookFoot, dmy, FormHeader } from './forms'
import { addMoney, divideMoney, formatMoney } from './money'

// Accounting books printed on the reference forms of TT 99/2025/TT-BTC Phụ lục III (hình thức Nhật ký chung).
// Layouts copied from Công báo 1575+1576: S03a-DN Sổ Nhật ký chung, S03b-DN Sổ Cái, S07-DN Sổ quỹ tiền mặt, S08-DN Sổ tiền gửi,
// S31-DN / S38-DN Sổ chi tiết.

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

/** Mẫu số S08-DN — Sổ tiền gửi không kỳ hạn: one bank account, deposits / withdrawals and the balance after each line. */
export function BankBookPrint({ company, address, ledger: l, from, to, bankName, bankAccountNo }:
  { company: string; address: string; ledger: LedgerData; from: string; to: string; bankName?: string | null; bankAccountNo?: string | null }) {
  return (
    <PrintSheet>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S08-DN" />
      <h1>SỔ TIỀN GỬI KHÔNG KỲ HẠN</h1>
      <div className="center">Nơi mở tài khoản giao dịch: {bankName || '................'}<br />Số hiệu tài khoản tại nơi gửi: {bankAccountNo || '.............'}<br />
        <b>Tài khoản: {l.accountCode} – {l.accountName}</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>Tài khoản đối ứng</th>
            <th colSpan={3}>Số tiền</th><th rowSpan={2}>Ghi chú</th></tr>
          <tr><th>Số hiệu</th><th>Ngày, tháng</th><th>Thu (gửi vào)</th><th>Chi (rút ra)</th><th>Còn lại</th></tr>
          {letters(['A', 'B', 'C', 'D', 'E', '1', '2', '3', 'F'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>- Số dư đầu kỳ</td><td /><td /><td /><td className="num">{formatMoney(l.openingBalance)}</td><td /></tr>
          <tr><td /><td /><td /><td>- Số phát sinh trong kỳ</td><td /><td /><td /><td /><td /></tr>
          {l.rows.map((r, i) => (
            <tr key={i}><td>{dmy(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{r.description}</td><td className="center">{r.counterAccounts}</td>
              <td className="num">{m(r.debit)}</td><td className="num">{m(r.credit)}</td><td className="num">{formatMoney(r.runningBalance)}</td><td /></tr>))}
          <tr><td /><td /><td /><td><b>- Cộng số phát sinh trong kỳ</b></td><td className="center">x</td><td className="num"><b>{formatMoney(l.totalDebit)}</b></td>
            <td className="num"><b>{formatMoney(l.totalCredit)}</b></td><td className="center">x</td><td className="center">x</td></tr>
          <tr><td /><td /><td /><td><b>- Số dư cuối kỳ</b></td><td className="center">x</td><td className="center">x</td><td className="center">x</td>
            <td className="num"><b>{formatMoney(l.closingBalance)}</b></td><td className="center">x</td></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

/** One row of the stock card API (StockService.StockCardAsync): quantities, value (null = not yet costed) and balances after the row. */
export interface StockCardLine {
  date: string; documentDate?: string | null; documentNumber: string; description: string | null; counterAccount?: string | null
  inQty: string; outQty: string; value: string | null; balanceQty: string; balanceValue: string | null
}
export interface StockBookInfo { company: string; address: string; warehouse: string; itemCode: string; itemName: string; unit: string; account?: string | null; from: string; to: string }

const neg = (v: string) => (v.startsWith('-') ? v.slice(1) : '-' + v)
/** Opening quantity / value of the period, worked back from the first row (balance after it minus its own movement). */
function openingOf(rows: StockCardLine[]): { qty: string; value: string | null } | null {
  const r = rows[0]
  if (!r) return null
  const qty = addMoney([r.balanceQty, neg(r.inQty), r.outQty])
  if (r.balanceValue == null || r.value == null) return { qty, value: null }
  const signed = r.inQty !== '0' || (r.outQty === '0') ? r.value : neg(r.value)   // receipts and value adjustments add, issues take away
  return { qty, value: addMoney([r.balanceValue, neg(signed)]) }
}

/** Mẫu số S12-DN — Thẻ kho (Sổ kho): quantities only, one item in one warehouse. */
export function StockCardPrint({ info, rows }: { info: StockBookInfo; rows: StockCardLine[] }) {
  const moves = rows.filter(r => r.inQty !== '0' || r.outQty !== '0')   // value adjustments do not change quantities
  const closing = rows.at(-1)?.balanceQty ?? openingOf(rows)?.qty ?? '0'
  return (
    <PrintSheet>
      <FormHeader company={info.company} second={<>Địa chỉ: {info.address || '......................'}</>} formNo="S12-DN" />
      <h1>THẺ KHO (SỔ KHO)</h1>
      <div className="center">Ngày lập thẻ: {dmy(info.to)}<br />Tờ số: ..........</div>
      <p>- Tên, nhãn hiệu, quy cách vật tư: {info.itemName} (kho {info.warehouse})<br />- Đơn vị tính: {info.unit}<br />- Mã số: {info.itemCode}</p>
      {period(info.from, info.to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Số TT</th><th rowSpan={2}>Ngày, tháng</th><th colSpan={2}>Số hiệu chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>Ngày nhập, xuất</th>
            <th colSpan={3}>Số lượng</th><th rowSpan={2}>Ký xác nhận của kế toán</th></tr>
          <tr><th>Nhập</th><th>Xuất</th><th>Nhập</th><th>Xuất</th><th>Tồn</th></tr>
          {letters(['A', 'B', 'C', 'D', 'E', 'F', '1', '2', '3', 'G'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td /><td>Tồn đầu kỳ</td><td /><td /><td /><td className="num">{formatMoney(openingOf(rows)?.qty ?? closing)}</td><td /></tr>
          {moves.map((r, i) => {
            const receipt = r.inQty !== '0'
            return <tr key={i}><td className="center">{i + 1}</td><td>{dmy(r.documentDate ?? r.date)}</td><td>{receipt ? r.documentNumber : ''}</td><td>{receipt ? '' : r.documentNumber}</td>
              <td>{r.description}</td><td>{dmy(r.date)}</td><td className="num">{m(r.inQty)}</td><td className="num">{m(r.outQty)}</td><td className="num">{formatMoney(r.balanceQty)}</td><td /></tr>
          })}
          <tr><td /><td /><td /><td /><td><b>Cộng cuối kỳ</b></td><td className="center">x</td><td className="num"><b>{formatMoney(addMoney(moves.map(r => r.inQty)))}</b></td>
            <td className="num"><b>{formatMoney(addMoney(moves.map(r => r.outQty)))}</b></td><td className="num"><b>{formatMoney(closing)}</b></td><td className="center">x</td></tr>
        </tbody>
      </table>
      <BookFoot date={info.to} />
    </PrintSheet>
  )
}

/** Mẫu số S10-DN — Sổ chi tiết vật liệu, dụng cụ (sản phẩm, hàng hoá): quantity and value in / out / balance, totals per month. */
export function MaterialLedgerPrint({ info, rows }: { info: StockBookInfo; rows: StockCardLine[] }) {
  const open = openingOf(rows)
  const groups: { key: string; rows: StockCardLine[] }[] = []
  for (const r of rows) {
    const key = r.date.slice(0, 7)
    if (groups.at(-1)?.key !== key) groups.push({ key, rows: [] })
    groups.at(-1)!.rows.push(r)
  }
  const price = (r: StockCardLine) => {
    const q = r.inQty !== '0' ? r.inQty : r.outQty
    return r.value == null || q === '0' ? '' : formatMoney(divideMoney(r.value, q) ?? '')
  }
  const sum = (xs: (string | null)[]) => (xs.some(x => x == null) ? null : addMoney(xs as string[]))
  return (
    <PrintSheet landscape>
      <FormHeader company={info.company} second={<>Địa chỉ: {info.address || '......................'}</>} formNo="S10-DN" />
      <h1>SỔ CHI TIẾT VẬT LIỆU, DỤNG CỤ (SẢN PHẨM, HÀNG HOÁ)</h1>
      <div className="center"><b>Năm {year(info.from, info.to)}<br />Tài khoản: {info.account ?? '............'} &nbsp; Tên kho: {info.warehouse}</b><br />
        Tên, quy cách nguyên liệu, vật liệu, công cụ, dụng cụ (sản phẩm, hàng hoá): {info.itemCode} – {info.itemName}</div>
      {period(info.from, info.to)}
      <div style={{ textAlign: 'right' }}><i>Đơn vị tính: {info.unit}; thành tiền: đồng</i></div>
      <table>
        <thead>
          <tr><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>Tài khoản đối ứng</th><th rowSpan={2}>Đơn giá</th>
            <th colSpan={2}>Nhập</th><th colSpan={2}>Xuất</th><th colSpan={2}>Tồn</th><th rowSpan={2}>Ghi chú</th></tr>
          <tr><th>Số hiệu</th><th>Ngày, tháng</th><th>Số lượng</th><th>Thành tiền</th><th>Số lượng</th><th>Thành tiền</th><th>Số lượng</th><th>Thành tiền</th></tr>
          {letters(['A', 'B', 'C', 'D', '1', '2', '3 = 1x2', '4', '5 = (1x4)', '6', '7 = (1x6)', '8'])}
        </thead>
        <tbody>
          <tr><td /><td /><td>Số dư đầu kỳ</td><td /><td /><td /><td /><td /><td /><td className="num">{open ? formatMoney(open.qty) : ''}</td>
            <td className="num">{open?.value == null ? '' : formatMoney(open.value)}</td><td /></tr>
          {groups.map(g => [
            ...g.rows.map((r, i) => {
              const adjust = r.inQty === '0' && r.outQty === '0'
              const inValue = adjust || r.inQty !== '0' ? r.value : null, outValue = r.outQty !== '0' ? r.value : null
              return <tr key={g.key + i}><td>{r.documentNumber}</td><td>{dmy(r.documentDate ?? r.date)}</td><td>{r.description}</td><td className="center">{r.counterAccount ?? ''}</td>
                <td className="num">{adjust ? '' : price(r)}</td><td className="num">{m(r.inQty)}</td><td className="num">{inValue == null ? '' : formatMoney(inValue)}</td>
                <td className="num">{m(r.outQty)}</td><td className="num">{outValue == null ? '' : formatMoney(outValue)}</td>
                <td className="num">{formatMoney(r.balanceQty)}</td><td className="num">{r.balanceValue == null ? '' : formatMoney(r.balanceValue)}</td>
                <td>{adjust ? 'Điều chỉnh giá trị' : r.value == null ? 'Chờ tính giá' : ''}</td></tr>
            }),
            <tr key={g.key + 'm'}><td /><td /><td><b>Cộng tháng {g.key.slice(5, 7)}/{g.key.slice(0, 4)}</b></td><td className="center">x</td><td className="center">x</td>
              <td className="num"><b>{formatMoney(addMoney(g.rows.map(r => r.inQty)))}</b></td>
              <td className="num"><b>{formatMoney(sum(g.rows.filter(r => r.outQty === '0').map(r => r.value)) ?? '')}</b></td>
              <td className="num"><b>{formatMoney(addMoney(g.rows.map(r => r.outQty)))}</b></td>
              <td className="num"><b>{formatMoney(sum(g.rows.filter(r => r.outQty !== '0').map(r => r.value)) ?? '')}</b></td>
              <td className="num"><b>{formatMoney(g.rows.at(-1)!.balanceQty)}</b></td><td className="num"><b>{formatMoney(g.rows.at(-1)!.balanceValue ?? '')}</b></td><td /></tr>,
          ])}
        </tbody>
      </table>
      <BookFoot date={info.to} />
    </PrintSheet>
  )
}

export interface StockSummaryLine { warehouseCode: string; itemCode: string; itemName: string; openingValue: string; inValue: string; outValue: string; closingValue: string }

/** Mẫu số S11-DN — Bảng tổng hợp chi tiết vật liệu, dụng cụ, sản phẩm, hàng hoá: values only, one table per inventory account. */
export function StockSummaryPrint({ company, address, groups, from, to }:
  { company: string; address: string; groups: { account: string; rows: StockSummaryLine[] }[]; from: string; to: string }) {
  const total = (rows: StockSummaryLine[], k: 'openingValue' | 'inValue' | 'outValue' | 'closingValue') => formatMoney(addMoney(rows.map(r => r[k])))
  const sameMonth = from.slice(0, 7) === to.slice(0, 7)
  return (
    <PrintSheet>
      {groups.map((g, gi) => (
        <div key={g.account} style={gi > 0 ? { breakBefore: 'page' } : undefined}>
          <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S11-DN" />
          <h1>BẢNG TỔNG HỢP CHI TIẾT<br />VẬT LIỆU, DỤNG CỤ, SẢN PHẨM, HÀNG HOÁ</h1>
          <div className="center"><b>Tài khoản: {g.account}<br />{sameMonth ? `Tháng ${from.slice(5, 7)} năm ${from.slice(0, 4)}` : `Từ ngày ${dmy(from)} đến ngày ${dmy(to)}`}</b></div>
          <table>
            <thead>
              <tr><th rowSpan={2}>STT</th><th rowSpan={2}>Tên, qui cách vật liệu, dụng cụ, sản phẩm hàng hoá</th><th colSpan={4}>Số tiền</th></tr>
              <tr><th>Tồn đầu kỳ</th><th>Nhập trong kỳ</th><th>Xuất trong kỳ</th><th>Tồn cuối kỳ</th></tr>
              {letters(['A', 'B', '1', '2', '3', '4'])}
            </thead>
            <tbody>
              {g.rows.map((r, i) => <tr key={r.warehouseCode + r.itemCode}><td className="center">{i + 1}</td><td>{r.itemCode} – {r.itemName} (kho {r.warehouseCode})</td>
                <td className="num">{m(r.openingValue)}</td><td className="num">{m(r.inValue)}</td><td className="num">{m(r.outValue)}</td><td className="num">{m(r.closingValue)}</td></tr>)}
              <tr><td /><td className="center"><b>Cộng</b></td><td className="num"><b>{total(g.rows, 'openingValue')}</b></td><td className="num"><b>{total(g.rows, 'inValue')}</b></td>
                <td className="num"><b>{total(g.rows, 'outValue')}</b></td><td className="num"><b>{total(g.rows, 'closingValue')}</b></td></tr>
            </tbody>
          </table>
          <BookFoot date={to} />
        </div>
      ))}
    </PrintSheet>
  )
}

export interface SalesBookRow { postingDate: string; number: string; invoiceDate: string; invoiceRef: string; description: string; counterAccount: string | null
  quantity: string | null; unitPrice: string | null; revenue: string; tax: string; deduction: string }
export interface SalesBookData { itemCode: string; itemName: string; unit: string; rows: SalesBookRow[]; totalQuantity: string; totalRevenue: string; totalTax: string
  totalDeduction: string; netRevenue: string; costOfSales: string; costComplete: boolean; grossProfit: string }

/** Mẫu số S35-DN — Sổ chi tiết bán hàng of one product: revenue lines, deductions, then net revenue, cost of sales and gross profit. */
export function SalesBookPrint({ company, address, book: b, from, to }: { company: string; address: string; book: SalesBookData; from: string; to: string }) {
  const blank = <><td /><td /><td /></>
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S35-DN" />
      <h1>SỔ CHI TIẾT BÁN HÀNG</h1>
      <div className="center"><b>Tên sản phẩm (hàng hoá, dịch vụ, bất động sản đầu tư): {b.itemCode} – {b.itemName} ({b.unit})<br />Năm: {year(from, to)}<br />Quyển số: ..........</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>TK đối ứng</th><th colSpan={3}>Doanh thu</th><th colSpan={2}>Các khoản tính trừ</th></tr>
          <tr><th>Số hiệu</th><th>Ngày, tháng</th><th>Số lượng</th><th>Đơn giá</th><th>Thành tiền</th><th>Thuế</th><th>Các khoản chiết khấu thương mại, giảm giá hàng bán, hàng bán bị trả lại</th></tr>
          {letters(['A', 'B', 'C', 'D', 'E', '1', '2', '3', '4', '5'])}
        </thead>
        <tbody>
          {b.rows.map((r, i) => (
            <tr key={i}><td>{dmy(r.postingDate)}</td><td>{r.number}<br /><small>{r.invoiceRef}</small></td><td>{dmy(r.invoiceDate)}</td><td>{r.description}</td><td className="center">{r.counterAccount ?? ''}</td>
              <td className="num">{r.quantity == null ? '' : formatMoney(r.quantity)}</td><td className="num">{r.unitPrice == null ? '' : formatMoney(r.unitPrice)}</td>
              <td className="num">{m(r.revenue)}</td><td className="num">{m(r.tax)}</td><td className="num">{m(r.deduction)}</td></tr>))}
          <tr><td /><td /><td /><td><b>Cộng số phát sinh</b></td><td /><td className="num"><b>{formatMoney(b.totalQuantity)}</b></td><td />
            <td className="num"><b>{formatMoney(b.totalRevenue)}</b></td><td className="num"><b>{m(b.totalTax)}</b></td><td className="num"><b>{m(b.totalDeduction)}</b></td></tr>
          <tr><td /><td /><td /><td>- Doanh thu thuần</td><td />{blank}<td className="num">{formatMoney(b.netRevenue)}</td><td /></tr>
          <tr><td /><td /><td /><td>- Giá vốn hàng bán{b.costComplete ? '' : ' (còn phiếu xuất chờ tính giá)'}</td><td />{blank}<td className="num">{formatMoney(b.costOfSales)}</td><td /></tr>
          <tr><td /><td /><td /><td>- Lãi gộp</td><td />{blank}<td className="num">{formatMoney(b.grossProfit)}</td><td /></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

export interface FixedAssetLine {
  code: string; name: string; assetAccountCode: string; inServiceDate: string; usefulLifeMonths: number; originalCost: string; residualValue?: string; accumulated: string
  sourceVoucher?: string | null; sourceDate?: string | null; disposalDate: string | null; disposalVoucher?: string | null; disposalReason?: string | null
}
const monthYear = (d: string) => `${d.slice(5, 7)}/${d.slice(0, 4)}`
/** Straight line over the useful life: yearly rate (%) and yearly amount on the depreciable base. */
export function yearlyDepreciation(a: Pick<FixedAssetLine, 'usefulLifeMonths' | 'originalCost' | 'residualValue'>) {
  if (a.usefulLifeMonths <= 0) return { rate: '', amount: '' }
  const base = addMoney([a.originalCost, neg(a.residualValue ?? '0')])
  return { rate: divideMoney('1200', String(a.usefulLifeMonths)) ?? '', amount: divideMoney(addMoney(Array(12).fill(base)), String(a.usefulLifeMonths), 0) ?? '' }
}

/** Mẫu số S21-DN — Sổ tài sản cố định: one table per asset account (loại tài sản), increase, depreciation and decrease columns. */
export function FixedAssetBookPrint({ company, address, assets, year: y }: { company: string; address: string; assets: FixedAssetLine[]; year: string }) {
  const groups = [...new Set(assets.map(a => a.assetAccountCode))].sort().map(k => ({ account: k, rows: assets.filter(a => a.assetAccountCode === k) }))
  return (
    <PrintSheet landscape>
      {groups.map((g, gi) => (
        <div key={g.account} style={gi > 0 ? { breakBefore: 'page' } : undefined}>
          <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S21-DN" />
          <h1>SỔ TÀI SẢN CỐ ĐỊNH</h1>
          <div className="center">Năm: {y}<br /><b>Loại tài sản: TK {g.account}</b></div>
          <table>
            <thead>
              <tr><th rowSpan={3}>Số TT</th><th colSpan={7}>Ghi tăng TSCĐ</th><th colSpan={3}>Khấu hao TSCĐ</th><th colSpan={3}>Ghi giảm TSCĐ</th></tr>
              <tr><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Tên, đặc điểm, ký hiệu TSCĐ</th><th rowSpan={2}>Nước sản xuất</th><th rowSpan={2}>Tháng năm đưa vào sử dụng</th>
                <th rowSpan={2}>Số hiệu TSCĐ</th><th rowSpan={2}>Nguyên giá TSCĐ</th><th colSpan={2}>Khấu hao</th><th rowSpan={2}>Khấu hao đã tính đến khi ghi giảm TSCĐ</th>
                <th colSpan={2}>Chứng từ</th><th rowSpan={2}>Lý do giảm TSCĐ</th></tr>
              <tr><th>Số hiệu</th><th>Ngày tháng</th><th>Tỷ lệ (%) khấu hao</th><th>Mức khấu hao</th><th>Số hiệu</th><th>Ngày, tháng, năm</th></tr>
              {letters(['A', 'B', 'C', 'D', 'E', 'G', 'H', '1', '2', '3', '4', 'I', 'K', 'L'])}
            </thead>
            <tbody>
              {g.rows.map((a, i) => {
                const dep = yearlyDepreciation(a)
                return <tr key={a.code}><td className="center">{i + 1}</td><td>{a.sourceVoucher ?? ''}</td><td>{a.sourceDate ? dmy(a.sourceDate) : ''}</td><td>{a.name}</td><td />
                  <td className="center">{monthYear(a.inServiceDate)}</td><td>{a.code}</td><td className="num">{formatMoney(a.originalCost)}</td>
                  <td className="num">{dep.rate ? formatMoney(dep.rate) : ''}</td><td className="num">{dep.amount ? formatMoney(dep.amount) : ''}</td>
                  <td className="num">{a.disposalDate ? formatMoney(a.accumulated) : ''}</td><td>{a.disposalVoucher ?? ''}</td><td>{a.disposalDate ? dmy(a.disposalDate) : ''}</td><td>{a.disposalReason ?? ''}</td></tr>
              })}
              <tr><td /><td /><td /><td className="center"><b>Cộng</b></td><td className="center">x</td><td className="center">x</td><td className="center">x</td>
                <td className="num"><b>{formatMoney(addMoney(g.rows.map(a => a.originalCost)))}</b></td><td />
                <td className="num"><b>{formatMoney(addMoney(g.rows.map(a => yearlyDepreciation(a).amount || '0')))}</b></td>
                <td className="num"><b>{m(addMoney(g.rows.filter(a => a.disposalDate).map(a => a.accumulated)))}</b></td><td className="center">x</td><td className="center">x</td><td className="center">x</td></tr>
            </tbody>
          </table>
          <BookFoot date={`${y}-12-31`} />
        </div>
      ))}
    </PrintSheet>
  )
}

export interface FixedAssetCard {
  asset: FixedAssetLine; openingAccumulated: string
  sources: { voucherNumber: string; postingDate: string; accountCode: string; amount: string }[]
  /** Depreciation booked per month (Đã ghi sổ of the schedule). */
  booked: { periodEnd: string; amount: string }[]
}

/** Mẫu số S23-DN — Thẻ tài sản cố định: cost lines, depreciation per year with the running total, decrease. */
export function FixedAssetCardPrint({ company, address, card: c, date }: { company: string; address: string; card: FixedAssetCard; date: string }) {
  const years: { year: string; amount: string; cumulative: string }[] = []
  let total = c.openingAccumulated
  for (const b of c.booked) {
    const y = b.periodEnd.slice(0, 4)
    if (years.at(-1)?.year !== y) years.push({ year: y, amount: '0', cumulative: total })
    const row = years.at(-1)!
    row.amount = addMoney([row.amount, b.amount]); total = addMoney([total, b.amount]); row.cumulative = total
  }
  const lines = Math.max(c.sources.length, years.length + (c.openingAccumulated !== '0' ? 1 : 0))
  const depRows = [...(c.openingAccumulated !== '0' ? [{ year: 'Đầu kỳ', amount: c.openingAccumulated, cumulative: c.openingAccumulated }] : []), ...years]
  const a = c.asset
  return (
    <PrintSheet>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S23-DN" />
      <h1>THẺ TÀI SẢN CỐ ĐỊNH</h1>
      <div className="center"><i>Số: {a.code}<br />Ngày {dmy(date)} lập thẻ</i></div>
      <p>Căn cứ vào Biên bản giao nhận TSCĐ số ............ ngày ..../..../........<br />
        Tên, ký mã hiệu, quy cách (cấp hạng) TSCĐ: {a.name} &nbsp; Số hiệu TSCĐ: {a.code}<br />
        Nước sản xuất (xây dựng): ................ Năm sản xuất: ........<br />
        Bộ phận quản lý, sử dụng: ................ Năm đưa vào sử dụng: {a.inServiceDate.slice(0, 4)}<br />
        Công suất (diện tích thiết kế): ................<br />
        Đình chỉ sử dụng TSCĐ ngày {a.disposalDate ? dmy(a.disposalDate) : '..../..../........'}<br />Lý do đình chỉ: {a.disposalReason ?? '................'}</p>
      <table>
        <thead>
          <tr><th rowSpan={2}>Số hiệu chứng từ</th><th colSpan={3}>Nguyên giá tài sản cố định</th><th colSpan={3}>Giá trị hao mòn tài sản cố định</th></tr>
          <tr><th>Ngày, tháng, năm</th><th>Diễn giải</th><th>Nguyên giá</th><th>Năm</th><th>Giá trị hao mòn</th><th>Cộng dồn</th></tr>
          {letters(['A', 'B', 'C', '1', '2', '3', '4'])}
        </thead>
        <tbody>
          {Array.from({ length: lines }, (_, i) => {
            const s = c.sources[i], d = depRows[i]
            return <tr key={i}><td>{s?.voucherNumber ?? ''}</td><td>{s ? dmy(s.postingDate) : ''}</td><td>{s ? `Nguyên giá (TK ${s.accountCode})` : ''}</td><td className="num">{s ? formatMoney(s.amount) : ''}</td>
              <td className="center">{d?.year ?? ''}</td><td className="num">{d ? formatMoney(d.amount) : ''}</td><td className="num">{d ? formatMoney(d.cumulative) : ''}</td></tr>
          })}
        </tbody>
      </table>
      <h2 className="center" style={{ fontSize: 'inherit' }}>Dụng cụ phụ tùng kèm theo</h2>
      <table>
        <thead><tr><th>Số TT</th><th>Tên, quy cách dụng cụ, phụ tùng</th><th>Đơn vị tính</th><th>Số lượng</th><th>Giá trị</th></tr>{letters(['A', 'B', 'C', '1', '2'])}</thead>
        <tbody><tr><td>&nbsp;</td><td /><td /><td /><td /></tr></tbody>
      </table>
      <p>Ghi giảm TSCĐ chứng từ số: {a.disposalVoucher ?? '................'} ngày {a.disposalDate ? dmy(a.disposalDate) : '..../..../........'}<br />Lý do giảm: {a.disposalReason ?? '................'}</p>
      <BookFoot date={date} />
    </PrintSheet>
  )
}
