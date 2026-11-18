// Printed books (TT 99/2025 Phụ lục III): totals, month blocks of the general ledger, Nợ / Có balance columns.
import { cleanup, render } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { CashBookPrint, DetailLedgerPrint, GeneralLedgerPrint, JournalBookPrint, type LedgerData } from './books'

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
})
