// Nested dialogs take keys only on the top one; leaving a screen with unsaved input asks first.
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { newRequestKey, saveOnce } from './api'
import { Modal } from './common'
import { DialogHost } from './dialogs'
import { confirmLeave, unsavedScreens, useUnsaved } from './leave'

afterEach(() => cleanup())

describe('Hộp thoại lồng nhau', () => {
  it('Esc closes only the dialog on top, then the one below', () => {
    function Harness() {
      const [outer, setOuter] = useState(true)
      const [inner, setInner] = useState(true)
      return <>
        {outer && <Modal title="Ngoài" onClose={() => setOuter(false)}><input aria-label="ô ngoài" />
          {inner && <Modal title="Trong" onClose={() => setInner(false)}><input aria-label="ô trong" /></Modal>}</Modal>}
      </>
    }
    render(<Harness />)
    fireEvent.keyDown(window, { key: 'Escape' })
    expect(screen.queryByRole('dialog', { name: 'Trong' })).toBeNull()
    expect(screen.getByRole('dialog', { name: 'Ngoài' })).toBeTruthy()
    fireEvent.keyDown(window, { key: 'Escape' })
    expect(screen.queryByRole('dialog', { name: 'Ngoài' })).toBeNull()
  })
})

describe('Rời màn hình khi chưa lưu', () => {
  function Form() {
    const [v, setV] = useState('')
    useUnsaved(v.trim() !== '', 'phiếu thử')
    return <input aria-label="số tiền" value={v} onChange={e => setV(e.target.value)} />
  }

  it('asks only when something was typed, and staying keeps the input', async () => {
    render(<><Form /><DialogHost /></>)
    expect(await confirmLeave()).toBe(true)                      // nothing typed: no question
    fireEvent.change(screen.getByRole('textbox', { name: 'số tiền' }), { target: { value: '500000' } })
    expect(unsavedScreens()).toEqual(['phiếu thử'])

    let answer: Promise<boolean> = Promise.resolve(true)
    act(() => { answer = confirmLeave() })
    expect(await screen.findByText(/Đang có dữ liệu nhập chưa lưu: phiếu thử/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Không' }))
    expect(await answer).toBe(false)
    expect((screen.getByRole('textbox', { name: 'số tiền' }) as HTMLInputElement).value).toBe('500000')

    act(() => { answer = confirmLeave() })
    fireEvent.click(await screen.findByRole('button', { name: 'Đồng ý' }))
    expect(await answer).toBe(true)
    await waitFor(() => expect(unsavedScreens()).toEqual([]))
  })
})

describe('Lưu khi mất kết nối', () => {
  it('sends again with the same key when the server could not be reached', async () => {
    const keys: string[] = []
    let calls = 0
    vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
      keys.push((init?.headers as Record<string, string>)['Idempotency-Key'])
      if (++calls === 1) throw new TypeError('Failed to fetch')
      return Response.json({ id: 'v1' })
    }))
    const key = newRequestKey()
    expect(key).toMatch(/^[0-9a-f]{32}$/)
    expect(await saveOnce<{ id: string }>('POST', '/vouchers', { a: 1 }, key)).toEqual({ id: 'v1' })
    expect(keys).toEqual([key, key])
  }, 10_000)
})