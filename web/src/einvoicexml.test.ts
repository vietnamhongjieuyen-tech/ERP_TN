import assert from 'node:assert/strict'
import { test } from 'node:test'
import { parseEInvoiceXml } from './einvoicexml.ts'

// Minimal invoice.xml in the tax authority's layout (Cục Thuế data component; series = KHMSHDon + KHHDon, TT 91/2026 Phụ lục I).
const invoice = (form: string, series: string) => `<?xml version="1.0" encoding="UTF-8"?><HDon><DLHDon>
  <TTChung><KHMSHDon>${form}</KHMSHDon><KHHDon>${series}</KHHDon><SHDon>123</SHDon><NLap>2026-07-15</NLap><DVTTe>VND</DVTTe></TTChung>
  <NDHDon><NBan><Ten>Người bán</Ten><MST>0100000001</MST></NBan><NMua><Ten>Người mua</Ten><MST>0100000002</MST></NMua>
  <DSHHDVu><HHDVu><THHDVu>Hàng</THHDVu><ThTien>1000000</ThTien><TSuat>10%</TSuat></HHDVu></DSHHDVu>
  <TToan><THTTLTSuat><LTSuat><TSuat>10%</TSuat><ThTien>1000000</ThTien><TThue>100000</TThue></LTSuat></THTTLTSuat>
  <TgTCThue>1000000</TgTCThue><TgTThue>100000</TgTThue><TgTTTBSo>1100000</TgTTTBSo></TToan></NDHDon></DLHDon><MCCQT>ABC</MCCQT></HDon>`

test('reads the new form numbers of TT 91/2026 (7 = commercial invoice, 8 / 9 = with tax receipt)', () => {
  for (const [form, series] of [['1', 'C26TAA'], ['7', 'K26XAB'], ['8', 'C26TYY']]) {
    const inv = parseEInvoiceXml(invoice(form, series), '0100000002')
    assert.equal(inv.series, form + series)
    assert.equal(inv.number, '123')
  }
})

test('refuses form 6 (internal transfer / consignment stock slip): it is not a VAT invoice', () => {
  assert.throws(() => parseEInvoiceXml(invoice('6', 'K26NAB'), '0100000002'), /mẫu 6.*không phải hóa đơn/)
})

// Layout of an invoice issued after 01/07/2026 under ND 254 / TT 91 (data component version PBan 2.1.0): Id attributes, extra fields
// (TTKhac), a line description with a line break, a non-taxable rate KKKNT, and the seller / tax-authority signature block DSCKS.
// Names and tax codes are invented; the structure follows a real invoice received 17/11/2026.
test('reads the 2026 layout (PBan 2.1.0) with KKKNT and the signature block', () => {
  const xml = `<HDon><DLHDon Id="a1"><TTChung><PBan>2.1.0</PBan><THDon>Hóa đơn giá trị gia tăng</THDon><KHMSHDon>1</KHMSHDon><KHHDon>C26TAB</KHHDon>
    <SHDon>00012345</SHDon><NLap>2026-11-17</NLap><DVTTe>VND</DVTTe><TGia>1</TGia><HTTToan>Tiền mặt/Chuyển khoản</HTTToan><MSTTCGP>0100000009</MSTTCGP>
    <TTKhac><TTin><TTruong>KhoXuat</TTruong><KDLieu>String</KDLieu><DLieu>Kho chung</DLieu></TTin></TTKhac></TTChung>
    <NDHDon><NBan><Ten>CÔNG TY BÁN THỬ</Ten><MST>0100000001</MST><DChi>Địa chỉ người bán</DChi></NBan><NMua><Ten>Công ty mua thử</Ten><MST>0100000002</MST></NMua>
    <DSHHDVu><HHDVu><TChat>1</TChat><STT>1</STT><THHDVu>Dầu Điêzen 0,05S Mức 2
</THHDVu><DVTinh>Lít</DVTinh><SLuong>54.08</SLuong><DGia>26820.01</DGia><TLCKhau>0</TLCKhau><STCKhau>0</STCKhau><ThTien>1450426</ThTien><TSuat>KKKNT</TSuat></HHDVu></DSHHDVu>
    <TToan><THTTLTSuat><LTSuat><TSuat>KKKNT</TSuat><ThTien>1450426</ThTien><TThue>0</TThue></LTSuat></THTTLTSuat><TgTCThue>1450426</TgTCThue><TgTThue>0</TgTThue>
    <TTCKTMai>0</TTCKTMai><TgTTTBSo>1450426</TgTTTBSo><TgTTTBChu>Một triệu bốn trăm năm mươi nghìn bốn trăm hai mươi sáu đồng chẵn./.</TgTTTBChu></TToan></NDHDon></DLHDon>
    <MCCQT Id="Id-1">00AB12</MCCQT><DSCKS><NBan><Signature xmlns="http://www.w3.org/2000/09/xmldsig#"><SignedInfo /></Signature></NBan><NMua /><CCKSKhac /><CQT /></DSCKS></HDon>`
  const inv = parseEInvoiceXml(xml, '0100000002')
  assert.equal(inv.direction, 'Input')
  assert.deepEqual([inv.series, inv.number, inv.issueDate, inv.lookupCode], ['1C26TAB', '00012345', '2026-11-17', '00AB12'])
  assert.deepEqual([inv.amountBeforeVat, inv.vatAmount, inv.totalPayment], ['1450426', '0', '1450426'])
  assert.deepEqual(inv.rates, [{ vatRate: null, amountBeforeVat: '1450426', vatAmount: '0' }])
})