// Posting-rule screen, the events offered on the cash screen and the cash count, against a small fake server.
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { countedTotal } from './CashCount'
import { eventsFor } from './CashDocs'
import { RulesSetup, ruleNeeds, type Rule } from './Rules'

afterEach(() => cleanup())

const accounts = [
  { id: 'a-cash', code: '1111', name: 'Tiền mặt', parentId: null, isPostable: true, requiresPartner: false, isActive: true, openItemNature: 'None', cashKind: 'Cash' },
  { id: 'a-ar', code: '131', name: 'Phải thu của khách hàng', parentId: null, isPostable: true, requiresPartner: true, isActive: true, openItemNature: 'Receivable', cashKind: 'None' },
]
const partners = [{ id: 'p-kh', code: 'KH01', name: 'Công ty Đại Dương', taxCode: null, isActive: true }]
const rule = (p: Partial<Rule>): Rule => ({
  id: 'r', code: 'X', version: 1, documentType: 'CP', businessEvent: 'ADVANCE', priority: 10, effectiveFrom: '2026-01-01', effectiveTo: null, status: 'Active',
  definitionJson: JSON.stringify({ lines: [{ side: 'D', account: 'ROLE:EMPLOYEE_ADVANCE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] }),
  testCasesJson: '[]', lastTestRunJson: null, description: null, ...p,
})

describe('Kiểm kê quỹ', () => {
  it('adds denomination × notes exactly and ignores blank counts', () => {
    expect(countedTotal({ '500000': '3', '200000': '', '1000': '7', '200': '1' })).toBe('1507200')
    expect(countedTotal({})).toBe('0')
  })
})

describe('Nghiệp vụ thu / chi theo quy tắc', () => {
  it('offers the events with an active rule in force on the date, else the built-in list', () => {
    const rules = [rule({}), rule({ id: 'r2', businessEvent: 'EXPENSE', status: 'Retired' }), rule({ id: 'r3', businessEvent: 'OTHER_PAYMENT', effectiveFrom: '2027-01-01' })]
    const ev = eventsFor(rules, 'CP', '2026-11-17')
    expect(ev.map(e => [e.value, e.label, e.fromRule])).toEqual([['ADVANCE', 'Chi tạm ứng', true]])
    expect(ruleNeeds(ev[0].lines)).toEqual({ partner: true, counter: false, settle: true, partnerRole: 'EMPLOYEE_ADVANCE' })
    expect(eventsFor(rules, 'BP', '2026-11-17').map(e => [e.value, e.fromRule])).toEqual([['SUPPLIER_PAYMENT', false], ['INTERNAL_TRANSFER', false]])
  })
})

describe('Quy tắc định khoản', () => {
  it('builds a rule from a preset and saves the sample document with the lines it gives as the test case', async () => {
    const posted: { url: string; body: unknown }[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: string, init?: RequestInit) => {
      const path = String(input).split('?')[0]
      if (init?.method === 'POST') { posted.push({ url: path, body: JSON.parse(String(init.body)) }); return Response.json({ id: 'new', version: 1 }) }
      const routes: Record<string, unknown> = {
        '/api/accounts': accounts, '/api/partners': partners, '/api/rules': [],
        '/api/rules/role-mappings': [{ roleCode: 'AR_TRADE', accountId: 'a-ar', accountCode: '131' }],
        '/api/rules/account-roles': [{ code: 'AR_TRADE', module: 'Bán hàng', description: '', optional: false, account: '131 – Phải thu' }, { code: 'CASH_DEFAULT', module: 'Thu chi', description: '', optional: false, account: null }],
      }
      return Response.json(routes[path] ?? [])
    }))
    render(<RulesSetup />)
    expect(await screen.findByText(/Chưa có quy tắc — chưa lập được phiếu thu/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Thêm quy tắc…' }))
    fireEvent.change(screen.getByRole('combobox', { name: /Bắt đầu từ mẫu/ }), { target: { value: '0' } })   // Thu tiền khách hàng (phiếu thu)
    const pick = (box: HTMLElement, text: string) => { fireEvent.focus(box); fireEvent.change(box, { target: { value: text } }); fireEvent.keyDown(box, { key: 'Enter' }) }
    // No cash account chosen: the money line falls back to the CASH_DEFAULT role, which is not mapped.
    expect(await screen.findByText(/chưa xác định được tài khoản/)).toBeTruthy()
    pick(screen.getByRole('combobox', { name: 'Tài khoản' }), '1111')
    // Then the partner line needs a sample partner.
    expect(await screen.findByText('Quy tắc ghi theo đối tượng: chọn đối tượng mẫu.')).toBeTruthy()
    pick(screen.getByRole('combobox', { name: 'Đối tượng' }), 'KH01')
    await waitFor(() => expect(screen.queryByText(/chọn đối tượng mẫu/)).toBeNull())
    fireEvent.click(screen.getByRole('button', { name: 'Lưu (nháp)' }))
    await waitFor(() => expect(posted.some(p => p.url === '/api/rules')).toBe(true))
    const body = posted.find(p => p.url === '/api/rules')!.body as { code: string; documentType: string; businessEvent: string; definition: unknown; testCases: { document: unknown; expected: unknown }[] }
    expect([body.code, body.documentType, body.businessEvent]).toEqual(['CR-CUSTOMER', 'CR', 'CUSTOMER_RECEIPT'])
    expect(body.definition).toEqual({ lines: [{ side: 'D', account: 'DOC:MAIN_ACCOUNT|ROLE:CASH_DEFAULT' }, { side: 'C', account: 'ROLE:AR_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }] })
    expect(body.testCases[0].document).toEqual({ partner_code: 'KH01', main_account_code: '1111', lines: [{ amount: '1000000', counter_account_code: null }] })
    expect(body.testCases[0].expected).toEqual([{ side: 'D', account_code: '1111', amount: '1000000' }, { side: 'C', account_code: '131', amount: '1000000', partner_code: 'KH01' }])
  })
})
