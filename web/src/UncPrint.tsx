import type { CSSProperties, ReactNode } from 'react'
import { PrintSheet } from './common'
import { amountInWords, formatMoney } from './money'

export interface UncData {
  number: string; date: string; amount: string; description: string
  payerName: string; payerAddress: string | null; payerAccountNo: string | null; payerBank: string | null
  payeeName: string; payeeAccountNo: string | null; payeeBank: string | null
}

export type UncLayout = 'vcb' | 'agribank' | 'generic'

/** Which bank's paper form the payment order follows: decided from the name of the paying bank. */
export function uncLayoutOf(bankName: string | null | undefined): UncLayout {
  const n = (bankName ?? '').toLowerCase()
  if (/vietcombank|vcb|ngoại thương|ngoai thuong/.test(n)) return 'vcb'
  if (/agribank|nông nghiệp|nong nghiep/.test(n)) return 'agribank'
  return 'generic'
}

const dots = '........................................'
const box: CSSProperties = { border: '1px solid #000', padding: '4px 8px' }
const cell: CSSProperties = { borderBottom: '1px solid #000', padding: '5px 8px', minHeight: 30 }
const Label = ({ vi, en }: { vi: string; en: string }) => <><b>{vi}</b> <i>({en})</i>: </>

/**
 * Ủy nhiệm chi laid out like the paying bank's own paper form (the fields in the same places and with the same captions),
 * so the bank teller reads it as usual. It carries no bank logo: a bank may still insist on its pre-printed form.
 */
export function UncPrint({ data: d, layout }: { data: UncData; layout: UncLayout }) {
  const [y, m, day] = d.date.split('-')
  const words = amountInWords(d.amount)
  if (layout === 'vcb') {
    const Account = ({ no, name, address, bank }: { no: string | null; name: string; address: string | null; bank: string | null }) => (
      <div style={{ border: '1px solid #000' }}>
        <div style={cell}><Label vi="SỐ TK" en="A/C No" />{no ?? ''}</div>
        <div style={cell}><Label vi="TÊN TK" en="A/C name" />{name}<br /><Label vi="ĐỊA CHỈ" en="Address" />{address ?? ''}</div>
        <div style={{ ...cell, borderBottom: 0 }}><Label vi="TẠI NH" en="With Bank" />{bank ?? ''}</div>
      </div>)
    return (
      <PrintSheet>
        <div style={{ border: '1px solid #000', padding: 10, fontSize: '10.5pt' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', borderBottom: '1px solid #000', paddingBottom: 8, marginBottom: 10 }}>
            <div style={{ width: '32%' }}><b>{d.payerBank ?? ''}</b></div>
            <div style={{ textAlign: 'center', flex: 1 }}>
              <b>CHỨNG TỪ GIAO DỊCH</b><br /><span style={{ fontSize: '15pt' }}><b>ỦY NHIỆM CHI</b> - <i>PAYMENT ORDER</i></span><br />
              <b>Ngày</b> <i>(Date)</i>: {day}/{m}/{y}
            </div>
            <div style={{ width: '14%', textAlign: 'right' }}>Số: {d.number}</div>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '42% 1fr', gap: 18 }}>
            <div>
              <p style={{ margin: '0 0 4px' }}><b>ĐỀ NGHỊ GHI NỢ TÀI KHOẢN</b> <i>(Please Debit account)</i>:</p>
              <Account no={d.payerAccountNo} name={d.payerName} address={d.payerAddress} bank={d.payerBank} />
              <p style={{ margin: '12px 0 4px' }}><b>&amp; GHI CÓ TÀI KHOẢN</b> (&amp; Credit account):</p>
              <Account no={d.payeeAccountNo} name={d.payeeName} address={null} bank={d.payeeBank} />
            </div>
            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                <p style={{ margin: '0 0 4px' }}><b>SỐ TIỀN</b> <i>(With amount)</i>:</p><p style={{ margin: '0 0 4px' }}><b>PHÍ NH</b> <i>(Bank charges)</i></p>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 120px', gap: 10 }}>
                <div style={{ border: '1px solid #000' }}>
                  <div style={cell}><Label vi="BẰNG SỐ" en="In figures" /><b>{formatMoney(d.amount)} VND</b></div>
                  <div style={{ ...cell, borderBottom: 0, minHeight: 70 }}><Label vi="BẰNG CHỮ" en="In words" />{words}</div>
                </div>
                <div style={{ fontSize: '9.5pt' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 6 }}><span style={{ ...box, width: 22, height: 22, padding: 0 }} /><span><b>Phí trong</b><br /><i>Including</i></span></div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}><span style={{ ...box, width: 22, height: 22, padding: 0 }} /><span><b>Phí ngoài</b><br /><i>Excluding</i></span></div>
                </div>
              </div>
              <p style={{ margin: '12px 0 0' }}><Label vi="NỘI DUNG" en="Details of Payment" />{d.description}</p>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', textAlign: 'center', marginTop: 16, minHeight: 90 }}>
                <div><b>KẾ TOÁN TRƯỞNG KÝ</b><br /><i>Chief Accountant</i></div>
                <div><b>CHỦ TÀI KHOẢN KÝ VÀ ĐÓNG DẤU</b><br /><i>Acc. Holder &amp; Stamp</i></div>
              </div>
            </div>
          </div>
          <div style={{ borderTop: '1px solid #000', marginTop: 10, paddingTop: 6, minHeight: 110 }}>
            <b><u>DÀNH CHO NGÂN HÀNG</u></b> <i>(for Bank's Use only)</i> &nbsp;&nbsp;&nbsp; <b>MÃ VAT:</b>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', textAlign: 'center', marginTop: 34 }}><b>Thanh toán viên</b><b>Kiểm soát</b><b>Giám đốc</b></div>
          </div>
        </div>
        <div className="foot">Bản in từ lệnh thanh toán {d.number} của HAK Accounting, bố cục theo mẫu ủy nhiệm chi của ngân hàng. Ngân hàng có thể yêu cầu dùng mẫu in sẵn của họ.</div>
      </PrintSheet>
    )
  }
  if (layout === 'agribank') {
    const Row = ({ vi, en, children }: { vi: string; en: string; children: ReactNode }) => (
      <tr><td style={{ border: 0, width: '34%', padding: '3px 6px' }}><b>{vi}:</b><br /><i style={{ fontSize: '9pt' }}>{en}</i></td><td style={{ border: 0, borderBottom: '1px dotted #000', padding: '3px 6px' }}>{children}</td></tr>)
    return (
      <PrintSheet>
        <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '10pt' }}>
          <div><b>{d.payerBank ?? ''}</b></div>
          <div style={{ textAlign: 'right' }}>Loại tiền tệ <i>(Currency)</i>: VND<br />Số <i>(No)</i>: {d.number}<br />Ngày <i>(Date)</i>: {day}/{m}/{y}</div>
        </div>
        <h1>ỦY NHIỆM CHI</h1>
        <div className="center"><i>Payment Order</i></div>
        <table style={{ marginTop: 10 }}>
          <tbody>
            <Row vi="Khách hàng trả tiền" en="Applicant">{d.payerName}</Row>
            <Row vi="Địa chỉ" en="Address">{d.payerAddress ?? ''}</Row>
            <Row vi="Số tài khoản" en="Account No">{d.payerAccountNo ?? ''}</Row>
            <Row vi="Tại ngân hàng" en="At bank">{d.payerBank ?? ''}</Row>
            <Row vi="Khách hàng thụ hưởng" en="Beneficiary">{d.payeeName}</Row>
            <Row vi="Số tài khoản" en="Account No">{d.payeeAccountNo ?? ''}</Row>
            <Row vi="Tại ngân hàng" en="At bank">{d.payeeBank ?? ''}</Row>
            <Row vi="Số tiền bằng số" en="Amount in figures"><b>{formatMoney(d.amount)} VND</b></Row>
            <Row vi="Số tiền bằng chữ" en="Amount in words">{words}</Row>
            <Row vi="Nội dung" en="Details">{d.description}</Row>
          </tbody>
        </table>
        <div className="signs" style={{ gridTemplateColumns: '1fr 1fr' }}>
          <div><b>KHÁCH HÀNG</b> <i>(Customer)</i><br />Kế toán trưởng &nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp; Chủ tài khoản<br /><i>(Ký, họ tên, đóng dấu)</i></div>
          <div><b>NGÂN HÀNG</b> <i>(Bank)</i><br />Giao dịch viên &nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp; Kiểm soát viên<br /><i>(Ký, họ tên)</i></div>
        </div>
        <div className="foot">Bản in từ lệnh thanh toán {d.number} của HAK Accounting, bố cục theo mẫu ủy nhiệm chi của ngân hàng. Ngân hàng có thể yêu cầu dùng mẫu in sẵn của họ.</div>
      </PrintSheet>
    )
  }
  return (
    <PrintSheet>
      <h1>ỦY NHIỆM CHI</h1>
      <div className="center">Số: <b>{d.number}</b> — <i>Ngày {day} tháng {m} năm {y}</i></div>
      <table>
        <tbody>
          <tr><td style={{ width: '50%' }}><b>Đơn vị trả tiền:</b> {d.payerName}<br />Địa chỉ: {d.payerAddress ?? dots}<br />Số tài khoản: {d.payerAccountNo ?? dots}<br />Tại ngân hàng: {d.payerBank ?? dots}</td>
            <td><b>Đơn vị thụ hưởng:</b> {d.payeeName}<br />Số tài khoản: {d.payeeAccountNo ?? dots}<br />Tại ngân hàng: {d.payeeBank ?? dots}</td></tr>
          <tr><td colSpan={2}>Số tiền bằng số: <b>{formatMoney(d.amount)} đồng</b><br />Số tiền bằng chữ: <i>{words}</i><br />Nội dung thanh toán: {d.description}</td></tr>
        </tbody>
      </table>
      <div className="signs" style={{ gridTemplateColumns: '1fr 1fr' }}>
        <div><b>ĐƠN VỊ TRẢ TIỀN</b><br />Kế toán trưởng &nbsp;&nbsp;&nbsp;&nbsp;&nbsp; Chủ tài khoản<br /><i>(Ký, họ tên, đóng dấu)</i></div>
        <div><b>NGÂN HÀNG</b><br />Giao dịch viên &nbsp;&nbsp;&nbsp;&nbsp;&nbsp; Kiểm soát<br /><i>(Ký, họ tên)</i></div>
      </div>
      <div className="foot">Bản in từ lệnh thanh toán {d.number} của HAK Accounting. Ngân hàng có thể yêu cầu dùng mẫu ủy nhiệm chi riêng của họ.</div>
    </PrintSheet>
  )
}
