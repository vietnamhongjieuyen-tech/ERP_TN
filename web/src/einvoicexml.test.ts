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
