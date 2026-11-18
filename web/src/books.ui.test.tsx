// Printed books (TT 99/2025 Phụ lục III): totals, month blocks of the general ledger, Nợ / Có balance columns.
import { cleanup, render } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { BankBookPrint, FixedAssetBookPrint, FixedAssetCardPrint, CashBookPrint, DetailLedgerPrint, GeneralLedgerPrint, JournalBookPrint, MaterialLedgerPrint, StockCardPrint, SalesBookPrint, StockSummaryPrint, type LedgerData, type StockCardLine } from './books'

afterEach(() => cleanup())
const sheet = () => document.querySelector('.print-sheet') as HTMLElement
const rowText = (label: string) => [...sheet().querySelectorAll('tr')].find(r => r.textContent?.includes(label))!.textContent

const ledger: LedgerData = {
  accountCode: '131', accountName: 'Phải thu của khách hàng', openingBalance: '100', totalDebit: '550', totalCredit: '300', closingBalance: '350',
  rows: [
    { postingDate: '2026-02-10', voucherDate: '2026-02-09', voucherNumber: 'SI-1', voucherType: 'SI', description: 'Bán chịu', counterAccounts: '511, 3331', debit: '550', credit: '0', runningBalance: '650' },
    { postingDate: '2026-04-05', voucherNumber: 'CR-1', voucherType: 'CR', description: 'Khách trả', counterAccounts: '1111', debit: '0', credit: '700', runningBalance: '-50' },
    { postingDate: '2026-04-20', voucherNumber: 'SI-2', voucherType: 'SI', description: 'Điều chỉnh', counterAccounts: '511', debit: '400', credit: '0', runningBalance: '350' },
  ],
}

describe('Sổ theo Phụ lục III', () => {
  it('S03a-DN: one numbered line per journal line, voucher date, totals carried to the next page', () => {
    render(<JournalBookPrint company="Công ty thử" address="" from="2026-01-01" to="2026-12-31" rows={[
      { postingDate: '2026-03-02', voucherDate: '2026-03-01', voucherNumber: 'GJ-1', lineNo: 1, accountCode: '111', debit: '1000', credit: '0', description: 'Góp vốn' },
      { postingDate: '2026-03-02', voucherDate: '2026-03-01', voucherNumber: 'GJ-1', lineNo: 2, accountCode: '4111', debit: '0', credit: '1000', description: 'Góp vốn' }]} />)
    expect(sheet().textContent).toContain('Mẫu số S03a-DN')
    expect(rowText('4111')).toContain('01/03/2026')
    expect(rowText('Cộng chuyển sang trang sau')).toContain('1.000')
  })

  it('S03b-DN: month blocks with month totals, month-end balance and the quarter cumulative', () => {
    render(<GeneralLedgerPrint company="Công ty thử" address="" ledger={ledger} from="2026-01-01" to="2026-12-31" />)
    expect(rowText('Số dư đầu năm')).toContain('100')
    const months = [...sheet().querySelectorAll('tr')].filter(r => r.textContent?.includes('Cộng số phát sinh tháng')).map(r => r.textContent)
    expect(months).toEqual([expect.stringContaining('550'), expect.stringContaining('400700')])
    const cumulative = [...sheet().querySelectorAll('tr')].filter(r => r.textContent?.includes('luỹ kế từ đầu quý')).map(r => r.textContent)
    expect(cumulative[1]).toContain('400700')   // April starts quarter 2: February is not carried into it
  })

  it('S31-DN: discount-term column and the running balance on its Nợ / Có side', () => {
    render(<DetailLedgerPrint company="Công ty thử" address="" ledger={ledger} from="2026-01-01" to="2026-12-31" partner="KH01" form="S31" />)
    expect(sheet().className).toContain('landscape')
    expect(sheet().textContent).toContain('Thời hạn được chiết khấu')
    expect(rowText('Khách trả')).toMatch(/700\s*50$/)            // dư Có 50 after the receipt
    expect(rowText('Số dư cuối kỳ')).toContain('350')
  })

  it('S07-DN: receipt and payment numbers in their own columns, running cash balance', () => {
    render(<CashBookPrint company="Công ty thử" address="" from="2026-04-01" to="2026-04-30" ledger={{
      accountCode: '1111', accountName: 'Tiền mặt', openingBalance: '1000', totalDebit: '200', totalCredit: '300', closingBalance: '900',
      rows: [{ postingDate: '2026-04-02', voucherNumber: 'PT-1', voucherType: 'CR', description: 'Thu', counterAccounts: '131', debit: '200', credit: '0', runningBalance: '1200' },
        { postingDate: '2026-04-03', voucherNumber: 'PC-1', voucherType: 'CP', description: 'Chi', counterAccounts: '642', debit: '0', credit: '300', runningBalance: '900' }] }} />)
    expect(sheet().textContent).toContain('SỔ QUỸ TIỀN MẶT')
    expect(rowText('PC-1')).toContain('300900')
    expect(rowText('Số tồn quỹ cuối kỳ')).toContain('900')
  })

  it('S08-DN: bank name and account number from the deposit account, running balance', () => {
    render(<BankBookPrint company="Công ty thử" address="" from="2026-04-01" to="2026-04-30" bankName="NH TMCP A" bankAccountNo="0123456789" ledger={{
      accountCode: '1121', accountName: 'Tiền gửi NH A', openingBalance: '5000', totalDebit: '1000', totalCredit: '2500', closingBalance: '3500',
      rows: [{ postingDate: '2026-04-02', voucherNumber: 'BC-1', voucherType: 'BR', description: 'Khách chuyển khoản', counterAccounts: '131', debit: '1000', credit: '0', runningBalance: '6000' },
        { postingDate: '2026-04-09', voucherNumber: 'BN-1', voucherType: 'BP', description: 'Trả NCC', counterAccounts: '331', debit: '0', credit: '2500', runningBalance: '3500' }] }} />)
    expect(sheet().textContent).toContain('Mẫu số S08-DN')
    expect(sheet().textContent).toContain('Nơi mở tài khoản giao dịch: NH TMCP A')
    expect(sheet().textContent).toContain('Số hiệu tài khoản tại nơi gửi: 0123456789')
    expect(rowText('Trả NCC')).toContain('2.5003.500')
    expect(rowText('Số dư cuối kỳ')).toContain('3.500')
  })

  // 10 in at 100 = 1,000; 4 out costed 400; a value adjustment +50; 3 out not yet costed (monthly average).
  const card: StockCardLine[] = [
    { date: '2026-05-03', documentDate: '2026-05-02', documentNumber: 'NK-1', description: 'Nhập mua', counterAccount: '331', inQty: '10', outQty: '0', value: '1000', balanceQty: '15', balanceValue: '1500' },
    { date: '2026-05-10', documentNumber: 'XK-1', description: 'Xuất bán', counterAccount: '632', inQty: '0', outQty: '4', value: '400', balanceQty: '11', balanceValue: '1100' },
    { date: '2026-05-20', documentNumber: 'ĐCGT', description: 'Điều chỉnh giá HĐ', inQty: '0', outQty: '0', value: '50', balanceQty: '11', balanceValue: '1150' },
    { date: '2026-06-02', documentNumber: 'XK-2', description: 'Xuất SX', counterAccount: '621', inQty: '0', outQty: '3', value: null, balanceQty: '8', balanceValue: null },
  ]
  const info = { company: 'Công ty thử', address: '', warehouse: 'K1 – Kho chính', itemCode: 'DAM', itemName: 'Dăm gỗ', unit: 'tấn', account: '156', from: '2026-05-01', to: '2026-06-30' }

  it('S12-DN: quantities only, opening worked back from the first row, value adjustments left out', () => {
    render(<StockCardPrint info={info} rows={card} />)
    expect(sheet().textContent).toContain('THẺ KHO (SỔ KHO)')
    expect(rowText('Tồn đầu kỳ')).toContain('5')
    expect(rowText('Nhập mua')).toContain('02/05/2026')          // ngày chứng từ, then ngày nhập
    expect(sheet().textContent).not.toContain('Điều chỉnh giá HĐ')
    expect(rowText('Cộng cuối kỳ')).toMatch(/107\s*8/)
  })

  it('S10-DN: unit price, values in and out, month totals, rows not yet costed are marked', () => {
    render(<MaterialLedgerPrint info={info} rows={card} />)
    expect(sheet().className).toContain('landscape')
    expect(rowText('Số dư đầu kỳ')).toContain('5500')               // 5 units worth 500
    expect(rowText('Nhập mua')).toContain('331')
    expect(rowText('Nhập mua')).toContain('100101.000')              // đơn giá 100 | 10 units | 1.000
    expect(rowText('Điều chỉnh giá HĐ')).toContain('Điều chỉnh giá trị')
    expect(rowText('Xuất SX')).toContain('Chờ tính giá')
    expect(rowText('Cộng tháng 05/2026')).toContain('101.0504400111.150')
  })

  it('S11-DN: one table per inventory account with its totals', () => {
    render(<StockSummaryPrint company="Công ty thử" address="" from="2026-05-01" to="2026-05-31" groups={[
      { account: '152 – Nguyên liệu', rows: [{ warehouseCode: 'K1', itemCode: 'GO', itemName: 'Gỗ', openingValue: '100', inValue: '50', outValue: '30', closingValue: '120' },
        { warehouseCode: 'K2', itemCode: 'GO', itemName: 'Gỗ', openingValue: '0', inValue: '10', outValue: '0', closingValue: '10' }] },
      { account: '156 – Hàng hóa', rows: [{ warehouseCode: 'K1', itemCode: 'DAM', itemName: 'Dăm', openingValue: '7', inValue: '0', outValue: '7', closingValue: '0' }] }]} />)
    expect([...sheet().querySelectorAll('h1')].length).toBe(2)
    expect(sheet().textContent).toContain('Tháng 05 năm 2026')
    expect(rowText('Cộng')).toContain('1006030130')
  })

  it('S35-DN: revenue lines, deductions, then net revenue, cost of sales and gross profit', () => {
    render(<SalesBookPrint company="Công ty thử" address="" from="2026-03-01" to="2026-03-31" book={{
      itemCode: 'PHU', itemName: 'Phụ gia', unit: 'kg', totalQuantity: '40', totalRevenue: '3300000', totalTax: '0', totalDeduction: '100000',
      netRevenue: '3200000', costOfSales: '2000000', costComplete: false, grossProfit: '1200000',
      rows: [{ postingDate: '2026-03-04', number: 'HDB-1', invoiceDate: '2026-03-03', invoiceRef: '1C26TBB0000101', description: 'Bán phụ gia', counterAccount: '131',
        quantity: '30', unitPrice: '80000', revenue: '2400000', tax: '0', deduction: '0' },
      { postingDate: '2026-03-20', number: 'HDB-3', invoiceDate: '2026-03-20', invoiceRef: '1C26TBB0000103', description: 'Giảm giá / chiết khấu: Chiết khấu', counterAccount: '131',
        quantity: null, unitPrice: null, revenue: '0', tax: '0', deduction: '100000' }] }} />)
    expect(sheet().textContent).toContain('Mẫu số S35-DN')
    expect(rowText('Bán phụ gia')).toContain('3080.0002.400.000')
    expect(rowText('Chiết khấu')).toMatch(/100\.000$/)
    expect(rowText('Doanh thu thuần')).toContain('3.200.000')
    expect(rowText('Giá vốn hàng bán')).toContain('chờ tính giá')
    expect(rowText('Lãi gộp')).toContain('1.200.000')
  })

  const machine = { code: 'TS01', name: 'Máy xúc', assetAccountCode: '2112', inServiceDate: '2026-02-15', usefulLifeMonths: 60, originalCost: '120000000',
    residualValue: '0', accumulated: '20000000', sourceVoucher: 'HDM-7', sourceDate: '2026-02-10', disposalDate: null }

  it('S21-DN: one table per asset account, yearly rate and amount, decrease columns', () => {
    render(<FixedAssetBookPrint company="Công ty thử" address="" year="2026" assets={[machine,
      { ...machine, code: 'TS02', name: 'Xe tải', usefulLifeMonths: 96, originalCost: '96000000', accumulated: '5000000', disposalDate: '2026-10-31', disposalVoucher: 'FAD-1', disposalReason: 'Thanh lý' },
      { ...machine, code: 'NX01', name: 'Nhà xưởng', assetAccountCode: '2111' }]} />)
    expect([...sheet().querySelectorAll('h1')].length).toBe(2)                    // 2111 and 2112
    expect(rowText('Máy xúc')).toContain('02/2026')
    expect(rowText('Máy xúc')).toContain('120.000.0002024.000.000')                       // 20 %/năm, 24.000.000 / năm
    expect(rowText('Xe tải')).toMatch(/5\.000\.000FAD-131\/10\/2026Thanh lý$/)
  })

  it('S23-DN: cost lines and depreciation per year with the running total', () => {
    render(<FixedAssetCardPrint company="Công ty thử" address="" date="2027-03-31" card={{ asset: machine, openingAccumulated: '0',
      sources: [{ voucherNumber: 'HDM-7', postingDate: '2026-02-10', accountCode: '2112', amount: '120000000' }],
      booked: [{ periodEnd: '2026-11-30', amount: '2000000' }, { periodEnd: '2026-12-31', amount: '2000000' }, { periodEnd: '2027-01-31', amount: '2000000' }] }} />)
    expect(sheet().textContent).toContain('THẺ TÀI SẢN CỐ ĐỊNH')
    expect(rowText('HDM-7')).toContain('120.000.000')
    expect(rowText('HDM-7')).toMatch(/20264\.000\.0004\.000\.000$/)
    expect(rowText('2027')).toMatch(/20272\.000\.0006\.000\.000$/)
  })
})
