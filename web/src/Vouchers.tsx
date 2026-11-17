import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { takeParams } from './nav'
import { Attachments } from './Attachments'
import { SearchSelect } from './combo'
import type { AnalysisCode } from './Analysis'
import { askDate, askText } from './dialogs'
import { get, post, put, session, type Voucher } from './api'
import { AccountSelect, CASHFLOW_VI, ErrorBox, PartnerSelect, PERM_VI, PrintSheet, printSheet, STATUS_VI, TYPE_VI, useAction, useCatalog, usePermissions } from './common'
import { addMoney, amountInWords, formatMoney, parseMoney, today } from './money'

interface LineDraft { accountId: string; side: 'D' | 'C'; amount: string; partnerId: string; description: string; cashFlowCode: string; analysisCodeId?: string }
const emptyLine = (side: 'D' | 'C'): LineDraft => ({ accountId: '', side, amount: '', partnerId: '', description: '', cashFlowCode: '', analysisCodeId: '' })
interface Template { id: string; name: string; voucherType: string; description: string
  lines: { accountId: string; side: 'D' | 'C'; amount: string | null; partnerId: string | null; description: string | null; cashFlowCode: string | null; analysisCodeId: string | null }[] }

export function VouchersView() {
  // Opened from a report row (drill-down): show that voucher, with the list narrowed to its posting date.
  const [boot] = useState(() => takeParams('vouchers'))
  const [openId, setOpenId] = useState(boot?.voucherId ?? '')
  // Company in one-person mode (Thiết lập → chế độ phân quyền): save, approve and post in one step.
  const [oneStep, setOneStep] = useState(false)
  // Action buttons follow the user's permissions at this company (the server checks again): a missing one disables the
  // button and says why; busy disables every action so a slow reply cannot be sent twice.
  const perms = usePermissions()
  const gate = (...need: string[]) => {
    const missing = perms ? need.filter(p => !perms.has(p)) : []
    return { disabled: busy || missing.length > 0, title: missing.length ? `Cần quyền ${missing.map(p => PERM_VI[p] ?? p).join(', ')} tại công ty này` : undefined }
  }
  useEffect(() => { get<{ requireDistinctApprover: boolean }>('/company/segregation').then(r => setOneStep(r.requireDistinctApprover === false)).catch(() => setOneStep(false)) }, [])
  const [from, setFrom] = useState(boot?.date ?? boot?.from ?? today().slice(0, 8) + '01')
  const [to, setTo] = useState(boot?.date ?? today())
  const [status, setStatus] = useState(boot?.status ?? '')
  const [type, setType] = useState('')
  const [text, setText] = useState('')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(0)
  const [total, setTotal] = useState(0)
  const [list, setList] = useState<Voucher[]>([])
  const [selected, setSelected] = useState<Voucher | null>(null)
  const [editing, setEditing] = useState<Voucher | 'new' | { copy: FormState } | null>(null)
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const { busy, error, run } = useAction()
  const [info, setInfo] = useState('')

  const load = useCallback(() => run(async () => {
    const q = new URLSearchParams({ from, to, skip: String(page * PAGE), take: String(PAGE), ...(status ? { status } : {}), ...(type ? { type } : {}), ...(search ? { text: search } : {}) })
    const data = await get<{ items: Voucher[]; total: number }>(`/vouchers?${q}`)
    setList(data.items)
    setTotal(data.total)
    setSelected(s => data.items.find(v => v.id === (openId || s?.id)) ?? null)
    setOpenId('')
    setChecked(c => new Set([...c].filter(id => data.items.some(v => v.id === id))))
  }), [from, to, status, type, search, page, run, openId])
  useEffect(() => { load() }, [load])

  const act = (label: string, fn: () => Promise<unknown>) => run(async () => { await fn(); setInfo(label); await load() })
  // Bulk actions on the ticked vouchers: one by one in posting-date order through the same endpoints as a single voucher, so every
  // check still applies (approver ≠ preparer, open period, cash funds not negative…); failures are listed, the rest continue.
  const bulk = (label: string, from: string[], steps: string[]) => run(async () => {
    const todo = list.filter(x => checked.has(x.id) && from.includes(x.status))
      .sort((a, b) => (a.postingDate === b.postingDate ? (a.number < b.number ? -1 : 1) : a.postingDate < b.postingDate ? -1 : 1))
    let done = 0
    const failed: string[] = []
    for (const x of todo) {
      try {
        let rv = x.rowVersion
        for (const s of steps) rv = (await post<Voucher>(`/vouchers/${x.id}/${s}?rowVersion=${rv}`)).rowVersion
        done++
      } catch (e) { failed.push(`${x.number}: ${e instanceof Error ? e.message : String(e)}`) }
    }
    const skipped = checked.size - todo.length
    setInfo(`${label}: ${done} chứng từ.${skipped ? ` Bỏ qua ${skipped} chứng từ không ở trạng thái phù hợp.` : ''}${failed.length ? ` Không làm được ${failed.length}: ${failed.slice(0, 8).join(' | ')}${failed.length > 8 ? ' …' : ''}` : ''}`)
    setChecked(new Set())
    await load()
  })
  const count = (...s: string[]) => list.filter(x => checked.has(x.id) && s.includes(x.status)).length

  if (editing) return <VoucherForm voucher={editing === 'new' || 'copy' in editing ? null : editing} seed={editing !== 'new' && 'copy' in editing ? editing.copy : undefined}
    oneStep={oneStep} onDone={(saved, finish) => {
      setEditing(null)
      if (!saved) { load(); return }
      // Show the saved voucher with the result of the full server-side check; "Lưu và gửi duyệt" submits it when nothing blocks.
      run(async () => {
        if (saved.postingDate < from) setFrom(saved.postingDate)
        if (saved.postingDate > to) setTo(saved.postingDate)
        setOpenId(saved.id)
        const errs = await post<{ code: string; message: string; lineNo: number | null }[]>(`/vouchers/${saved.id}/validate`)
        const text = errs.map(e => `${e.message}`).join(' | ')
        if (finish === 'post') {
          if (errs.length) setInfo(`Đã lưu nháp ${saved.number}, chưa ghi sổ vì cần xem lại: ${text}`)
          else {
            let rv = saved.rowVersion
            for (const s of ['submit', 'approve', 'post']) rv = (await post<Voucher>(`/vouchers/${saved.id}/${s}?rowVersion=${rv}`)).rowVersion
            setInfo(`Đã lưu và ghi sổ ${saved.number}.`)
          }
        } else if (finish === 'submit') {
          await post(`/vouchers/${saved.id}/submit?rowVersion=${saved.rowVersion}`)
          setInfo(`Đã lưu và gửi duyệt ${saved.number}.${text ? ' Lưu ý: ' + text : ''}`)
        } else setInfo(errs.length ? `Đã lưu nháp ${saved.number}. Cần xem lại: ${text}` : `Đã lưu nháp ${saved.number} — hợp lệ, có thể gửi duyệt.`)
        await load()
      })
    }} />

  const v = selected
  return (
    <>
      <div className="toolbar">
        <h2>Chứng từ kế toán</h2>
        <label>Từ ngày<input type="date" value={from} onChange={e => { setFrom(e.target.value); setPage(0) }} /></label>
        <label>Đến ngày<input type="date" value={to} onChange={e => { setTo(e.target.value); setPage(0) }} /></label>
        <label>Trạng thái
          <select value={status} onChange={e => { setStatus(e.target.value); setPage(0) }}>
            <option value="">Tất cả</option>
            {Object.entries(STATUS_VI).map(([k, t]) => <option key={k} value={k}>{t}</option>)}
          </select>
        </label>
        <label>Loại
          <select value={type} onChange={e => { setType(e.target.value); setPage(0) }}>
            <option value="">Tất cả</option>
            {Object.entries(TYPE_VI).map(([k, t]) => <option key={k} value={k}>{t}</option>)}
          </select>
        </label>
        <form onSubmit={e => { e.preventDefault(); setSearch(text.trim()); setPage(0) }} style={{ display: 'contents' }}>
          <label>Tìm<input value={text} onChange={e => setText(e.target.value)} placeholder="số, diễn giải" /></label>
        </form>
        <button onClick={() => load()} disabled={busy}>Tải lại</button>
        <button className="primary" onClick={() => setEditing('new')}>Thêm chứng từ</button>
      </div>
      <ErrorBox error={error} />
      {info && !error && <div className={info.includes('Không làm được') ? 'msg error' : 'msg ok'}>{info}</div>}
      {checked.size > 0 && <div className="toolbar panel">
        <strong>Đã chọn {checked.size} chứng từ</strong>
        <button disabled={busy || !count('Draft')} onClick={() => bulk('Đã gửi duyệt', ['Draft'], ['submit'])}>Gửi duyệt ({count('Draft')})</button>
        <button disabled={busy || !count('Submitted')} onClick={() => bulk('Đã duyệt', ['Submitted'], ['approve'])}>Duyệt ({count('Submitted')})</button>
        <button disabled={busy || !count('Approved')} onClick={() => bulk('Đã ghi sổ', ['Approved'], ['post'])}>Ghi sổ ({count('Approved')})</button>
        <button className="primary" disabled={busy || !count('Submitted')} onClick={() => bulk('Đã duyệt và ghi sổ', ['Submitted'], ['approve', 'post'])}>Duyệt và ghi sổ ({count('Submitted')})</button>
        {oneStep && <button className="primary" disabled={busy || !count('Draft')} onClick={() => bulk('Đã ghi sổ', ['Draft'], ['submit', 'approve', 'post'])}>Ghi sổ ngay ({count('Draft')})</button>}
        <button disabled={busy} onClick={() => setChecked(new Set())}>Bỏ chọn</button>
        <span className="muted">Làm lần lượt theo ngày; chứng từ nào không qua kiểm tra được liệt kê, các chứng từ khác vẫn tiếp tục.</span>
      </div>}
      <div className={v ? 'split' : undefined}>
      {v && (
        <div className="panel detail-pane" aria-label={`Chi tiết chứng từ ${v.number}`}>
          <div className="toolbar">
            <button className="tree-toggle" aria-label="Đóng chi tiết" title="Đóng chi tiết" onClick={() => setSelected(null)}>✕</button>
            <strong>{v.number}</strong> <span className={`badge ${v.status}`}>{STATUS_VI[v.status]}</span>
            <span className="muted">{TYPE_VI[v.voucherType] ?? v.voucherType} · {v.postingDate} · {v.description}</span>
            <span style={{ marginLeft: 'auto' }} />
            {v.status === 'Draft' && !['CR', 'CP', 'BR', 'BP'].includes(v.voucherType) && <button onClick={() => setEditing(v)}>Sửa</button>}
            <button onClick={() => act('Kiểm tra: xem kết quả bên dưới', async () => {
              const errs = await post<{ code: string; message: string; lineNo: number | null }[]>(`/vouchers/${v.id}/validate`)
              setInfo(errs.length ? errs.map(e => `${e.lineNo ? 'Dòng ' + e.lineNo + ': ' : ''}${e.message}`).join(' | ') : 'Hợp lệ, có thể gửi duyệt.')
            })}>Kiểm tra</button>
            {v.status === 'Draft' && <button {...gate('voucher.submit')} onClick={() => act('Đã gửi duyệt', () => post(`/vouchers/${v.id}/submit?rowVersion=${v.rowVersion}`))}>Gửi duyệt</button>}
            {v.status === 'Draft' && oneStep && <button className="primary" {...gate('voucher.submit', 'voucher.approve', 'voucher.post')} onClick={() => act('Đã ghi sổ', async () => {
              let rv = v.rowVersion
              for (const s of ['submit', 'approve', 'post']) rv = (await post<Voucher>(`/vouchers/${v.id}/${s}?rowVersion=${rv}`)).rowVersion
            })}>Ghi sổ ngay</button>}
            {v.status === 'Submitted' && <button {...gate('voucher.approve')} onClick={() => act('Đã duyệt', () => post(`/vouchers/${v.id}/approve?rowVersion=${v.rowVersion}`))}>Duyệt</button>}
            {(v.status === 'Submitted' || v.status === 'Approved') && <button {...gate('voucher.approve')} onClick={async () => {
              const reason = await askText('Lý do trả lại:'); if (reason) act('Đã trả lại', () => post(`/vouchers/${v.id}/reject?rowVersion=${v.rowVersion}`, { reason }))
            }}>Trả lại</button>}
            {v.status === 'Approved' && <button className="primary" {...gate('voucher.post')} onClick={() => act('Đã ghi sổ', () => post(`/vouchers/${v.id}/post?rowVersion=${v.rowVersion}`))}>Ghi sổ</button>}
            {v.status === 'Posted' && <button className="danger" {...gate('voucher.reverse')} onClick={async () => {
              const reason = await askText('Lý do đảo bút toán:'); if (!reason) return
              const date = await askDate('Ngày đảo', v.postingDate); if (date) act('Đã đảo bút toán', () => post(`/vouchers/${v.id}/reverse`, { reversalDate: date, reason }))
            }}>Đảo</button>}
            {(v.status === 'Draft' || v.status === 'Submitted') && <button className="danger" {...gate('voucher.edit_draft')} onClick={async () => {
              const reason = await askText('Lý do hủy:'); if (reason) act('Đã hủy', () => post(`/vouchers/${v.id}/cancel?rowVersion=${v.rowVersion}`, { reason }))
            }}>Hủy</button>}
            {MANUAL_TYPES.includes(v.voucherType) && <button onClick={() => setEditing({ copy: copyOf(v) })} title="Lập chứng từ mới với cùng định khoản, ngày hôm nay">Sao chép</button>}
            <button onClick={printSheet}>In chứng từ</button>
          </div>
          <LinesTable voucher={v} />
          <Attachments key={v.id} voucherId={v.id} />
          <VoucherPrint voucher={v} />
        </div>
      )}
      <div className="scroll list-pane">
        <table className="grid">
          <thead><tr><th><input type="checkbox" aria-label="Chọn tất cả chứng từ trên trang" checked={list.length > 0 && list.every(x => checked.has(x.id))}
            onChange={e => setChecked(e.target.checked ? new Set(list.map(x => x.id)) : new Set())} /></th>
            <th>Ngày HT</th><th>Số</th><th>Loại</th><th>Diễn giải</th><th className="num">Số tiền (đ)</th><th>Trạng thái</th></tr></thead>
          <tbody>
            {list.map(x => (
              <tr key={x.id} className={x.id === v?.id ? 'selected' : ''} onClick={() => setSelected(x)} style={{ cursor: 'pointer' }}>
                <td onClick={e => e.stopPropagation()}><input type="checkbox" aria-label={`Chọn ${x.number}`} checked={checked.has(x.id)}
                  onChange={e => setChecked(c => { const n = new Set(c); if (e.target.checked) n.add(x.id); else n.delete(x.id); return n })} /></td>
                <td>{x.postingDate}</td><td>{x.number}</td><td>{TYPE_VI[x.voucherType] ?? x.voucherType}</td><td>{x.description}</td>
                <td className="num">{formatMoney(x.totalDebit)}</td><td><span className={`badge ${x.status}`}>{STATUS_VI[x.status]}</span></td>
              </tr>
            ))}
            {list.length === 0 && <tr><td colSpan={7} className="muted">Không có chứng từ trong khoảng đã chọn.</td></tr>}
          </tbody>
        </table>
      </div>
      </div>
      <div className="toolbar">
        <span className="muted">{total === 0 ? '' : `Dòng ${page * PAGE + 1}–${page * PAGE + list.length} / ${total}`}</span>
        <button disabled={busy || page === 0} onClick={() => setPage(p => p - 1)}>← Trang trước</button>
        <button disabled={busy || (page + 1) * PAGE >= total} onClick={() => setPage(p => p + 1)}>Trang sau →</button>
      </div>
    </>
  )
}

const PAGE = 200

/** Printed voucher (internal layout — the legal forms of the accounting regime are not reproduced until verified). */
function VoucherPrint({ voucher: v }: { voucher: Voucher }) {
  const { accounts, partners } = useCatalog()
  const [company, setCompany] = useState('')
  const [address, setAddress] = useState('')
  useEffect(() => { get<{ id: string; name: string; address?: string | null }[]>('/me/companies').then(cs => { const c = cs.find(x => x.id === session.companyId); setCompany(c?.name ?? ''); setAddress(c?.address ?? '') }).catch(() => undefined) }, [])
  const acc = (id: string) => accounts.find(a => a.id === id)?.code ?? ''
  const par = (id: string | null) => (id ? partners.find(p => p.id === id) : undefined)
  const d = (s: string) => s.split('-').reverse().join('/')
  // Phiếu thu / phiếu chi: the cash slip layout (payer or payee, reason, amount in words, five signatures).
  if (v.voucherType === 'CR' || v.voucherType === 'CP') {
    const receipt = v.voucherType === 'CR'
    const cashSide = receipt ? 'D' : 'C'
    const isCashAcc = (id: string) => accounts.find(a => a.id === id)?.cashKind === 'Cash'
    const cashLines = v.lines.filter(l => l.side === cashSide && isCashAcc(l.accountId))
    const amount = cashLines.length ? addMoney(cashLines.map(l => l.amount)) : v.totalDebit
    const codes = (side: 'D' | 'C') => [...new Set(v.lines.filter(l => l.side === side).map(l => acc(l.accountId)))].join(', ')
    const person = par(v.lines.find(l => l.partnerId)?.partnerId ?? null)
    const [, m, day] = v.voucherDate.split('-'), y = v.voucherDate.slice(0, 4)
    return (
      <PrintSheet>
        <div style={{ display: 'flex', justifyContent: 'space-between' }}>
          <div><b>{company}</b><br />Địa chỉ: {address || '..............................................'}</div>
          <div style={{ textAlign: 'right' }}>Số: <b>{v.number}</b><br />Nợ: {codes('D')}<br />Có: {codes('C')}</div>
        </div>
        <h1>{receipt ? 'PHIẾU THU' : 'PHIẾU CHI'}</h1>
        <div className="center"><i>Ngày {day} tháng {m} năm {y}</i></div>
        {v.status !== 'Posted' && <div className="center" style={{ marginTop: 6 }}><span className="stamp">{STATUS_VI[v.status]?.toUpperCase()} — CHƯA GHI SỔ</span></div>}
        <p>Họ và tên người {receipt ? 'nộp' : 'nhận'} tiền: <b>{person?.name ?? '..............................................................'}</b><br />
          Địa chỉ: ..........................................................................................<br />
          Lý do {receipt ? 'nộp' : 'chi'}: {v.description}<br />
          Số tiền: <b>{formatMoney(amount)} đồng</b><br />
          Viết bằng chữ: <i>{amountInWords(amount)}</i><br />
          Kèm theo: ............ chứng từ gốc.</p>
        <div className="signs" style={{ gridTemplateColumns: 'repeat(5, 1fr)' }}>
          <div><b>Giám đốc</b><br /><i>(Ký, họ tên, đóng dấu)</i></div><div><b>Kế toán trưởng</b><br /><i>(Ký, họ tên)</i></div>
          <div><b>Người {receipt ? 'nộp' : 'nhận'} tiền</b><br /><i>(Ký, họ tên)</i></div><div><b>Người lập phiếu</b><br /><i>(Ký, họ tên)</i></div><div><b>Thủ quỹ</b><br /><i>(Ký, họ tên)</i></div>
        </div>
        <p>Đã nhận đủ số tiền (viết bằng chữ): ............................................................................................</p>
        <div className="foot">Mẫu in của HAK Accounting, doanh nghiệp tự thiết kế — kế toán trưởng xem lại nội dung trước khi dùng chính thức [CẦN XÁC MINH]. In lúc {new Date().toLocaleString('vi-VN')}.</div>
      </PrintSheet>
    )
  }
  return (
    <PrintSheet>
      <div><b>{company}</b></div>
      <h1>{(TYPE_VI[v.voucherType] ?? 'Chứng từ kế toán').toUpperCase()}</h1>
      <div className="center">Số: <b>{v.number}</b> · Ngày chứng từ: {d(v.voucherDate)} · Ngày ghi sổ: {d(v.postingDate)}</div>
      {v.status !== 'Posted' && <div className="center" style={{ marginTop: 6 }}><span className="stamp">{STATUS_VI[v.status]?.toUpperCase()} — CHƯA GHI SỔ</span></div>}
      <p>Nội dung: {v.description}</p>
      <table>
        <thead><tr><th>STT</th><th>Diễn giải</th><th>TK Nợ</th><th>TK Có</th><th>Đối tượng</th><th>Số tiền</th></tr></thead>
        <tbody>{v.lines.map(l => {
          const p = par(l.partnerId)
          return <tr key={l.lineNo}><td className="center">{l.lineNo}</td><td>{l.description ?? v.description}</td>
            <td>{l.side === 'D' ? acc(l.accountId) : ''}</td><td>{l.side === 'C' ? acc(l.accountId) : ''}</td>
            <td>{p ? `${p.code} – ${p.name}` : ''}</td><td className="num">{formatMoney(l.amount)}</td></tr>
        })}</tbody>
        <tfoot><tr><td colSpan={5}><b>Cộng</b> (Nợ = Có)</td><td className="num"><b>{formatMoney(v.totalDebit)}</b></td></tr></tfoot>
      </table>
      <p>Bằng chữ: <i>{amountInWords(v.totalDebit)}</i></p>
      <div className="signs"><div><b>Người lập</b><br /><i>(Ký, họ tên)</i></div><div><b>Kế toán trưởng</b><br /><i>(Ký, họ tên)</i></div><div><b>Giám đốc</b><br /><i>(Ký, họ tên, đóng dấu)</i></div></div>
      <div className="foot">Mẫu in nội bộ HAK Accounting — chưa đối chiếu với mẫu chứng từ theo chế độ kế toán [CẦN XÁC MINH]. In lúc {new Date().toLocaleString('vi-VN')}.</div>
    </PrintSheet>
  )
}

function LinesTable({ voucher }: { voucher: Voucher }) {
  const { accounts, partners } = useCatalog()
  const acc = (id: string) => accounts.find(a => a.id === id)?.code ?? id.slice(0, 8)
  const par = (id: string | null) => (id ? partners.find(p => p.id === id)?.code ?? '' : '')
  return (
    <table className="grid">
      <thead><tr><th>#</th><th>Tài khoản</th><th>Đối tượng</th><th>Diễn giải</th><th className="num">Nợ</th><th className="num">Có</th></tr></thead>
      <tbody>
        {voucher.lines.map(l => (
          <tr key={l.lineNo}><td>{l.lineNo}</td><td>{acc(l.accountId)}</td><td>{par(l.partnerId)}</td><td>{l.description}</td>
            <td className="num">{l.side === 'D' ? formatMoney(l.amount) : ''}</td><td className="num">{l.side === 'C' ? formatMoney(l.amount) : ''}</td></tr>
        ))}
      </tbody>
      <tfoot><tr><td colSpan={4}>Cộng</td><td className="num">{formatMoney(voucher.totalDebit)}</td><td className="num">{formatMoney(voucher.totalCredit)}</td></tr></tfoot>
    </table>
  )
}

interface FormState { type: string; voucherDate: string; postingDate: string; description: string; lines: LineDraft[] }
interface StoredDraft { state: FormState; rowVersion: number | null; savedAt: string }
const MANUAL_TYPES = ['GJ', 'ADJ', 'ALC', 'OPB', 'CLS', 'FXR']

const fromVoucher = (v: Voucher): FormState => ({
  type: v.voucherType, voucherDate: v.voucherDate, postingDate: v.postingDate, description: v.description,
  lines: v.lines.map(l => ({ accountId: l.accountId, side: l.side, amount: formatMoney(l.amount), partnerId: l.partnerId ?? '', description: l.description ?? '', cashFlowCode: l.cashFlowCode ?? '', analysisCodeId: l.analysisCodeId ?? '' })),
})
/** A new voucher with the lines of an existing one, dated today ("Sao chép"). */
const copyOf = (v: Voucher): FormState => ({ ...fromVoucher(v), type: MANUAL_TYPES.includes(v.voucherType) ? v.voucherType : 'GJ', voucherDate: today(), postingDate: today() })

// What is being typed is kept in this browser until it is saved, so Esc, a closed tab or a power cut does not lose it.
const draftKey = (voucherId?: string) => `hak.draft.${session.companyId}.${voucherId ?? 'new'}`
function readDraft(key: string, rowVersion: number | null): StoredDraft | null {
  try {
    const d = JSON.parse(localStorage.getItem(key) ?? 'null') as StoredDraft | null
    return d && d.rowVersion === rowVersion && Array.isArray(d.state?.lines) ? d : null   // a voucher changed since then: the draft is stale
  } catch { return null }
}
function writeDraft(key: string, d: StoredDraft | null) {
  try { if (d) localStorage.setItem(key, JSON.stringify(d)); else localStorage.removeItem(key) } catch { /* storage unavailable: nothing kept */ }
}

interface PeriodInfo { startDate: string; endDate: string; status: 'Open' | 'Locked' }

/** save: draft only; submit: send for approval; post: submit, approve and post at once (company in one-person mode). */
export type Finish = 'save' | 'submit' | 'post'

function VoucherForm({ voucher, seed, onDone, oneStep }: { voucher: Voucher | null; seed?: FormState; onDone: (saved?: Voucher, finish?: Finish) => void; oneStep?: boolean }) {
  const { accounts, partners } = useCatalog()
  const key = draftKey(voucher?.id)
  const [initial] = useState<FormState>(() => (voucher ? fromVoucher(voucher)
    : seed ?? { type: 'GJ', voucherDate: today(), postingDate: today(), description: '', lines: [emptyLine('D'), emptyLine('C')] }))
  const [kept] = useState(() => (seed ? null : readDraft(key, voucher?.rowVersion ?? null)))
  const [restoredAt, setRestoredAt] = useState(kept?.savedAt ?? null)
  const start = kept?.state ?? initial
  const [type, setType] = useState(start.type)
  const [voucherDate, setVoucherDate] = useState(start.voucherDate)
  const [postingDate, setPostingDate] = useState(start.postingDate)
  const [description, setDescription] = useState(start.description)
  const [lines, setLines] = useState<LineDraft[]>(start.lines)
  const [periods, setPeriods] = useState<PeriodInfo[]>([])
  const [codes, setCodes] = useState<AnalysisCode[]>([])
  const [templates, setTemplates] = useState<Template[]>([])
  const [note, setNote] = useState('')
  const loadTemplates = () => get<Template[]>('/voucher-templates').then(setTemplates).catch(() => setTemplates([]))
  useEffect(() => { loadTemplates(); get<AnalysisCode[]>('/analysis-codes').then(setCodes).catch(() => setCodes([])) }, [])
  const codeOptions = useMemo(() => codes.filter(c => c.isActive).map(c => ({ id: c.id, code: c.code, name: `${c.name} (${c.kind})` })), [codes])
  const showCodes = codeOptions.length > 0 || lines.some(l => l.analysisCodeId)
  // Định khoản mẫu: the lines of a saved template replace what is on the form; amounts left empty in the template are typed now.
  const applyTemplate = (t: Template) => {
    setType(t.voucherType)
    if (t.description) setDescription(t.description)
    setLines(t.lines.map(l => ({ accountId: l.accountId, side: l.side, amount: l.amount ? formatMoney(l.amount) : '', partnerId: l.partnerId ?? '',
      description: l.description ?? '', cashFlowCode: l.cashFlowCode ?? '', analysisCodeId: l.analysisCodeId ?? '' })))
    setNote(`Đã áp dụng mẫu "${t.name}". Kiểm tra số tiền, ngày rồi lưu.`)
  }
  const saveTemplate = async () => {
    const name = await askText('Tên định khoản mẫu (trùng tên sẽ thay mẫu cũ):', description)
    if (!name) return
    run(async () => {
      if (lines.some(l => !l.accountId)) throw new Error('Mỗi dòng của mẫu phải có tài khoản.')
      await post('/voucher-templates', { name, voucherType: type, description, lines: lines.map(l => ({ accountId: l.accountId, side: l.side, amount: parseMoney(l.amount),
        partnerId: l.partnerId || null, description: l.description || null, cashFlowCode: l.cashFlowCode || null, analysisCodeId: l.analysisCodeId || null })) })
      await loadTemplates()
      setNote(`Đã lưu mẫu "${name}".`)
    })
  }
  const { busy, error, setError, run } = useAction()
  const panel = useRef<HTMLDivElement>(null)
  const focusNewLine = useRef(false)

  useEffect(() => { get<PeriodInfo[]>('/periods').then(setPeriods).catch(() => setPeriods([])) }, [])
  useEffect(() => {
    const state: FormState = { type, voucherDate, postingDate, description, lines }
    const dirty = JSON.stringify(state) !== JSON.stringify(initial)
    writeDraft(key, dirty ? { state, rowVersion: voucher?.rowVersion ?? null, savedAt: new Date().toISOString() } : null)
  }, [type, voucherDate, postingDate, description, lines, initial, key, voucher])
  const discard = () => {
    writeDraft(key, null); setRestoredAt(null)
    setType(initial.type); setVoucherDate(initial.voucherDate); setPostingDate(initial.postingDate); setDescription(initial.description); setLines(initial.lines)
  }

  const parsed = lines.map(l => parseMoney(l.amount))
  const totalD = useMemo(() => addMoney(lines.map((l, i) => (l.side === 'D' ? parsed[i] ?? '0' : '0'))), [lines, parsed])
  const totalC = useMemo(() => addMoney(lines.map((l, i) => (l.side === 'C' ? parsed[i] ?? '0' : '0'))), [lines, parsed])
  const diff = addMoney([totalD, '-' + totalC])

  const update = (i: number, patch: Partial<LineDraft>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)))
  const isCash = (id: string) => { const a = accounts.find(x => x.id === id); return !!a && (a.cashKind !== 'None' || (a.reportTags ?? '').split(',').includes('TDT')) }
  // The next line balances the voucher: opposite side, the amount still missing, the description of the line above.
  const nextLine = (ls: LineDraft[]): LineDraft => {
    const last = ls.at(-1)
    const d = addMoney(ls.map(l => { const p = parseMoney(l.amount) ?? '0'; return l.side === 'D' ? p : '-' + p }))
    const side: 'D' | 'C' = d === '0' ? (last?.side === 'D' ? 'C' : 'D') : d.startsWith('-') ? 'D' : 'C'
    return { ...emptyLine(side), amount: d === '0' ? '' : formatMoney(d.replace('-', '')), description: last?.description ?? '' }
  }
  const addLine = () => { focusNewLine.current = true; setLines(ls => [...ls, nextLine(ls)]) }
  useEffect(() => {
    if (!focusNewLine.current) return
    focusNewLine.current = false
    panel.current?.querySelector<HTMLInputElement>('tbody tr:last-child input[role=combobox]')?.focus()
  }, [lines.length])

  // Checked while typing (the server checks again when the voucher is saved, submitted and posted).
  const warnings: string[] = []
  const period = periods.find(p => p.startDate <= postingDate && postingDate <= p.endDate)
  if (periods.length > 0 && !period) warnings.push(`Chưa có kỳ kế toán chứa ngày hạch toán ${postingDate}.`)
  else if (period?.status === 'Locked') warnings.push(`Kỳ kế toán chứa ngày ${postingDate} đã khóa sổ — không ghi sổ được chứng từ này.`)
  lines.forEach((l, i) => {
    const a = accounts.find(x => x.id === l.accountId)
    if (a?.requiresPartner && !l.partnerId) warnings.push(`Dòng ${i + 1}: TK ${a.code} bắt buộc chọn đối tượng.`)
    if (l.amount !== '' && parsed[i] == null) warnings.push(`Dòng ${i + 1}: số tiền không hợp lệ.`)
  })

  const save = (finish: Finish = 'save') => run(async () => {
    const bad = parsed.findIndex(p => p == null)
    if (bad >= 0) { setError(new Error(`Dòng ${bad + 1}: số tiền không hợp lệ.`)); return }
    const body = {
      voucherType: type, voucherDate, postingDate, description,
      lines: lines.map((l, i) => ({ accountId: l.accountId, side: l.side, amount: parsed[i], partnerId: l.partnerId || null, description: l.description || null,
        cashFlowCode: isCash(l.accountId) ? l.cashFlowCode || null : null, analysisCodeId: l.analysisCodeId || null })),
    }
    const saved = voucher ? await put<Voucher>(`/vouchers/${voucher.id}?rowVersion=${voucher.rowVersion}`, body) : await post<Voucher>('/vouchers', body)
    writeDraft(key, null)
    onDone(saved, finish)
  })

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (document.querySelector('.modal-backdrop')) return   // a dialog is open on top of the form
      if (e.ctrlKey && e.key.toLowerCase() === 's') { e.preventDefault(); save() }
      if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); save(oneStep ? 'post' : 'submit') }
      if (e.key === 'Insert') { e.preventDefault(); addLine() }
      if (e.key === 'Escape') onDone()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })
  // Enter moves to the next field; on the last field of the last line it adds the balancing line.
  const onEnter = (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter' || e.ctrlKey || e.altKey || e.shiftKey || e.defaultPrevented) return
    const t = e.target as HTMLElement
    if (!(t instanceof HTMLInputElement || t instanceof HTMLSelectElement)) return
    e.preventDefault()
    const fields = [...(panel.current?.querySelectorAll<HTMLElement>('input, select') ?? [])]
    const next = fields[fields.indexOf(t) + 1]
    if (next) next.focus(); else addLine()
  }

  return (
    <>
      <div className="toolbar">
        <h2>{voucher ? `Sửa chứng từ ${voucher.number}` : 'Chứng từ mới'}</h2>
        <span className="muted"><kbd>Enter</kbd> ô kế · <kbd>Insert</kbd> thêm dòng · <kbd>Ctrl+S</kbd> lưu · <kbd>Ctrl+Enter</kbd> lưu và gửi duyệt · <kbd>Esc</kbd> đóng</span>
        <button onClick={() => onDone()}>Đóng</button>
        <button onClick={() => save()} disabled={busy}>Lưu nháp</button>
        {oneStep
          ? <button className="primary" onClick={() => save('post')} disabled={busy} title="Lưu, duyệt và ghi sổ ngay (Ctrl+Enter)">Lưu và ghi sổ</button>
          : <button className="primary" onClick={() => save('submit')} disabled={busy}>Lưu và gửi duyệt</button>}
      </div>
      <ErrorBox error={error} />
      {note && !error && <div className="msg ok">{note}</div>}
      {restoredAt && <div className="msg ok">Đã khôi phục nội dung đang nhập dở lúc {new Date(restoredAt).toLocaleString('vi-VN')} (chưa lưu vào hệ thống).
        {' '}<button onClick={discard}>Bỏ, nhập lại từ đầu</button></div>}
      <div className="panel" ref={panel} onKeyDown={onEnter}>
        <div className="form-row">
          <label>Loại
            <select value={type} onChange={e => setType(e.target.value)} disabled={!!voucher}>
              {MANUAL_TYPES.map(t => <option key={t} value={t}>{t} – {TYPE_VI[t]}</option>)}
            </select>
          </label>
          <label>Ngày chứng từ<input type="date" value={voucherDate} onChange={e => { setVoucherDate(e.target.value); if (postingDate === voucherDate) setPostingDate(e.target.value) }} /></label>
          <label>Ngày hạch toán<input type="date" value={postingDate} onChange={e => setPostingDate(e.target.value)} /></label>
          <label style={{ flex: 1, minWidth: 240 }}>Diễn giải<input value={description} onChange={e => setDescription(e.target.value)} /></label>
          {!voucher && <label>Định khoản mẫu
            <select value="" onChange={e => { const t = templates.find(x => x.id === e.target.value); if (t) applyTemplate(t) }}>
              <option value="">{templates.length ? '— chọn mẫu —' : '(chưa có mẫu)'}</option>
              {templates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select></label>}
        </div>
        <table className="grid">
          <thead><tr><th>#</th><th>Nợ/Có</th><th>Tài khoản</th><th>Đối tượng</th><th className="num">Số tiền</th><th>Diễn giải dòng</th>
            {showCodes && <th title="Mã quản trị: khoản mục phí, bộ phận, hợp đồng, chuyến tàu…">Mã phân tích</th>}
            <th title="Mã số trên Báo cáo lưu chuyển tiền tệ (B03-DN). Để trống: phần mềm tự xác định theo TK đối ứng">Mã LCTT</th><th /></tr></thead>
          <tbody>
            {lines.map((l, i) => (
              <tr key={i}>
                <td>{i + 1}</td>
                <td><select value={l.side} onChange={e => update(i, { side: e.target.value as 'D' | 'C' })} aria-label="Nợ/Có"><option value="D">Nợ</option><option value="C">Có</option></select></td>
                <td><AccountSelect accounts={accounts} value={l.accountId} onChange={id => update(i, { accountId: id })} ariaLabel="Tài khoản" /></td>
                <td><PartnerSelect partners={partners} value={l.partnerId} onChange={id => update(i, { partnerId: id })} /></td>
                <td><input className="num" value={l.amount} aria-label="Số tiền" aria-invalid={l.amount !== '' && parsed[i] == null}
                  onChange={e => update(i, { amount: e.target.value })}
                  onBlur={() => { const p = parseMoney(l.amount); if (p) update(i, { amount: formatMoney(p) }) }} /></td>
                <td><input value={l.description} placeholder={description} onChange={e => update(i, { description: e.target.value })} aria-label="Diễn giải dòng" /></td>
                {showCodes && <td><SearchSelect options={codeOptions} value={l.analysisCodeId ?? ''} onChange={id => update(i, { analysisCodeId: id })} ariaLabel="Mã phân tích" placeholder="—" width={150} /></td>}
                <td>{isCash(l.accountId) && <select value={l.cashFlowCode} onChange={e => update(i, { cashFlowCode: e.target.value })} aria-label="Mã luồng tiền">
                  <option value="">Tự động</option>{Object.entries(CASHFLOW_VI).map(([k, v]) => <option key={k} value={k}>{k} – {v}</option>)}</select>}</td>
                <td><button tabIndex={-1} onClick={() => setLines(ls => ls.filter((_, j) => j !== i))} aria-label="Xóa dòng">✕</button></td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr><td colSpan={4}>Tổng Nợ / Tổng Có</td><td className="num">{formatMoney(totalD)} / {formatMoney(totalC)}</td>
              <td colSpan={showCodes ? 4 : 3} style={{ color: diff === '0' ? 'var(--ok)' : 'var(--danger)' }}>{diff === '0' ? 'Cân bằng' : `Chênh lệch ${formatMoney(diff)}`}</td></tr>
          </tfoot>
        </table>
        <button style={{ marginTop: 8 }} onClick={addLine}>+ Thêm dòng</button>
        {' '}<button style={{ marginTop: 8 }} onClick={saveTemplate} disabled={busy} title="Lưu các dòng đang nhập thành định khoản mẫu dùng lại sau">Lưu làm mẫu…</button>
        {warnings.length > 0 && <ul className="field-warn">{warnings.map(w => <li key={w}>{w}</li>)}</ul>}
      </div>
    </>
  )
}
