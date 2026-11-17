import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { ApiError, get, post, session, type Account, type Partner } from './api'
import { SearchSelect } from './combo'
import { catalogChanged } from './nav'
import { downloadXlsx, type Cell, type Column } from './xlsx'

/** "Xuất Excel" for any report table: exact decimal values, typed numeric columns. */
export function ExportButton({ fileName, sheet, columns, rows, disabled }: { fileName: string; sheet: string; columns: Column[]; rows: Cell[][]; disabled?: boolean }) {
  return <>
    <button disabled={disabled || rows.length === 0} onClick={() => downloadXlsx(fileName, sheet, columns, rows)}>Xuất Excel</button>
    <button disabled={disabled || rows.length === 0} onClick={() => window.print()} title="In trang hiện tại (ẩn menu, nút bấm)">In</button>
  </>
}

/** A document laid out for paper, outside the app shell; shown only while printSheet() prints it. */
export function PrintSheet({ children }: { children: ReactNode }) {
  return createPortal(<div className="print-sheet">{children}</div>, document.body)
}

/** Prints only the PrintSheet content (window.print blocks until the print dialog closes). */
export function printSheet() {
  document.body.classList.add('print-sheet-mode')
  try { window.print() } finally { document.body.classList.remove('print-sheet-mode') }
}

// Open modals: only the top one answers Esc / Tab / Enter (a confirmation opened over a form must not also close the form).
// Top = deepest nesting (a modal rendered inside another's content), then the most recently opened (a dialog opened later
// from elsewhere, e.g. askConfirm). Depth matters because effects of nested components run child-first.
const ModalDepth = createContext(0)
const modalStack: { id: symbol; depth: number; seq: number }[] = []
let modalSeq = 0
const topModal = () => modalStack.reduce<(typeof modalStack)[number] | null>((t, m) => (!t || m.depth > t.depth || (m.depth === t.depth && m.seq > t.seq) ? m : t), null)?.id

/** Modal dialog: Esc or the backdrop closes it (unless busy); focus moves into it and Tab stays inside it; Enter in a field runs onSubmit (pass it only when the form is valid). Nested modals: only the top one takes keys. */
export function Modal({ title, onClose, children, footer, busy, onSubmit }: { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode; busy?: boolean; onSubmit?: () => void }) {
  const [box, setBox] = useState<HTMLDivElement | null>(null)
  const depth = useContext(ModalDepth) + 1
  const latest = useRef({ onClose, busy, onSubmit })
  latest.current = { onClose, busy, onSubmit }
  useEffect(() => {
    if (!box) return
    const focusable = () => [...box.querySelectorAll<HTMLElement>('input, select, textarea, button')].filter(el => !(el as HTMLButtonElement).disabled)
    const back = document.activeElement as HTMLElement | null
    const me = Symbol('modal')
    modalStack.push({ id: me, depth, seq: ++modalSeq })
    if (topModal() === me) focusable().find(el => el.tagName !== 'BUTTON')?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (topModal() !== me || e.defaultPrevented) return
      if (e.key === 'Escape' && !latest.current.busy) { e.preventDefault(); latest.current.onClose() }
      const t = e.target as HTMLInputElement
      if (e.key === 'Enter' && t.tagName === 'INPUT' && t.type !== 'checkbox' && box.contains(t) && !latest.current.busy && latest.current.onSubmit) {
        e.preventDefault(); latest.current.onSubmit()
      }
      if (e.key !== 'Tab') return
      const els = focusable()
      if (els.length === 0) return
      const i = els.indexOf(document.activeElement as HTMLElement)
      if (e.shiftKey && i <= 0) { e.preventDefault(); els[els.length - 1].focus() }
      else if (!e.shiftKey && (i === -1 || i === els.length - 1)) { e.preventDefault(); els[0].focus() }
    }
    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('keydown', onKey)
      modalStack.splice(modalStack.findIndex(m => m.id === me), 1)
      back?.focus?.()
    }
  }, [box, depth])
  return createPortal(
    <div className="modal-backdrop" onMouseDown={e => { if (e.target === e.currentTarget && !busy) onClose() }}>
      <div className="modal" role="dialog" aria-modal="true" aria-label={title} ref={setBox}>
        <header><h3>{title}</h3><button className="tree-toggle" aria-label="Đóng" disabled={busy} onClick={onClose}>✕</button></header>
        <div className="modal-body"><ModalDepth.Provider value={depth}>{children}</ModalDepth.Provider></div>
        {footer && <footer>{footer}</footer>}
      </div>
    </div>, document.body)
}

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null
  if (error instanceof ApiError)
    return (
      <div className="msg error" role="alert">
        <strong>{error.code}</strong>: {error.message}
        {error.errors.length > 1 && (
          <ul>{error.errors.map((e, i) => <li key={i}>{e.lineNo ? `Dòng ${e.lineNo}: ` : ''}{e.message}</li>)}</ul>
        )}
      </div>
    )
  // A check made in the screen itself: show its message only, not the technical "Error:" prefix.
  return <div className="msg error" role="alert">{error instanceof Error ? error.message : String(error)}</div>
}

/** Runs an async action, tracking busy state and the last error. */
export function useAction() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const run = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    setBusy(true); setError(null)
    try { return await fn() } catch (e) { setError(e); return undefined } finally { setBusy(false) }
  }, [])
  return { busy, error, setError, run }
}

export function useCatalog() {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [partners, setPartners] = useState<Partner[]>([])
  useEffect(() => {
    const load = () => {
      get<Account[]>('/accounts').then(setAccounts).catch(() => setAccounts([]))
      get<Partner[]>('/partners').then(setPartners).catch(() => setPartners([]))
    }
    load()
    window.addEventListener('hak:catalog', load)   // a partner added from inside a form (nav.catalogChanged)
    return () => window.removeEventListener('hak:catalog', load)
  }, [])
  return { accounts, partners, postable: accounts.filter(a => a.isPostable && a.isActive) }
}

/** Account picker: type the code or part of the name (accents optional). */
export function AccountSelect({ accounts, value, onChange, filter, ariaLabel }:
  { accounts: Account[]; value: string; onChange: (id: string) => void; filter?: (a: Account) => boolean; ariaLabel?: string }) {
  const options = useMemo(() => accounts.filter(a => (a.isPostable && a.isActive && (!filter || filter(a))) || a.id === value), [accounts, filter, value])
  return <SearchSelect options={options} value={value} onChange={onChange} ariaLabel={ariaLabel ?? 'Tài khoản'} />
}

/** Partner picker with type-to-search; the last row adds a new partner without leaving the form. */
export function PartnerSelect({ partners, value, onChange }: { partners: Partner[]; value: string; onChange: (id: string) => void }) {
  const options = useMemo(() => partners.filter(p => p.isActive || p.id === value), [partners, value])
  const [adding, setAdding] = useState<string | null>(null)
  return <>
    <SearchSelect options={options} value={value} onChange={onChange} ariaLabel="Đối tượng" placeholder="—" onCreate={setAdding} />
    {adding !== null && <QuickPartnerDialog typed={adding} onClose={() => setAdding(null)} onCreated={id => { setAdding(null); catalogChanged(); onChange(id) }} />}
  </>
}

const PARTNER_ROLES: [string, string][] = [['Customer', 'Khách hàng'], ['Supplier', 'Nhà cung cấp'], ['Employee', 'Nhân viên'], ['Other', 'Khác']]

function QuickPartnerDialog({ typed, onClose, onCreated }: { typed: string; onClose: () => void; onCreated: (id: string) => void }) {
  // Text that looks like a code (no spaces, short) goes to the code box, anything else to the name.
  const asCode = typed !== '' && !/\s/.test(typed) && typed.length <= 20
  const [code, setCode] = useState(asCode ? typed.toUpperCase() : '')
  const [name, setName] = useState(asCode ? '' : typed)
  const [taxCode, setTaxCode] = useState('')
  const [roles, setRoles] = useState<string[]>(['Customer'])
  const { busy, error, run } = useAction()
  const valid = code.trim() !== '' && name.trim() !== '' && roles.length > 0
  const save = () => run(async () => {
    const r = await post<{ id: string }>('/partners', { code: code.trim(), name: name.trim(), roles: roles.join(', '), taxCode: taxCode.trim() || null })
    onCreated(r.id)
  })
  return (
    <Modal title="Thêm đối tượng" onClose={onClose} busy={busy} onSubmit={valid ? save : undefined}
      footer={<><button onClick={onClose} disabled={busy}>Hủy</button><button className="primary" disabled={busy || !valid} onClick={save}>Thêm và chọn</button></>}>
      <ErrorBox error={error} />
      <div className="form-row">
        <label>Mã<input value={code} onChange={e => setCode(e.target.value.toUpperCase())} /></label>
        <label style={{ flex: 1 }}>Tên<input value={name} onChange={e => setName(e.target.value)} /></label>
        <label>Mã số thuế<input value={taxCode} onChange={e => setTaxCode(e.target.value)} /></label>
      </div>
      <div className="form-row">{PARTNER_ROLES.map(([k, t]) => (
        <label key={k} className="check"><input type="checkbox" checked={roles.includes(k)}
          onChange={e => setRoles(rs => (e.target.checked ? [...rs, k] : rs.filter(x => x !== k)))} /> {t}</label>))}</div>
      <p className="muted">Thông tin khác (địa chỉ, tài khoản ngân hàng…) bổ sung sau ở Danh mục đối tượng.</p>
    </Modal>
  )
}

/** Vietnamese names of permissions, for "cần quyền …" hints. */
export const PERM_VI: Record<string, string> = {
  'voucher.submit': 'gửi duyệt chứng từ', 'voucher.approve': 'duyệt chứng từ', 'voucher.post': 'ghi sổ', 'voucher.reverse': 'đảo bút toán',
  'voucher.edit_draft': 'sửa / hủy chứng từ nháp', 'voucher.create': 'lập chứng từ', 'config.manage': 'quản trị cấu hình', 'payment_order.approve': 'duyệt lệnh chi',
}

/** The user's permissions at the selected company (null while loading: buttons stay enabled and the server decides). */
export function usePermissions(): Set<string> | null {
  const [perms, setPerms] = useState<Set<string> | null>(null)
  useEffect(() => {
    get<{ id: string; permissions?: string[] }[]>('/me/companies')
      .then(cs => { const p = cs.find(c => c.id === session.companyId)?.permissions; setPerms(p ? new Set(p) : null) })
      .catch(() => setPerms(null))
  }, [])
  return perms
}

export const STATUS_VI: Record<string, string> = {
  Draft: 'Nháp', Submitted: 'Chờ duyệt', Approved: 'Đã duyệt', Posted: 'Đã ghi sổ', Reversed: 'Đã đảo', Cancelled: 'Đã hủy',
}

/** Mã số B03-DN (TT 99/2025) for cash lines; empty = derived from the counter-account. */
export const CASHFLOW_VI: Record<string, string> = {
  '01': 'Thu bán hàng, cung cấp DV', '02': 'Chi trả người cung cấp', '03': 'Chi trả người lao động', '04': 'Chi phí đi vay đã trả',
  '05': 'Thuế TNDN đã nộp', '06': 'Thu khác HĐKD', '07': 'Chi khác HĐKD', '21': 'Chi mua sắm, XD TSCĐ, TS dài hạn',
  '22': 'Thu thanh lý TSCĐ, TS dài hạn', '23': 'Chi cho vay, mua công cụ nợ', '24': 'Thu hồi cho vay, bán công cụ nợ',
  '25': 'Chi góp vốn vào đơn vị khác', '26': 'Thu hồi góp vốn', '27': 'Thu lãi, cổ tức, lợi nhuận được chia',
  '31': 'Thu phát hành CP, nhận vốn góp', '32': 'Trả lại vốn góp, mua lại CP', '33': 'Thu từ đi vay', '34': 'Trả nợ gốc vay',
  '35': 'Trả nợ gốc thuê tài chính', '36': 'Cổ tức, lợi nhuận đã trả', '61': 'Ảnh hưởng chênh lệch tỷ giá',
}

export const TYPE_VI: Record<string, string> = {
  GJ: 'Chứng từ khác', CLS: 'Kết chuyển', ALC: 'Phân bổ', ADJ: 'Điều chỉnh', REV: 'Đảo', FXR: 'Đánh giá', OPB: 'Số dư đầu',
  CR: 'Phiếu thu', CP: 'Phiếu chi', BR: 'Báo Có', BP: 'Báo Nợ',
  IR: 'Nhập kho', II: 'Xuất kho', IC: 'Tính giá xuất kho', PI: 'Hóa đơn mua', SI: 'Hóa đơn bán',
  DEP: 'Khấu hao / phân bổ', FAD: 'Ghi giảm tài sản', LIA: 'Trích lãi vay',
}
