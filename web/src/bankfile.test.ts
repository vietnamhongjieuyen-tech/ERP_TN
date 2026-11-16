import assert from 'node:assert/strict'
import { test } from 'node:test'
import { bankDate, bankNumber, htmlTableRows, parseStatementRows } from './bankfile.ts'
import { parseEInvoiceXml } from './einvoicexml.ts'

test('bankNumber reads the separators banks use', () => {
  assert.equal(bankNumber('1,234,567'), '1234567')
  assert.equal(bankNumber('1.234.567'), '1234567')
  assert.equal(bankNumber('1,234.50'), '1234.5')
  assert.equal(bankNumber('1.234,50'), '1234.5')
  assert.equal(bankNumber('850'), '850')
  assert.equal(bankNumber('1,500'), '1500')
  assert.equal(bankNumber('12.5'), '12.5')
  assert.equal(bankNumber('(2,000)'), '-2000')
  assert.equal(bankNumber('-300 VND'), '-300')
  assert.equal(bankNumber('abc'), null)
  assert.equal(bankNumber(''), null)
})

test('bankDate reads dd/mm/yyyy and ISO dates', () => {
  assert.equal(bankDate('03/10/2026'), '2026-10-03')
  assert.equal(bankDate('3-1-2026 14:05:00'), '2026-01-03')
  assert.equal(bankDate('2026-10-03T08:00:00'), '2026-10-03')
  assert.equal(bankDate('Tổng'), null)
})

// Layout of the HTML ".xls" statement: period and balances as text rows, a two-row header, a totals row. Figures are made up.
const html = `<html><body><table>
<tr><td></td><td>THÔNG BÁO SAO KÊ TÀI KHOẢN</td></tr>
<tr><td>Ông/Bà:</td><td>CONG TY THU NGHIEM</td></tr>
<tr><td>Sao kê từ ngày:</td><td>01/10/2026 đến 31/10/2026</td></tr>
<tr><td colspan="5"><b>Số dư đầu kỳ: 1,000,000 VND</b></td></tr>
<tr><td rowspan="2">Ngày giao dịch</td><td rowspan="2">Số tham chiếu</td><td>Phát sinh</td><td>Phát sinh</td><td rowspan="2">Chi tiết giao dịch</td></tr>
<tr><td>Nợ</td><td>Có</td></tr>
<tr><td>03/10/2026</td><td>5001 - 11111</td><td>11,000</td><td></td><td>THU PHI DICH VU &amp; VAT</td></tr>
<tr><td>25/10/2026</td><td>5002 - 22222</td><td></td><td>850</td><td>INTEREST PAYMENT</td></tr>
<tr><td>26/10/2026</td><td>5003 - 33333</td><td>400,000</td><td></td><td>NOP THUE</td></tr>
<tr><td></td><td></td><td></td><td></td><td></td></tr>
<tr><td>Tổng</td><td></td><td>411,000</td><td>850</td><td></td></tr>
<tr><td colspan="5"><b>Số dư cuối kỳ: 589,850 VND</b></td></tr>
<tr><td>Từ ngày 22/12/2014 ngân hàng triển khai dịch vụ nộp thuế đến 31/12/2030.</td></tr>
</table></body></html>`

test('parseStatementRows reads the HTML statement with a two-row header', () => {
  const s = parseStatementRows(htmlTableRows(html))
  assert.deepEqual([s.fromDate, s.toDate, s.opening, s.closing], ['2026-10-01', '2026-10-31', '1000000', '589850'])
  assert.deepEqual(s.lines, [
    { transactionDate: '2026-10-03', amount: '-11000', bankReference: '5001 - 11111', description: 'THU PHI DICH VU & VAT' },
    { transactionDate: '2026-10-25', amount: '850', bankReference: '5002 - 22222', description: 'INTEREST PAYMENT' },
    { transactionDate: '2026-10-26', amount: '-400000', bankReference: '5003 - 33333', description: 'NOP THUE' },
  ])
  assert.deepEqual(s.warnings, [])
})

test('parseStatementRows reads a plain sheet with one-row header and a balance column', () => {
  const s = parseStatementRows([
    ['Ngày GD', 'Mã giao dịch', 'Ghi nợ', 'Ghi có', 'Số dư', 'Nội dung'],
    ['05/03/2026', 'FT1', '', '2.000.000', '7.000.000', 'KH tra tien'],
    ['06/03/2026', '', '500.000', '', '6.500.000', 'Phi'],
  ])
  assert.deepEqual([s.opening, s.closing, s.fromDate, s.toDate], ['5000000', '6500000', '2026-03-05', '2026-03-06'])
  assert.deepEqual(s.lines.map(l => [l.amount, l.bankReference]), [['2000000', 'FT1'], ['-500000', 'KHONG-MA-2026-03-06-2']])
  assert.deepEqual(s.warnings, [])
})

test('parseStatementRows reports a balance that does not add up and an unknown layout', () => {
  const bad = parseStatementRows([['Số dư đầu kỳ: 100'], ['Ngày', 'Số tiền', 'Nội dung'], ['01/01/2026', '50', 'x'], ['Số dư cuối kỳ: 170']])
  assert.match(bad.warnings.join(' '), /khác số dư cuối/)
  assert.match(parseStatementRows([['a', 'b'], ['1', '2']]).warnings.join(' '), /Không nhận ra dòng tiêu đề/)
})

// Element names of the tax portal's invoice.xml; every value is made up.
const xml = `<?xml version="1.0" encoding="UTF-8"?><HDon><DLHDon Id="x"><TTChung><PBan>2.0.1</PBan><THDon>Hóa đơn giá trị gia tăng</THDon>
<KHMSHDon>1</KHMSHDon><KHHDon>C26TAA</KHHDon><SHDon>00000123</SHDon><NLap>2026-07-21</NLap><DVTTe>VND</DVTTe><TGia>1</TGia></TTChung>
<NDHDon><NBan><Ten>CÔNG TY BÁN &amp; DỊCH VỤ</Ten><MST>0101234567</MST><DChi>Hà Nội</DChi></NBan>
<NMua><Ten>CÔNG TY MUA</Ten><MST>0309999999</MST></NMua>
<DSHHDVu><HHDVu><STT>1</STT><THHDVu>Hàng A</THHDVu><ThTien>1000000</ThTien><TSuat>10%</TSuat></HHDVu>
<HHDVu><STT>2</STT><THHDVu>Hàng B</THHDVu><ThTien>500000.00</ThTien><TSuat>KCT</TSuat></HHDVu></DSHHDVu>
<TToan><THTTLTSuat><LTSuat><TSuat>10%</TSuat><ThTien>1000000</ThTien><TThue>100000</TThue></LTSuat>
<LTSuat><TSuat>KCT</TSuat><ThTien>500000</ThTien><TThue>0</TThue></LTSuat></THTTLTSuat>
<TgTCThue>1500000</TgTCThue><TgTThue>100000</TgTThue><TgTTTBSo>1600000</TgTTTBSo></TToan></NDHDon></DLHDon>
<MCCQT>00ABCDEF0123456789</MCCQT><DSCKS><NBan>chữ ký</NBan></DSCKS></HDon>`

test('parseEInvoiceXml reads an invoice and decides the direction from the company tax code', () => {
  const inv = parseEInvoiceXml(xml, '0309999999')
  assert.deepEqual([inv.direction, inv.sellerTaxCode, inv.sellerName, inv.buyerTaxCode, inv.series, inv.number, inv.issueDate],
    ['Input', '0101234567', 'CÔNG TY BÁN & DỊCH VỤ', '0309999999', '1C26TAA', '00000123', '2026-07-21'])
  assert.deepEqual([inv.amountBeforeVat, inv.vatAmount, inv.totalPayment, inv.status, inv.currency, inv.lookupCode],
    ['1500000', '100000', '1600000', 'Original', null, '00ABCDEF0123456789'])
  assert.deepEqual(inv.rates, [{ vatRate: '10', amountBeforeVat: '1000000', vatAmount: '100000' }, { vatRate: null, amountBeforeVat: '500000', vatAmount: '0' }])
  assert.equal(parseEInvoiceXml(xml, '0101234567').direction, 'Output')
})

test('parseEInvoiceXml falls back to goods lines and rejects other files', () => {
  const noSummary = xml.replace(/<THTTLTSuat>[\s\S]*<\/THTTLTSuat>/, '')
  assert.deepEqual(parseEInvoiceXml(noSummary, null).rates.map(r => [r.vatRate, r.amountBeforeVat]), [['10', '1000000'], [null, '500000']])
  assert.throws(() => parseEInvoiceXml('<a><b>1</b></a>', null), /không phải tệp hóa đơn/)
  assert.throws(() => parseEInvoiceXml(xml.replace('<SHDon>00000123</SHDon>', ''), null), /thiếu MST/)
})
