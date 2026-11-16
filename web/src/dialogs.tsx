import { useEffect, useState } from 'react'
import { Modal } from './common'

// In-app replacements for the browser's prompt() / confirm(): same call shape, but awaited, so screens ask for a reason, a date
// or a confirmation in the application's own dialog (keyboard: Enter accepts, Esc cancels).

type Request = { id: number } & (
  | { kind: 'text' | 'date' | 'password'; title: string; initial: string; resolve: (v: string | null) => void }
  | { kind: 'confirm'; title: string; resolve: (v: boolean) => void })

let nextId = 1

let enqueue: ((r: Request) => void) | null = null

/** Asks for a line of text; resolves to the trimmed text, or null when cancelled or left empty. */
export function askText(title: string, initial = '', kind: 'text' | 'date' | 'password' = 'text'): Promise<string | null> {
  return new Promise(resolve => {
    if (enqueue) enqueue({ id: nextId++, kind, title, initial, resolve })
    else resolve(window.prompt(title, initial))   // host not mounted (should not happen): fall back to the browser
  })
}

/** Asks for a date (yyyy-mm-dd) with the browser's date picker. */
export const askDate = (title: string, initial = '') => askText(title, initial, 'date')

export function askConfirm(title: string): Promise<boolean> {
  return new Promise(resolve => {
    if (enqueue) enqueue({ id: nextId++, kind: 'confirm', title, resolve })
    else resolve(window.confirm(title))
  })
}

/** Mounted once at the application root; shows the pending requests one at a time. */
export function DialogHost() {
  const [queue, setQueue] = useState<Request[]>([])
  useEffect(() => {
    enqueue = r => setQueue(q => [...q, r])
    return () => { enqueue = null }
  }, [])
  const current = queue[0]
  if (!current) return null
  const done = () => setQueue(q => q.slice(1))
  return current.kind === 'confirm'
    ? <ConfirmDialog key={current.id} request={current} done={done} />
    : <TextDialog key={current.id} request={current} done={done} />
}

function TextDialog({ request: r, done }: { request: Extract<Request, { kind: 'text' | 'date' | 'password' }>; done: () => void }) {
  const [value, setValue] = useState(r.initial)
  const cancel = () => { r.resolve(null); done() }
  const ok = () => { r.resolve(r.kind === 'password' ? value : value.trim()); done() }
  const valid = value.trim() !== ''
  return (
    <Modal title="Nhập thông tin" onClose={cancel} onSubmit={valid ? ok : undefined}
      footer={<><button onClick={cancel}>Hủy</button><button className="primary" disabled={!valid} onClick={ok}>Đồng ý</button></>}>
      <label style={{ display: 'block' }}><span style={{ whiteSpace: 'pre-wrap' }}>{r.title}</span>
        <input type={r.kind} value={value} onChange={e => setValue(e.target.value)} style={{ width: '100%' }}
          autoComplete={r.kind === 'password' ? 'off' : undefined} /></label>
    </Modal>
  )
}

function ConfirmDialog({ request: r, done }: { request: Extract<Request, { kind: 'confirm' }>; done: () => void }) {
  const answer = (v: boolean) => { r.resolve(v); done() }
  return (
    <Modal title="Xác nhận" onClose={() => answer(false)}
      footer={<><button onClick={() => answer(false)}>Không</button><button className="primary" onClick={() => answer(true)}>Đồng ý</button></>}>
      <p style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{r.title}</p>
    </Modal>
  )
}
