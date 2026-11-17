// Screen tests: the type-to-search picker, the in-app dialogs and the voucher form, driven like a user would (keyboard and
// mouse events in a simulated browser). The server is replaced by a small fake that answers the requests these screens make.
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SearchSelect } from './combo'
import { Modal } from './common'
import { askConfirm, askText, DialogHost } from './dialogs'
import { VouchersView } from './Vouchers'

afterEach(() => { cleanup(); localStorage.clear() })

const accounts = [
  { id: 'a-cash', code: '1111', name: 'Tiền mặt', parentId: null, isPostable: true, requiresPartner: false, isActive: true, openItemNature: 'None', cashKind: 'Cash', reportTags: null },
  { id: 'a-ar', code: '131', name: 'Phải thu của khách hàng', parentId: null, isPostable: true, requiresPartner: true, isActive: true, openItemNature: 'Receivable', cashKind: 'None', reportTags: null },
  { id: 'a-rev', code: '5111', name: 'Doanh thu bán hàng', parentId: null, isPostable: true, requiresPartner: false, isActive: true, openItemNature: 'None', cashKind: 'None', reportTags: null },
  { id: 'a-sum', code: '511', name: 'Doanh thu (tổng hợp)', parentId: null, isPostable: false, requiresPartner: false, isActive: true, openItemNature: 'None', cashKind: 'None', reportTags: null },
]
const partners = [{ id: 'p-kh', code: 'KH01', name: 'Công ty Đại Dương', taxCode: null, isActive: true }]

/** Fake API: answers GET lists, records POST bodies. */
function fakeServer(extra: Record<string, unknown> = {}) {
  const posted: { url: string; body: unknown }[] = []
  const routes: Record<string, unknown> = {
    '/api/accounts': accounts, '/api/partners': partners, '/api/periods': [{ id: 'per', startDate: '2026-01-01', endDate: '2026-12-31', status: 'Open' }],
    '/api/voucher-templates': [], '/api/analysis-codes': [], '/api/me/companies': [], ...extra,
  }
  vi.stubGlobal('fetch', vi.fn(async (input: string, init?: RequestInit) => {
    const url = String(input), path = url.split('?')[0]
    if (init?.method === 'POST' || init?.method === 'PUT') {
      const body = init.body ? JSON.parse(String(init.body)) : null
      posted.push({ url, body })
      if (path === '/api/vouchers') return Response.json({ id: 'v1', number: 'GJ2026-000001', rowVersion: 1, postingDate: body.postingDate, lines: [] })
      if (path.endsWith('/validate')) return Response.json([])
      return Response.json({})
    }
    if (path === '/api/vouchers') return Response.json({ items: [], total: 0 })
    return Response.json(routes[path] ?? [])
  }))
  return posted
}

describe('SearchSelect (ô chọn gõ để tìm)', () => {
  function Harness({ onCreate }: { onCreate?: (t: string) => void }) {
    const [v, setV] = useState('')
    return <><SearchSelect options={accounts} value={v} onChange={setV} ariaLabel="TK" onCreate={onCreate} /><output>{v}</output></>
  }

  it('finds by part of the name without accents and takes the highlighted row with Enter', () => {
    render(<Harness />)
    const box = screen.getByRole('combobox', { name: 'TK' })
    fireEvent.focus(box)
    fireEvent.change(box, { target: { value: 'phai thu' } })
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['131 – Phải thu của khách hàng'])
    fireEvent.keyDown(box, { key: 'Enter' })
    expect(screen.getByRole('status').textContent).toBe('a-ar')
    expect(screen.queryByRole('listbox')).toBeNull()
  })

  it('moves with the arrow keys, takes an exact code on leaving, clears when emptied', () => {
    render(<Harness />)
    const box = screen.getByRole('combobox', { name: 'TK' })
    fireEvent.focus(box)
    fireEvent.change(box, { target: { value: 'doanh thu' } })
    fireEvent.keyDown(box, { key: 'ArrowDown' })
    fireEvent.keyDown(box, { key: 'Enter' })
    expect(screen.getByRole('status').textContent).toBe('a-sum')
    fireEvent.focus(box)
    fireEvent.change(box, { target: { value: '1111' } })
    fireEvent.blur(box)
    expect(screen.getByRole('status').textContent).toBe('a-cash')
    fireEvent.focus(box)
    fireEvent.change(box, { target: { value: '' } })
    fireEvent.blur(box)
    expect(screen.getByRole('status').textContent).toBe('')
  })

  it('offers "+ Thêm mới" with the typed text and never clears a value it was only tabbed through', () => {
    const created: string[] = []
    render(<Harness onCreate={t => created.push(t)} />)
    const box = screen.getByRole('combobox', { name: 'TK' })
    fireEvent.focus(box)
    fireEvent.change(box, { target: { value: 'KHMOI' } })
    expect(screen.getAllByRole('option').at(-1)!.textContent).toContain('Thêm mới “KHMOI”')
    fireEvent.keyDown(box, { key: 'Enter' })
    expect(created).toEqual(['KHMOI'])
    fireEvent.focus(box)
    fireEvent.change(box, { target: { value: '131' } })
    fireEvent.keyDown(box, { key: 'Enter' })
    fireEvent.focus(box)
    fireEvent.keyDown(box, { key: 'Tab' })
    fireEvent.blur(box)
    expect(screen.getByRole('status').textContent).toBe('a-ar')
  })
})

describe('Hộp thoại của phần mềm', () => {
  it('askText resolves the typed text, Esc resolves null; askConfirm answers yes / no', async () => {
    render(<DialogHost />)
    let answer: Promise<string | null>
    act(() => { answer = askText('Lý do hủy:') })
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('button', { name: 'Đồng ý' })).toHaveProperty('disabled', true)   // empty text cannot be accepted
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: '  Nhập nhầm  ' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Đồng ý' }))
    expect(await answer!).toBe('Nhập nhầm')

    act(() => { answer = askText('Lý do:') })
    fireEvent.keyDown(await screen.findByRole('dialog'), { key: 'Escape' })
    expect(await answer!).toBeNull()

    let yes: Promise<boolean>
    act(() => { yes = askConfirm('Khóa sổ kỳ 3?') })
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Không' }))
    expect(await yes!).toBe(false)
    act(() => { yes = askConfirm('Khóa sổ kỳ 3?') })
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Đồng ý' }))
    expect(await yes!).toBe(true)
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })

  it('Modal: Enter in a field submits, Esc closes', () => {
    const calls: string[] = []
    render(<Modal title="Thử" onClose={() => calls.push('close')} onSubmit={() => calls.push('submit')}><input aria-label="Ô" /></Modal>)
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Ô' }), { key: 'Enter' })
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' })
    expect(calls).toEqual(['submit', 'close'])
  })
})

describe('Form chứng từ', () => {
  it('Enter on the last field adds the balancing line; the draft survives closing; saving sends the lines', async () => {
    const posted = fakeServer()
    render(<><VouchersView /><DialogHost /></>)
    fireEvent.click(await screen.findByRole('button', { name: 'Thêm chứng từ' }))
    await waitFor(() => expect(screen.getAllByRole('combobox', { name: 'Tài khoản' })).toHaveLength(2))

    const pick = (box: HTMLElement, typed: string) => { fireEvent.focus(box); fireEvent.change(box, { target: { value: typed } }); fireEvent.keyDown(box, { key: 'Enter' }) }
    const [acc1, acc2] = screen.getAllByRole('combobox', { name: 'Tài khoản' })
    pick(acc1, '1111')
    fireEvent.change(screen.getAllByRole('textbox', { name: 'Số tiền' })[0], { target: { value: '5000000' } })
    pick(acc2, 'phai thu')
    expect(await screen.findByText(/TK 131 bắt buộc chọn đối tượng/)).toBeTruthy()       // checked while typing
    pick(screen.getAllByRole('combobox', { name: 'Đối tượng' })[1], 'dai duong')
    fireEvent.change(screen.getAllByRole('textbox', { name: 'Số tiền' })[1], { target: { value: '3000000' } })
    await waitFor(() => expect(screen.queryByText(/bắt buộc chọn đối tượng/)).toBeNull())

    // Enter on the last field of the last line: a third line, credit side, with the 2,000,000 still missing.
    const lastField = screen.getAllByRole('textbox', { name: 'Diễn giải dòng' })[1]
    fireEvent.keyDown(lastField, { key: 'Enter' })
    await waitFor(() => expect(screen.getAllByRole('textbox', { name: 'Số tiền' })).toHaveLength(3))
    expect((screen.getAllByRole('textbox', { name: 'Số tiền' })[2] as HTMLInputElement).value).toBe('2.000.000')
    expect((screen.getAllByRole('combobox', { name: 'Nợ/Có' })[2] as HTMLSelectElement).value).toBe('C')
    pick(screen.getAllByRole('combobox', { name: 'Tài khoản' })[2], '5111')
    expect(screen.getByText('Cân bằng')).toBeTruthy()

    // Close and reopen: the lines typed so far come back.
    fireEvent.click(screen.getByRole('button', { name: 'Đóng' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Thêm chứng từ' }))
    expect(await screen.findByText(/Đã khôi phục nội dung đang nhập dở/)).toBeTruthy()
    expect(screen.getAllByRole('textbox', { name: 'Số tiền' })).toHaveLength(3)

    fireEvent.click(screen.getByRole('button', { name: 'Lưu nháp' }))
    await waitFor(() => expect(posted.some(p => p.url === '/api/vouchers')).toBe(true))
    const body = posted.find(p => p.url === '/api/vouchers')!.body as { lines: { accountId: string; side: string; amount: string; partnerId: string | null }[] }
    expect(body.lines.map(l => [l.accountId, l.side, l.amount, l.partnerId])).toEqual([
      ['a-cash', 'D', '5000000', null], ['a-ar', 'C', '3000000', 'p-kh'], ['a-rev', 'C', '2000000', null]])
    await waitFor(() => expect(localStorage.length).toBe(0))   // the kept draft is removed once saved
  })
})
describe('Tệp đính kèm', () => {
  it('uploads each picked file, shows a refused one as an error and still lists the ones stored', async () => {
    const stored: { id: string; fileName: string; size: number; uploadedAt: string; isRemoved: boolean; removeReason: string | null }[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: string, init?: RequestInit) => {
      const url = String(input)
      if (init?.method === 'POST' && url.includes('/attachments?fileName=')) {
        const name = decodeURIComponent(url.split('fileName=')[1])
        if (name.endsWith('.exe')) return Response.json({ code: 'VALIDATION_FAILED', message: 'Loại tệp .exe không được đính kèm.' }, { status: 422 })
        stored.push({ id: 'f' + stored.length, fileName: name, size: 2048, uploadedAt: '2026-11-17T08:00:00Z', isRemoved: false, removeReason: null })
        return Response.json(stored.at(-1))
      }
      return Response.json(stored)
    }))
    const { Attachments } = await import('./Attachments')
    render(<Attachments voucherId="v1" />)
    expect(await screen.findByText('Tệp đính kèm (0)')).toBeTruthy()
    const input = document.querySelector('input[type=file]') as HTMLInputElement
    fireEvent.change(input, { target: { files: [new File(['%PDF'], 'hoa-don.pdf'), new File(['MZ'], 'chay.exe')] } })
    expect(await screen.findByText(/Loại tệp .exe không được đính kèm/)).toBeTruthy()
    expect(await screen.findByText('Tệp đính kèm (1)')).toBeTruthy()
    expect(screen.getByText('hoa-don.pdf')).toBeTruthy()
  })
})