import { PrintSheet } from './common'
import { BookFoot, dmy, FormHeader } from './forms'
import { addMoney, divideMoney, formatMoney } from './money'

// Accounting books printed on the reference forms of TT 99/2025/TT-BTC Phụ lục III (hình thức Nhật ký chung).
// Layouts copied from Công báo 1575+1576: S03a-DN Sổ Nhật ký chung, S03b-DN Sổ Cái, S07-DN Sổ quỹ tiền mặt, S08-DN Sổ tiền gửi,
// S31-DN / S38-DN Sổ chi tiết.

export interface JournalLine { postingDate: string; voucherDate?: string | null; voucherNumber: string; lineNo: number; accountCode: string; debit: string; credit: string; description: string | null }
export interface LedgerLine { postingDate: string; voucherDate?: string | null; voucherNumber: string; voucherType: string; description: string | null; counterAccounts: string; debit: string; credit: string; runningBalance: string
  foreignCurrency?: string | null; foreignAmount?: string | null }
export interface LedgerData { accountCode: string; accountName: string; openingBalance: string; rows: LedgerLine[]; totalDebit: string; totalCredit: string; closingBalance: string
  foreignClosing?: { currency: string; closing: string }[] }

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
  usedBy?: string | null
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

export interface TrialBalanceLine { accountCode: string; accountName: string; isGroup?: boolean; level?: number
  openingDebit: string; openingCredit: string; periodDebit: string; periodCredit: string; closingDebit: string; closingCredit: string }

/** Mẫu số S06-DN — Bảng cân đối số phát sinh (summary accounts in bold, never in the totals). */
export function TrialBalancePrint({ company, address, lines, totals, from, to }:
  { company: string; address: string; lines: TrialBalanceLine[]; totals: Record<string, string>; from: string; to: string }) {
  const month = from.slice(0, 7) === to.slice(0, 7) && from.endsWith('-01')
  const word = month ? 'tháng' : 'kỳ'
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S06-DN" />
      <h1>BẢNG CÂN ĐỐI SỐ PHÁT SINH</h1>
      <div className="center">{month ? `Tháng ${from.slice(5, 7)} năm ${from.slice(0, 4)}` : `Từ ngày ${dmy(from)} đến ngày ${dmy(to)}`}</div>
      <table>
        <thead>
          <tr><th rowSpan={2}>Số hiệu tài khoản</th><th rowSpan={2}>Tên tài khoản kế toán</th><th colSpan={2}>Số dư đầu {word}</th><th colSpan={2}>Số phát sinh trong {word}</th><th colSpan={2}>Số dư cuối {word}</th></tr>
          <tr><th>Nợ</th><th>Có</th><th>Nợ</th><th>Có</th><th>Nợ</th><th>Có</th></tr>
          {letters(['A', 'B', '1', '2', '3', '4', '5', '6'])}
        </thead>
        <tbody>
          {lines.map(r => {
            const cells = [r.openingDebit, r.openingCredit, r.periodDebit, r.periodCredit, r.closingDebit, r.closingCredit].map((v, i) => <td key={i} className="num">{m(v)}</td>)
            return <tr key={r.accountCode + (r.isGroup ? 'g' : '')} style={r.isGroup ? { fontWeight: 600 } : undefined}><td>{r.accountCode}</td>
              <td style={{ paddingLeft: 4 + 10 * (r.level ?? 0) }}>{r.accountName}</td>{cells}</tr>
          })}
          <tr><td /><td className="center"><b>Tổng cộng</b></td>
            {['totalOpeningDebit', 'totalOpeningCredit', 'totalPeriodDebit', 'totalPeriodCredit', 'totalClosingDebit', 'totalClosingCredit'].map(k => <td key={k} className="num"><b>{formatMoney(totals[k] ?? '0')}</b></td>)}</tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

export type SpecialJournal = 'S03a1' | 'S03a2' | 'S03a3' | 'S03a4'
export interface SpecialJournalLine extends JournalLine { voucherId: string }
const SPECIAL: Record<SpecialJournal, { title: string; focusSide: 'debit' | 'credit'; focusHead: string; countersHead: string; focusFirst: boolean }> = {
  S03a1: { title: 'SỔ NHẬT KÝ THU TIỀN', focusSide: 'debit', focusHead: 'Ghi Nợ TK', countersHead: 'Ghi Có các TK', focusFirst: true },
  S03a2: { title: 'SỔ NHẬT KÝ CHI TIỀN', focusSide: 'credit', focusHead: 'Ghi Có TK', countersHead: 'Ghi Nợ các TK', focusFirst: true },
  S03a3: { title: 'SỔ NHẬT KÝ MUA HÀNG', focusSide: 'credit', focusHead: 'Phải trả người bán (ghi Có)', countersHead: 'Tài khoản ghi Nợ', focusFirst: false },
  S03a4: { title: 'SỔ NHẬT KÝ BÁN HÀNG', focusSide: 'debit', focusHead: 'Phải thu từ người mua (ghi Nợ)', countersHead: 'Ghi Có tài khoản', focusFirst: true },
}

/**
 * Build the rows of a special journal from the general journal lines: one row per voucher that touches the focus account on the
 * focus side; the opposite-side lines of the voucher go to the counter-account columns (the most used ones get their own column, the
 * rest go to "Tài khoản khác" with their code).
 */
export function specialJournalRows(lines: SpecialJournalLine[], focusAccounts: string[], form: SpecialJournal, columns = 4) {
  const { focusSide } = SPECIAL[form]
  const other = focusSide === 'debit' ? 'credit' : 'debit'
  const byVoucher = new Map<string, SpecialJournalLine[]>()
  for (const l of lines) byVoucher.set(l.voucherId, [...(byVoucher.get(l.voucherId) ?? []), l])
  const rows: { first: SpecialJournalLine; focus: string; counters: Map<string, string> }[] = []
  for (const vl of byVoucher.values()) {
    const focus = vl.filter(l => focusAccounts.includes(l.accountCode) && l[focusSide] !== '0')
    if (!focus.length) continue
    const counters = new Map<string, string>()
    for (const l of vl) if (l[other] !== '0' && !focusAccounts.includes(l.accountCode)) counters.set(l.accountCode, addMoney([counters.get(l.accountCode) ?? '0', l[other]]))
    rows.push({ first: focus[0], focus: addMoney(focus.map(l => l[focusSide])), counters })
  }
  const use = new Map<string, number>()
  for (const r of rows) for (const k of r.counters.keys()) use.set(k, (use.get(k) ?? 0) + 1)
  const fixed = [...use.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1)).slice(0, columns).map(([k]) => k).sort()
  return { rows, fixed }
}

/** Mẫu số S03a1-DN … S03a4-DN — Sổ Nhật ký thu tiền / chi tiền / mua hàng / bán hàng. */
export function SpecialJournalPrint({ company, address, form, lines, focusAccounts, from, to }:
  { company: string; address: string; form: SpecialJournal; lines: SpecialJournalLine[]; focusAccounts: string[]; from: string; to: string }) {
  const s = SPECIAL[form]
  const { rows, fixed } = specialJournalRows(lines, focusAccounts, form)
  const others = (r: (typeof rows)[number]) => [...r.counters.entries()].filter(([k]) => !fixed.includes(k))
  const focusLabel = form === 'S03a1' || form === 'S03a2' ? `${s.focusHead} ${focusAccounts.join(', ')}` : `${s.focusHead}${focusAccounts.length ? ` – TK ${focusAccounts.join(', ')}` : ''}`
  const focusTh = <th rowSpan={2}>{focusLabel}</th>
  const sum = (xs: string[]) => formatMoney(addMoney(xs))
  const focusTotal = sum(rows.map(r => r.focus))
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo={`${form}-DN`} />
      <h1>{s.title}</h1>
      <div className="center"><b>Năm {year(from, to)}</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th>{s.focusFirst && focusTh}
            <th colSpan={fixed.length + 2}>{s.countersHead}</th>{!s.focusFirst && focusTh}</tr>
          <tr><th>Số hiệu</th><th>Ngày tháng</th>{fixed.map(k => <th key={k}>TK {k}</th>)}<th>Tài khoản khác – Số tiền</th><th>Số hiệu</th></tr>
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>Số trang trước chuyển sang</td>{s.focusFirst && <td />}{fixed.map(k => <td key={k} />)}<td /><td />{!s.focusFirst && <td />}</tr>
          {rows.map((r, i) => {
            const rest = others(r)
            const focusTd = <td className="num">{formatMoney(r.focus)}</td>
            return <tr key={i}><td>{dmy(r.first.postingDate)}</td><td>{r.first.voucherNumber}</td><td>{dmy(r.first.voucherDate ?? r.first.postingDate)}</td><td>{r.first.description}</td>
              {s.focusFirst && focusTd}{fixed.map(k => <td key={k} className="num">{m(r.counters.get(k) ?? '0')}</td>)}
              <td className="num">{rest.length ? sum(rest.map(([, v]) => v)) : ''}</td><td className="center">{rest.map(([k]) => k).join(', ')}</td>{!s.focusFirst && focusTd}</tr>
          })}
          <tr><td /><td /><td /><td><b>Cộng chuyển sang trang sau</b></td>{s.focusFirst && <td className="num"><b>{focusTotal}</b></td>}
            {fixed.map(k => <td key={k} className="num"><b>{sum(rows.map(r => r.counters.get(k) ?? '0'))}</b></td>)}
            <td className="num"><b>{sum(rows.flatMap(r => others(r).map(([, v]) => v)))}</b></td><td />{!s.focusFirst && <td className="num"><b>{focusTotal}</b></td>}</tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

/** Mẫu số S07a-DN — Sổ kế toán chi tiết quỹ tiền mặt: like S07 with the counter account and Nợ / Có movement columns. */
export function CashDetailBookPrint({ company, address, ledger: l, from, to }: { company: string; address: string; ledger: LedgerData; from: string; to: string }) {
  return (
    <PrintSheet>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S07a-DN" />
      <h1>SỔ KẾ TOÁN CHI TIẾT QUỸ TIỀN MẶT</h1>
      <div className="center"><b>Tài khoản: {l.accountCode}<br />Loại quỹ: {l.accountName}<br />Năm {year(from, to)}</b></div>
      {period(from, to)}
      <div style={{ textAlign: 'right' }}><i>Đơn vị tính: đồng</i></div>
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th rowSpan={2}>Ngày, tháng chứng từ</th><th colSpan={2}>Số hiệu chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>TK đối ứng</th>
            <th colSpan={2}>Số phát sinh</th><th rowSpan={2}>Số tồn</th><th rowSpan={2}>Ghi chú</th></tr>
          <tr><th>Thu</th><th>Chi</th><th>Nợ</th><th>Có</th></tr>
          {letters(['A', 'B', 'C', 'D', 'E', 'F', '1', '2', '3', 'G'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td /><td>- Số tồn đầu kỳ</td><td /><td /><td /><td className="num">{formatMoney(l.openingBalance)}</td><td /></tr>
          <tr><td /><td /><td /><td /><td>- Số phát sinh trong kỳ</td><td /><td /><td /><td /><td /></tr>
          {l.rows.map((r, i) => {
            const receipt = r.debit !== '0'
            return <tr key={i}><td>{dmy(r.postingDate)}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{receipt ? r.voucherNumber : ''}</td><td>{receipt ? '' : r.voucherNumber}</td>
              <td>{r.description}</td><td className="center">{r.counterAccounts}</td><td className="num">{m(r.debit)}</td><td className="num">{m(r.credit)}</td>
              <td className="num">{formatMoney(r.runningBalance)}</td><td /></tr>
          })}
          <tr><td /><td /><td /><td /><td><b>- Cộng số phát sinh trong kỳ</b></td><td className="center">x</td><td className="num"><b>{formatMoney(l.totalDebit)}</b></td>
            <td className="num"><b>{formatMoney(l.totalCredit)}</b></td><td className="center">x</td><td className="center">x</td></tr>
          <tr><td /><td /><td /><td /><td><b>- Số tồn cuối kỳ</b></td><td className="center">x</td><td className="center">x</td><td className="center">x</td>
            <td className="num"><b>{formatMoney(l.closingBalance)}</b></td><td className="center">x</td></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

export interface LoanInfo { code: string; description: string; lender: string; ratePercent: string | null; maturityDate: string }

/** Mẫu số S34-DN — Sổ chi tiết tiền vay of one loan contract (ledger of the principal account for the lender). */
export function LoanBookPrint({ company, address, ledger: l, loan, from, to }: { company: string; address: string; ledger: LedgerData; loan: LoanInfo; from: string; to: string }) {
  const [od, oc] = sides(l.openingBalance), [cd, cc] = sides(l.closingBalance)
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S34-DN" />
      <h1>SỔ CHI TIẾT TIỀN VAY</h1>
      <div className="center"><b>(Dùng cho TK 341)<br />Tài khoản: {l.accountCode} – {l.accountName}<br />Đối tượng cho vay: {loan.lender}<br />
        Khế ước vay: {loan.description} &nbsp; Số: {loan.code}</b></div>
      <div style={{ textAlign: 'right' }}><b>(Tỷ lệ lãi vay: {loan.ratePercent ? `${formatMoney(loan.ratePercent)}%/năm` : '..........'})</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={2}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Diễn giải</th><th rowSpan={2}>TK đối ứng</th><th rowSpan={2}>Ngày đến hạn thanh toán</th><th colSpan={2}>Số tiền</th></tr>
          <tr><th>Số hiệu</th><th>Ngày, tháng</th><th>Nợ</th><th>Có</th></tr>
          {letters(['A', 'B', 'C', 'D', 'E', 'G', '1', '2'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>- Số dư đầu kỳ</td><td /><td /><td className="num">{od}</td><td className="num">{oc}</td></tr>
          <tr><td /><td /><td /><td>- Số phát sinh trong kỳ</td><td /><td /><td /><td /></tr>
          {l.rows.map((r, i) => (
            <tr key={i}><td>{dmy(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{r.description}</td><td className="center">{r.counterAccounts}</td>
              <td className="center">{r.credit !== '0' ? dmy(loan.maturityDate) : ''}</td><td className="num">{m(r.debit)}</td><td className="num">{m(r.credit)}</td></tr>))}
          <tr><td /><td /><td /><td><b>- Cộng số phát sinh</b></td><td className="center">x</td><td className="center">x</td>
            <td className="num"><b>{formatMoney(l.totalDebit)}</b></td><td className="num"><b>{formatMoney(l.totalCredit)}</b></td></tr>
          <tr><td /><td /><td /><td><b>- Số dư cuối kỳ</b></td><td className="center">x</td><td className="center">x</td><td className="num"><b>{cd}</b></td><td className="num"><b>{cc}</b></td></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

/** The currency a foreign-currency ledger is kept in: the first one on the rows, else the one of the closing balance. */
export const ledgerCurrency = (l: LedgerData) => l.rows.find(r => r.foreignCurrency && r.foreignAmount)?.foreignCurrency ?? l.foreignClosing?.[0]?.currency ?? null

/**
 * Mẫu số S32-DN (131, 331 — with the discount-term column) / S33-DN (other payables / receivables) — sổ theo dõi thanh toán bằng ngoại tệ.
 * Foreign amounts come from the ledger lines; revaluation lines carry no foreign amount and only move the VND columns. The opening
 * foreign balance is the closing one less the period's movements.
 */
export function ForeignLedgerPrint({ company, address, ledger: l, from, to, partner, form }:
  { company: string; address: string; ledger: LedgerData; from: string; to: string; partner: string | null; form: 'S32' | 'S33' }) {
  const s32 = form === 'S32'
  const cur = ledgerCurrency(l) ?? ''
  const rows = l.rows.map(r => ({ ...r, fx: r.foreignCurrency === cur && r.foreignAmount ? r.foreignAmount : '0' }))
  const signedFx = (r: (typeof rows)[number]) => (r.debit !== '0' ? r.fx : neg(r.fx))
  const closingFx = l.foreignClosing?.find(f => f.currency === cur)?.closing ?? addMoney(rows.map(signedFx))
  const openingFx = addMoney([closingFx, ...rows.map(r => neg(signedFx(r)))])
  let runFx = openingFx
  const [od, oc] = sides(l.openingBalance), [ofd, ofc] = sides(openingFx), [cd, cc] = sides(l.closingBalance), [cfd, cfc] = sides(closingFx)
  const extra = s32 ? <td /> : null
  const x = <td className="center">x</td>
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo={`${form}-DN`} />
      <h1>{s32 ? 'SỔ CHI TIẾT THANH TOÁN VỚI NGƯỜI MUA (NGƯỜI BÁN) BẰNG NGOẠI TỆ' : 'SỔ THEO DÕI THANH TOÁN BẰNG NGOẠI TỆ'}</h1>
      <div className="center"><b>{s32 ? '(Dùng cho TK: 131, 331)' : '(Dùng cho các TK 136, 138, 141, 244, 334, 336, 338, 344)'}<br />
        Tài khoản: {l.accountCode} – {l.accountName}<br />Đối tượng: {partner ?? 'tất cả'}<br />Loại ngoại tệ: {cur}</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={3}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={3}>Diễn giải</th><th rowSpan={3}>TK đối ứng</th><th rowSpan={3}>Tỷ giá hối đoái</th>
            {s32 && <th rowSpan={3}>Thời hạn được chiết khấu</th>}<th colSpan={4}>Số phát sinh</th><th colSpan={4}>Số dư</th></tr>
          <tr><th rowSpan={2}>Số hiệu</th><th rowSpan={2}>Ngày, tháng</th><th colSpan={2}>Nợ</th><th colSpan={2}>Có</th><th colSpan={2}>Nợ</th><th colSpan={2}>Có</th></tr>
          <tr>{Array.from({ length: 4 }, (_, i) => [<th key={i + 'f'}>Ngoại tệ</th>, <th key={i + 'v'}>Quy ra VNĐ</th>])}</tr>
          {letters(s32 ? ['A', 'B', 'C', 'D', 'E', '1', '2', '3', '4', '5', '6', '7', '8', '9', '10'] : ['A', 'B', 'C', 'D', 'E', '1', '2', '3', '4', '5', '6', '7', '8', '9'])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>- Số dư đầu kỳ</td><td /><td />{extra}<td /><td /><td /><td /><td className="num">{ofd}</td><td className="num">{od}</td><td className="num">{ofc}</td><td className="num">{oc}</td></tr>
          <tr><td /><td /><td /><td>- Số phát sinh</td><td /><td />{extra}<td /><td /><td /><td /><td /><td /><td /><td /></tr>
          {rows.map((r, i) => {
            runFx = addMoney([runFx, signedFx(r)])
            const [bd, bc] = sides(r.runningBalance), [bfd, bfc] = sides(runFx)
            const vnd = r.debit !== '0' ? r.debit : r.credit
            const rate = r.fx !== '0' ? formatMoney(divideMoney(vnd, r.fx) ?? '') : ''
            return <tr key={i}><td>{dmy(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{r.description}</td><td className="center">{r.counterAccounts}</td>
              <td className="num">{rate}</td>{extra}
              <td className="num">{r.debit !== '0' ? m(r.fx) : ''}</td><td className="num">{m(r.debit)}</td><td className="num">{r.credit !== '0' ? m(r.fx) : ''}</td><td className="num">{m(r.credit)}</td>
              <td className="num">{bfd}</td><td className="num">{bd}</td><td className="num">{bfc}</td><td className="num">{bc}</td></tr>
          })}
          <tr><td /><td /><td /><td><b>- Cộng số phát sinh</b></td>{x}{x}{s32 && x}
            <td className="num"><b>{formatMoney(addMoney(rows.filter(r => r.debit !== '0').map(r => r.fx)))}</b></td><td className="num"><b>{formatMoney(l.totalDebit)}</b></td>
            <td className="num"><b>{formatMoney(addMoney(rows.filter(r => r.credit !== '0').map(r => r.fx)))}</b></td><td className="num"><b>{formatMoney(l.totalCredit)}</b></td>{x}{x}{x}{x}</tr>
          <tr><td /><td /><td /><td><b>- Số dư cuối kỳ</b></td>{x}{x}{s32 && x}{x}{x}{x}{x}
            <td className="num"><b>{cfd}</b></td><td className="num"><b>{cd}</b></td><td className="num"><b>{cfc}</b></td><td className="num"><b>{cc}</b></td></tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

/** Debit lines split by counter account: the most used single counter accounts get a column, lines with several or other ones go to "khác". */
function costBreakdown(rows: LedgerLine[], columns: number) {
  const debit = rows.filter(r => r.debit !== '0')
  const single = (r: LedgerLine) => (r.counterAccounts && !r.counterAccounts.includes(',') ? r.counterAccounts.trim() : null)
  const use = new Map<string, number>()
  for (const r of debit) { const k = single(r); if (k) use.set(k, (use.get(k) ?? 0) + 1) }
  const fixed = [...use.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1)).slice(0, columns).map(([k]) => k).sort()
  const column = (r: LedgerLine) => { const k = single(r); return k && fixed.includes(k) ? k : 'khác' }
  const total = (k: string) => addMoney(debit.filter(r => column(r) === k).map(r => r.debit))
  return { debit, fixed, column, total, hasOther: debit.some(r => column(r) === 'khác') }
}

/** Mẫu số S36-DN — Sổ chi phí sản xuất, kinh doanh of one cost account: debits split by counter account, credits listed below. */
export function CostBookPrint({ company, address, ledger: l, from, to }: { company: string; address: string; ledger: LedgerData; from: string; to: string }) {
  const b = costBreakdown(l.rows, 6)
  const cols = [...b.fixed, ...(b.hasOther ? ['khác'] : [])]
  const credits = l.rows.filter(r => r.credit !== '0')
  const creditBy = new Map<string, string>()
  for (const r of credits) creditBy.set(r.counterAccounts || '—', addMoney([creditBy.get(r.counterAccounts || '—') ?? '0', r.credit]))
  const empty = cols.map(c => <td key={c} />)
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S36-DN" />
      <h1>SỔ CHI PHÍ SẢN XUẤT, KINH DOANH</h1>
      <div className="center"><b>(Dùng cho các TK 621, 622, 623, 627, 154, 641, 642, 242, 335, 632)<br />- Tài khoản: {l.accountCode} – {l.accountName}<br />
        - Tên phân xưởng: ................ &nbsp; - Tên sản phẩm, dịch vụ: ................</b></div>
      {period(from, to)}
      <table>
        <thead>
          <tr><th rowSpan={3}>Ngày, tháng ghi sổ</th><th colSpan={2}>Chứng từ</th><th rowSpan={3}>Diễn giải</th><th rowSpan={3}>Tài khoản đối ứng</th><th colSpan={cols.length + 1}>Ghi Nợ Tài khoản {l.accountCode}</th></tr>
          <tr><th rowSpan={2}>Số hiệu</th><th rowSpan={2}>Ngày, tháng</th><th rowSpan={2}>Tổng số tiền</th>{cols.length > 0 && <th colSpan={cols.length}>Chia ra</th>}</tr>
          <tr>{cols.map(c => <th key={c}>{c === 'khác' ? 'TK khác' : `TK ${c}`}</th>)}</tr>
          {letters(['A', 'B', 'C', 'D', 'E', ...Array.from({ length: cols.length + 1 }, (_, i) => String(i + 1))])}
        </thead>
        <tbody>
          <tr><td /><td /><td /><td>- Số dư đầu kỳ</td><td /><td className="num">{m(l.openingBalance)}</td>{empty}</tr>
          <tr><td /><td /><td /><td>- Số phát sinh trong kỳ</td><td /><td />{empty}</tr>
          {b.debit.map((r, i) => <tr key={i}><td>{dmy(r.postingDate)}</td><td>{r.voucherNumber}</td><td>{dmy(r.voucherDate ?? r.postingDate)}</td><td>{r.description}</td>
            <td className="center">{r.counterAccounts}</td><td className="num">{formatMoney(r.debit)}</td>{cols.map(c => <td key={c} className="num">{b.column(r) === c ? formatMoney(r.debit) : ''}</td>)}</tr>)}
          <tr><td /><td /><td /><td><b>- Cộng số phát sinh trong kỳ</b></td><td /><td className="num"><b>{formatMoney(l.totalDebit)}</b></td>{cols.map(c => <td key={c} className="num"><b>{formatMoney(b.total(c))}</b></td>)}</tr>
          {[...creditBy.entries()].map(([k, v]) => <tr key={k}><td /><td /><td /><td>- Ghi Có TK {l.accountCode}</td><td className="center">{k}</td><td className="num">{formatMoney(v)}</td>{empty}</tr>)}
          <tr><td /><td /><td /><td><b>- Số dư cuối kỳ</b></td><td /><td className="num"><b>{m(l.closingBalance)}</b></td>{empty}</tr>
        </tbody>
      </table>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

/** Mẫu số S37-DN — Thẻ tính giá thành sản phẩm, dịch vụ: opening WIP, costs of the period by cost element (counter account), cost of output, closing WIP. */
export function CostCardPrint({ company, address, product, wip, actualCost, closingWip, from, to }:
  { company: string; address: string; product: string; wip: LedgerData; actualCost: string; closingWip: string; from: string; to: string }) {
  const b = costBreakdown(wip.rows, 7)
  const cols = [...b.fixed, ...(b.hasOther ? ['khác'] : [])]
  const empty = cols.map(c => <td key={c} />)
  return (
    <PrintSheet landscape>
      <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S37-DN" />
      <h1>THẺ TÍNH GIÁ THÀNH SẢN PHẨM, DỊCH VỤ</h1>
      <div className="center"><b>Tháng {from.slice(5, 7)} năm {from.slice(0, 4)}<br />Tên sản phẩm, dịch vụ: {product}</b></div>
      <table>
        <thead>
          <tr><th rowSpan={2}>Chỉ tiêu</th><th rowSpan={2}>Tổng số tiền</th>{cols.length > 0 && <th colSpan={cols.length}>Chia ra theo khoản mục (TK đối ứng của TK {wip.accountCode})</th>}</tr>
          <tr>{cols.map(c => <th key={c}>{c === 'khác' ? 'Khác' : `TK ${c}`}</th>)}</tr>
          {letters(['A', ...Array.from({ length: cols.length + 1 }, (_, i) => String(i + 1))])}
        </thead>
        <tbody>
          <tr><td>1. Chi phí SXKD dở dang đầu kỳ</td><td className="num">{formatMoney(wip.openingBalance)}</td>{empty}</tr>
          <tr><td>2. Chi phí SXKD phát sinh trong kỳ</td><td className="num">{formatMoney(addMoney(b.debit.map(r => r.debit)))}</td>{cols.map(c => <td key={c} className="num">{formatMoney(b.total(c))}</td>)}</tr>
          <tr><td>3. Giá thành sản phẩm, dịch vụ trong kỳ</td><td className="num">{formatMoney(actualCost)}</td>{empty}</tr>
          <tr><td>4. Chi phí SXKD dở dang cuối kỳ</td><td className="num">{formatMoney(closingWip)}</td>{empty}</tr>
        </tbody>
      </table>
      <p><i>Dở dang đầu kỳ, giá thành và dở dang cuối kỳ chưa chia theo khoản mục (phần mềm theo dõi chi phí dở dang theo tổng số).</i></p>
      <BookFoot date={to} />
    </PrintSheet>
  )
}

/**
 * Mẫu số S22-DN — Sổ theo dõi TSCĐ và CCDC tại nơi sử dụng: one table per place of use, assets in use during the year (each card is one
 * unit: quantity 1, price = cost); decrease columns filled for those written off.
 */
export function AssetsInUsePrint({ company, address, assets, year: y }: { company: string; address: string; assets: FixedAssetLine[]; year: string }) {
  const inYear = assets.filter(a => a.inServiceDate <= `${y}-12-31` && (!a.disposalDate || a.disposalDate >= `${y}-01-01`))
  const places = [...new Set(inYear.map(a => a.usedBy?.trim() || ''))].sort((a, b) => (a === '' ? 1 : b === '' ? -1 : a.localeCompare(b)))
  return (
    <PrintSheet landscape>
      {places.map((place, gi) => {
        const rows = inYear.filter(a => (a.usedBy?.trim() || '') === place)
        return (
          <div key={place || '-'} style={gi > 0 ? { breakBefore: 'page' } : undefined}>
            <FormHeader company={company} second={<>Địa chỉ: {address || '......................'}</>} formNo="S22-DN" />
            <h1>SỔ THEO DÕI TÀI SẢN CỐ ĐỊNH VÀ CÔNG CỤ, DỤNG CỤ TẠI NƠI SỬ DỤNG</h1>
            <div className="center"><i>Năm {y}</i><br /><b>Tên đơn vị (phòng, ban hoặc người sử dụng): {place || '(chưa ghi nơi sử dụng)'}</b></div>
            <table>
              <thead>
                <tr><th colSpan={7}>Ghi tăng tài sản cố định và công cụ, dụng cụ</th><th colSpan={5}>Ghi giảm tài sản cố định và công cụ, dụng cụ</th><th rowSpan={3}>Ghi chú</th></tr>
                <tr><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Tên, nhãn hiệu, quy cách tài sản cố định và công cụ, dụng cụ</th><th rowSpan={2}>Đơn vị tính</th><th rowSpan={2}>Số lượng</th>
                  <th rowSpan={2}>Đơn giá</th><th rowSpan={2}>Số tiền</th><th colSpan={2}>Chứng từ</th><th rowSpan={2}>Lý do</th><th rowSpan={2}>Số lượng</th><th rowSpan={2}>Số tiền</th></tr>
                <tr><th>Số hiệu</th><th>Ngày, tháng</th><th>Số hiệu</th><th>Ngày, tháng</th></tr>
                {letters(['A', 'B', 'C', 'D', '1', '2', '3 = 1x2', 'E', 'G', 'H', '4', '5', 'I'])}
              </thead>
              <tbody>
                {rows.map(a => {
                  const out = !!a.disposalDate && a.disposalDate <= `${y}-12-31`
                  return <tr key={a.code}><td>{a.sourceVoucher ?? ''}</td><td>{a.sourceDate ? dmy(a.sourceDate) : ''}</td><td>{a.code} – {a.name}</td><td className="center">cái</td><td className="num">1</td>
                    <td className="num">{formatMoney(a.originalCost)}</td><td className="num">{formatMoney(a.originalCost)}</td>
                    <td>{out ? a.disposalVoucher ?? '' : ''}</td><td>{out ? dmy(a.disposalDate!) : ''}</td><td>{out ? a.disposalReason ?? '' : ''}</td>
                    <td className="num">{out ? '1' : ''}</td><td className="num">{out ? formatMoney(a.originalCost) : ''}</td><td>TK {a.assetAccountCode}</td></tr>
                })}
              </tbody>
            </table>
            <BookFoot date={`${y}-12-31`} />
          </div>
        )
      })}
    </PrintSheet>
  )
}
