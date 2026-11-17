// Unsaved-input guard. A screen with typed-but-unsaved input registers itself while it is dirty; leaving the screen (menu,
// drill-down link, changing company, logging out, closing the browser tab) then asks first and stays when the user says no.
import { useEffect } from 'react'
import { askConfirm } from './dialogs'

const dirty = new Map<symbol, string>()

/** Marks the calling screen as holding unsaved input while `isDirty` is true; `what` names it in the question. */
export function useUnsaved(isDirty: boolean, what: string) {
  useEffect(() => {
    if (!isDirty) return
    const id = Symbol(what)
    dirty.set(id, what)
    return () => { dirty.delete(id) }
  }, [isDirty, what])
}

/** Screens currently holding unsaved input (for tests and the question text). */
export const unsavedScreens = () => [...new Set(dirty.values())]

/** True when nothing is unsaved or the user agrees to discard it. */
export async function confirmLeave(): Promise<boolean> {
  const list = unsavedScreens()
  if (list.length === 0) return true
  const ok = await askConfirm(`Đang có dữ liệu nhập chưa lưu: ${list.join(', ')}. Rời đi sẽ mất phần chưa lưu. Vẫn rời đi?`)
  if (ok) dirty.clear()
  return ok
}

// Closing or reloading the browser tab: the browser shows its own question.
if (typeof window !== 'undefined')
  window.addEventListener('beforeunload', e => { if (dirty.size > 0) { e.preventDefault(); e.returnValue = '' } })
