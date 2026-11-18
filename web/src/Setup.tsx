import { useCallback, useEffect, useState } from 'react'
import { AnalysisSetup } from './Analysis'
import { get, post, put, type Account } from './api'
import { AccountSelect, ErrorBox, Modal, useAction } from './common'
import { ImportData } from './ImportData'
import { RulesSetup } from './Rules'
import { DateInput } from './DateInput'

const TABS = { accounts: 'Hệ thống tài khoản', roles: 'Vai trò tài khoản', rules: 'Quy tắc định khoản', stock: 'Kho & vật tư', years: 'Năm tài chính', analysis: 'Mẫu, mã phân tích, hạn mức duyệt', import: 'Nhập từ Excel/CSV' } as const

/** Thiết lập công ty: catalogue the modules depend on. Codes are the company's own (legal chart [CẦN XÁC MINH TT99]). */
export function SetupView() {
  const [tab, setTab] = useState<keyof typeof TABS>('accounts')
  return (
    <>
      <div className="toolbar"><h2>Thiết lập</h2>
        {Object.entries(TABS).map(([k, v]) => <button key={k} className={tab === k ? 'primary' : ''} onClick={() => setTab(k as keyof typeof TABS)}>{v}</button>)}
      </div>
      {tab === 'accounts' && <Accounts />}
      {tab === 'roles' && <Roles />}
      {tab === 'rules' && <RulesSetup />}
      {tab === 'stock' && <Stock />}
      {tab === 'years' && <Years />}
      {tab === 'analysis' && <AnalysisSetup />}
      {tab === 'import' && <ImportData />}
    </>
  )
}

const NATURE_VI: Record<string, string> = { None: '', Receivable: 'Phải thu', Payable: 'Phải trả' }
const CASH_VI: Record<string, string> = { None: '', Cash: 'Tiền mặt', Bank: 'Tiền gửi', InTransit: 'Tiền đang chuyển' }

const NEG_VI: Record<string, string> = { None: 'Cho phép', Warn: 'Cảnh báo', Block: 'Chặn' }
interface Attrs { requiresPartner: boolean; openItemNature: string; cashKind: string; negativeBalancePolicy: string }
type Dialog = { kind: 'new' } | { kind: 'child'; parent: Account } | { kind: 'edit'; account: Account } | null

/** Thuộc tính quản lý của TK chi tiết (dùng chung cho thêm / sửa). */
function AttrFields({ v, set, locked }: { v: Attrs; set: (f: (a: Attrs) => Attrs) => void; locked?: boolean }) {
  return <div className="form-row">
    <label className="check"><input type="checkbox" disabled={locked} checked={v.requiresPartner} onChange={e => set(a => ({ ...a, requiresPartner: e.target.checked }))} /> Bắt buộc đối tượng</label>
    <label>Công nợ theo chứng từ <select disabled={locked} value={v.openItemNature} onChange={e => set(a => ({ ...a, openItemNature: e.target.value, requiresPartner: e.target.value !== 'None' || a.requiresPartner }))}>
      <option value="None">Không</option><option value="Receivable">Phải thu</option><option value="Payable">Phải trả</option></select></label>
    <label>Loại tiền <select disabled={locked} value={v.cashKind} onChange={e => set(a => ({ ...a, cashKind: e.target.value }))}>
      <option value="None">Không</option><option value="Cash">Tiền mặt</option><option value="Bank">Tiền gửi</option><option value="InTransit">Tiền đang chuyển</option></select></label>
    <label>Số dư âm <select value={v.negativeBalancePolicy} onChange={e => set(a => ({ ...a, negativeBalancePolicy: e.target.value }))}>
      {Object.entries(NEG_VI).map(([k, t]) => <option key={k} value={k}>{t}</option>)}</select></label>
  </div>
}

const attrsOf = (a: Account): Attrs => ({ requiresPartner: a.requiresPartner, openItemNature: a.openItemNature, cashKind: a.cashKind, negativeBalancePolicy: a.negativeBalancePolicy ?? 'None' })

/** Hệ thống tài khoản dạng cây cha – con; thêm TK cấp 1, thêm TK chi tiết và sửa TK trong cửa sổ modal. */
function Accounts() {
  const [list, setList] = useState<Account[]>([])
  const [open, setOpen] = useState<Set<string>>(new Set())
  const [filter, setFilter] = useState('')
  const [dialog, setDialog] = useState<Dialog>(null)
  const [info, setInfo] = useState('')
  const { error, run } = useAction()
  const load = useCallback(() => run(async () => setList(await get<Account[]>('/accounts'))), [run])
  useEffect(() => { load() }, [load])

  const byId = Object.fromEntries(list.map(a => [a.id, a]))
  const children: Record<string, Account[]> = {}
  for (const a of [...list].sort((x, y) => (x.code < y.code ? -1 : 1))) (children[a.parentId && byId[a.parentId] ? a.parentId : ''] ??= []).push(a)
  // With a search: matching accounts plus their ancestors, all expanded.
  const q = filter.trim().toLowerCase()
  const visible = new Set<string>()
  if (q) for (const a of list) if (`${a.code} ${a.name}`.toLowerCase().includes(q)) for (let p: Account | undefined = a; p; p = p.parentId ? byId[p.parentId] : undefined) visible.add(p.id)
  const rows: { a: Account; depth: number }[] = []
  const walk = (parent: string, depth: number) => {
    for (const a of children[parent] ?? []) {
      if (q && !visible.has(a.id)) continue
      rows.push({ a, depth })
      if (q || open.has(a.id)) walk(a.id, depth + 1)
    }
  }
  walk('', 0)
  const toggle = (id: string) => setOpen(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  const done = async (msg: string, expand?: string) => { setDialog(null); setInfo(msg); if (expand) setOpen(s => new Set(s).add(expand)); await load() }

  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="toolbar">
        <button className="primary" onClick={() => setDialog({ kind: 'new' })}>Thêm TK cấp 1</button>
        <label>Tìm <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="mã, tên" /></label>
        <button onClick={() => setOpen(new Set(list.filter(a => children[a.id]).map(a => a.id)))}>Mở hết</button>
        <button onClick={() => setOpen(new Set())}>Thu gọn</button>
        <span className="muted">{list.length} tài khoản · TK in đậm là TK tổng hợp</span>
      </div>
      <div className="scroll"><table className="grid">
        <thead><tr><th>Mã</th><th>Tên</th><th>Loại</th><th title="Nhãn dùng trong công thức BCTC (DH, TDT, HCSD, LV, DT, KH…)">Nhãn BC</th><th>Đang dùng</th><th /></tr></thead>
        <tbody>{rows.map(({ a, depth }) => {
          const kids = children[a.id]?.length ?? 0
          return <tr key={a.id} style={{ fontWeight: a.isPostable ? undefined : 600, opacity: a.isActive ? undefined : 0.55 }}>
            <td style={{ paddingLeft: 4 + 18 * depth, whiteSpace: 'nowrap' }}>
              {kids > 0 ? <button className="tree-toggle" aria-label={`${open.has(a.id) || q ? 'Thu' : 'Mở'} ${a.code}`} onClick={() => toggle(a.id)}>{open.has(a.id) || q ? '▾' : '▸'}</button>
                : <span className="tree-toggle" style={{ display: 'inline-block' }} />}
              {a.code}</td>
            <td>{a.name}{kids > 0 && <span className="muted"> ({kids})</span>}</td>
            <td>{[a.isPostable ? '' : 'Tổng hợp', NATURE_VI[a.openItemNature], CASH_VI[a.cashKind], a.requiresPartner ? 'Theo đối tượng' : ''].filter(Boolean).join(' · ')}</td>
            <td>{a.reportTags}</td>
            <td>{a.isActive ? '✓' : 'Ngừng'}</td>
            <td style={{ whiteSpace: 'nowrap' }}>
              <button onClick={() => setDialog({ kind: 'edit', account: a })} aria-label={`Sửa TK ${a.code}`}>Sửa</button>{' '}
              {a.isActive && <button onClick={() => setDialog({ kind: 'child', parent: a })} aria-label={`Thêm TK con của ${a.code}`} title={a.isPostable ? 'TK này sẽ chuyển thành TK tổng hợp (chỉ khi chưa phát sinh)' : ''}>+ TK con</button>}
            </td></tr>
        })}</tbody></table></div>
      {list.length > 0 && rows.length === 0 && <p className="muted">Không có tài khoản phù hợp.</p>}
      {dialog?.kind === 'new' && <NewAccountDialog list={list} onClose={() => setDialog(null)} onDone={(c, p) => done(`Đã thêm TK ${c}.`, p)} />}
      {dialog?.kind === 'child' && <ChildAccountDialog parent={dialog.parent} onClose={() => setDialog(null)} onDone={c => done(`Đã thêm TK ${c} dưới ${dialog.parent.code}.`, dialog.parent.id)} />}
      {dialog?.kind === 'edit' && <EditAccountDialog account={dialog.account} list={list} onClose={() => setDialog(null)} onDone={() => done(`Đã cập nhật TK ${dialog.account.code}.`)} />}
    </>
  )
}

function NewAccountDialog({ list, onClose, onDone }: { list: Account[]; onClose: () => void; onDone: (code: string, parentId?: string) => void }) {
  const [v, setV] = useState({ code: '', name: '', parentCode: '', isPostable: true })
  const [attrs, setAttrs] = useState<Attrs>({ requiresPartner: false, openItemNature: 'None', cashKind: 'None', negativeBalancePolicy: 'None' })
  const { busy, error, run } = useAction()
  const save = () => run(async () => {
    await post('/accounts', { ...v, ...attrs, parentCode: v.parentCode || null, balanceDisplay: attrs.openItemNature === 'None' ? 'Net' : 'GrossByPartner' })
    onDone(v.code, list.find(a => a.code === v.parentCode)?.id)
  })
  return <Modal title="Thêm tài khoản" onClose={onClose} busy={busy} onSubmit={v.code && v.name ? save : undefined}
    footer={<><button onClick={onClose} disabled={busy}>Hủy</button><button className="primary" disabled={busy || !v.code || !v.name} onClick={save}>Thêm</button></>}>
    <ErrorBox error={error} />
    <div className="form-row">
      <label>Mã <input value={v.code} onChange={e => setV(x => ({ ...x, code: e.target.value.toUpperCase() }))} /></label>
      <label style={{ flex: 1 }}>Tên <input value={v.name} onChange={e => setV(x => ({ ...x, name: e.target.value }))} /></label>
    </div>
    <div className="form-row">
      <label>TK cấp trên <select value={v.parentCode} onChange={e => setV(x => ({ ...x, parentCode: e.target.value }))}><option value="">— (cấp 1)</option>
        {list.filter(a => !a.isPostable && a.isActive).map(a => <option key={a.id} value={a.code}>{a.code} – {a.name}</option>)}</select></label>
      <label className="check"><input type="checkbox" checked={v.isPostable} onChange={e => setV(x => ({ ...x, isPostable: e.target.checked }))} /> Hạch toán trực tiếp (TK chi tiết)</label>
    </div>
    {v.isPostable && <AttrFields v={attrs} set={setAttrs} />}
  </Modal>
}

function ChildAccountDialog({ parent, onClose, onDone }: { parent: Account; onClose: () => void; onDone: (code: string) => void }) {
  const [code, setCode] = useState(parent.code)
  const [name, setName] = useState('')
  const [inherit, setInherit] = useState(true)
  const [attrs, setAttrs] = useState<Attrs>(attrsOf(parent))
  const [tags, setTags] = useState(parent.reportTags ?? '')
  const { busy, error, run } = useAction()
  const save = () => run(async () => {
    await post(`/accounts/${parent.id}/children`, { code, name, ...(inherit ? {} : { ...attrs, reportTags: tags }) })
    onDone(code)
  })
  return <Modal title={`Thêm TK chi tiết của ${parent.code} – ${parent.name}`} onClose={onClose} busy={busy} onSubmit={code.length > parent.code.length && name ? save : undefined}
    footer={<><button onClick={onClose} disabled={busy}>Hủy</button><button className="primary" disabled={busy || code.length <= parent.code.length || !name} onClick={save}>Thêm</button></>}>
    <ErrorBox error={error} />
    {parent.isPostable && <div className="msg">TK {parent.code} đang là TK chi tiết: khi thêm TK con, {parent.code} chuyển thành <b>TK tổng hợp</b> và không hạch toán trực tiếp nữa.
      Chỉ làm được khi {parent.code} chưa có bút toán, chứng từ, ánh xạ vai trò hay liên kết phân hệ.</div>}
    <div className="form-row">
      <label>Mã (bắt đầu bằng {parent.code}) <input value={code} onChange={e => setCode(e.target.value.toUpperCase())} /></label>
      <label style={{ flex: 1 }}>Tên <input value={name} onChange={e => setName(e.target.value)} /></label>
    </div>
    <label className="check"><input type="checkbox" checked={inherit} onChange={e => setInherit(e.target.checked)} />
      Kế thừa thuộc tính và nhãn BC của {parent.code}{parent.reportTags ? ` (${parent.reportTags})` : ''}</label>
    {!inherit && <>
      <AttrFields v={attrs} set={setAttrs} />
      <label>Nhãn BC <input value={tags} onChange={e => setTags(e.target.value)} placeholder="vd DH, TDT" /></label>
    </>}
  </Modal>
}

function EditAccountDialog({ account: a, list, onClose, onDone }: { account: Account; list: Account[]; onClose: () => void; onDone: () => void }) {
  const byId = Object.fromEntries(list.map(x => [x.id, x]))
  const [name, setName] = useState(a.name)
  const [active, setActive] = useState(a.isActive)
  const [tags, setTags] = useState(a.reportTags ?? '')
  const [parent, setParent] = useState(a.parentId ? byId[a.parentId]?.code ?? '' : '')
  const [attrs, setAttrs] = useState<Attrs>(attrsOf(a))
  const [bankNo, setBankNo] = useState(a.bankAccountNo ?? '')
  const [bankName, setBankName] = useState(a.bankName ?? '')
  const { busy, error, run } = useAction()
  // A summary account cannot sit under itself or its own descendants.
  const descendants = new Set<string>([a.id])
  for (let grew = true; grew;) { grew = false; for (const x of list) if (x.parentId && descendants.has(x.parentId) && !descendants.has(x.id)) { descendants.add(x.id); grew = true } }
  const save = () => run(async () => {
    if (name !== a.name || active !== a.isActive) await put(`/accounts/${a.id}`, { name, isActive: active })
    if (tags.trim() !== (a.reportTags ?? '')) await post(`/accounts/${a.id}/tags`, { tags: tags.trim() || null })
    if (parent !== (a.parentId ? byId[a.parentId]?.code ?? '' : '')) await post(`/accounts/${a.id}/parent`, { parentCode: parent || null })
    const o = attrsOf(a)
    if (a.isPostable && JSON.stringify(o) !== JSON.stringify(attrs)) await post(`/accounts/${a.id}/attributes`, attrs)
    if (attrs.cashKind === 'Bank' && (bankNo.trim() !== (a.bankAccountNo ?? '') || bankName.trim() !== (a.bankName ?? '')))
      await put(`/accounts/${a.id}/bank-info`, { bankAccountNo: bankNo.trim() || null, bankName: bankName.trim() || null })
    onDone()
  })
  return <Modal title={`Sửa tài khoản ${a.code}`} onClose={onClose} busy={busy} onSubmit={name.trim() ? save : undefined}
    footer={<><button onClick={onClose} disabled={busy}>Hủy</button><button className="primary" disabled={busy || !name.trim()} onClick={save}>Lưu</button></>}>
    <ErrorBox error={error} />
    <div className="form-row">
      <label>Mã <input value={a.code} disabled /></label>
      <label style={{ flex: 1 }}>Tên <input value={name} onChange={e => setName(e.target.value)} /></label>
    </div>
    <div className="form-row">
      <label>TK cấp trên <select value={parent} onChange={e => setParent(e.target.value)}><option value="">— (cấp 1)</option>
        {list.filter(x => !x.isPostable && x.isActive && !descendants.has(x.id)).map(x => <option key={x.id} value={x.code}>{x.code} – {x.name}</option>)}</select></label>
      <label>Nhãn BC <input value={tags} onChange={e => setTags(e.target.value)} placeholder="vd DH, TDT" /></label>
      <label className="check"><input type="checkbox" checked={active} onChange={e => setActive(e.target.checked)} /> Đang dùng</label>
    </div>
    {a.isPostable && attrs.cashKind === 'Bank' && <div className="form-row">
      <label>Số tài khoản tại ngân hàng <input value={bankNo} onChange={e => setBankNo(e.target.value)} /></label>
      <label style={{ flex: 1 }}>Tên ngân hàng, chi nhánh (in trên ủy nhiệm chi) <input value={bankName} onChange={e => setBankName(e.target.value)} placeholder="Vietcombank – CN …" /></label>
    </div>}
    {a.isPostable
      ? <><AttrFields v={attrs} set={setAttrs} /><p className="muted">Đổi cấp trên chỉ đổi cách cộng dồn báo cáo, không đổi bút toán. Đối tượng, công nợ, loại tiền chỉ đổi được khi TK chưa có chứng từ.</p></>
      : <p className="muted">TK tổng hợp: thuộc tính hạch toán đặt ở các TK chi tiết.</p>}
  </Modal>
}

interface RoleRow { code: string; module: string; description: string; optional: boolean; account: string | null }

function Roles() {
  const [roles, setRoles] = useState<RoleRow[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [pick, setPick] = useState<Record<string, string>>({})
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => { setRoles(await get<RoleRow[]>('/rules/account-roles')); setAccounts(await get<Account[]>('/accounts')) }), [run])
  useEffect(() => { load() }, [load])
  const missing = roles.filter(r => !r.account && !r.optional).length
  return (
    <>
      <p className="muted">Các phân hệ hạch toán qua <strong>vai trò</strong>, không gán cứng số hiệu tài khoản. Kế toán trưởng ánh xạ mỗi vai trò tới một TK chi tiết của công ty theo chế độ kế toán áp dụng. Thiếu ánh xạ thì phân hệ đó từ chối ghi sổ (RULE_ROLE_UNMAPPED).</p>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      {missing > 0 && <div className="msg error">{missing} vai trò bắt buộc chưa ánh xạ — chỉ cần ánh xạ vai trò của các phân hệ công ty sử dụng.</div>}
      <table className="grid"><thead><tr><th>Phân hệ</th><th>Vai trò</th><th>Ý nghĩa</th><th>Đang ánh xạ</th><th>Đổi thành</th><th /></tr></thead>
        <tbody>{roles.map(r => <tr key={r.code}><td>{r.module}</td><td>{r.code}</td><td style={{ whiteSpace: 'normal' }}>{r.description}</td>
          <td>{r.account ?? <span className="muted">{r.optional ? 'tùy chọn (dùng vai trò chung)' : 'chưa ánh xạ'}</span>}</td>
          <td><AccountSelect accounts={accounts} value={pick[r.code] ?? ''} onChange={v => setPick(p => ({ ...p, [r.code]: v }))} ariaLabel={`TK cho ${r.code}`} /></td>
          <td>{pick[r.code] && <button disabled={busy} onClick={() => run(async () => {
            const code = accounts.find(a => a.id === pick[r.code])?.code
            await post('/rules/role-mappings', { roleCode: r.code, accountCode: code }); setInfo(`Đã ánh xạ ${r.code} → ${code}.`)
            setPick(p => { const n = { ...p }; delete n[r.code]; return n }); await load()
          })}>Lưu</button>}</td></tr>)}</tbody></table>
    </>
  )
}

interface Warehouse { id: string; code: string; name: string; isActive: boolean; policies: { method: string; effectiveFrom: string; note: string }[] }
interface Item { id: string; code: string; name: string; unit: string; inventoryAccountId: string; isActive: boolean; tracksDryness: boolean }
const METHOD_VI: Record<string, string> = { Fifo: 'Nhập trước xuất trước', MovingAverage: 'Bình quân tức thời', MonthlyAverage: 'Bình quân cuối tháng' }

function Stock() {
  const [whs, setWhs] = useState<Warehouse[]>([])
  const [items, setItems] = useState<Item[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [wh, setWh] = useState({ code: '', name: '' })
  const [policy, setPolicy] = useState({ warehouseId: '', method: 'MovingAverage', effectiveFrom: '', note: '' })
  const [item, setItem] = useState({ code: '', name: '', unit: '', inventoryAccountId: '', tracksDryness: false })
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => {
    setWhs(await get<Warehouse[]>('/warehouses')); setItems(await get<Item[]>('/items')); setAccounts(await get<Account[]>('/accounts'))
  }), [run])
  useEffect(() => { load() }, [load])
  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })
  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <section className="panel">
        <h3>Kho</h3>
        <table className="grid"><thead><tr><th>Mã</th><th>Tên</th><th>Phương pháp tính giá xuất kho (hiệu lực từ)</th></tr></thead>
          <tbody>{whs.map(w => <tr key={w.id}><td>{w.code}</td><td>{w.name}</td><td>{w.policies.map(p => `${METHOD_VI[p.method] ?? p.method} (từ ${p.effectiveFrom})`).join('; ') || <span className="muted">chưa thiết lập — chưa nhập xuất được</span>}</td></tr>)}</tbody></table>
        <div className="form-row" style={{ marginTop: 8 }}>
          <label>Mã kho <input value={wh.code} onChange={e => setWh(x => ({ ...x, code: e.target.value.toUpperCase() }))} /></label>
          <label>Tên kho <input value={wh.name} onChange={e => setWh(x => ({ ...x, name: e.target.value }))} /></label>
          <button disabled={busy || !wh.code || !wh.name} onClick={() => act(async () => { await post('/warehouses', wh); setWh({ code: '', name: '' }) }, 'Đã thêm kho.')}>Thêm kho</button>
        </div>
        <div className="form-row">
          <label>Kho <select value={policy.warehouseId} onChange={e => setPolicy(p => ({ ...p, warehouseId: e.target.value }))}><option value="">—</option>{whs.map(w => <option key={w.id} value={w.id}>{w.code}</option>)}</select></label>
          <label>Phương pháp <select value={policy.method} onChange={e => setPolicy(p => ({ ...p, method: e.target.value }))}>{Object.entries(METHOD_VI).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></label>
          <label>Hiệu lực từ (ngày đầu năm tài chính) <DateInput value={policy.effectiveFrom} onChange={e => setPolicy(p => ({ ...p, effectiveFrom: e.target.value }))} /></label>
          <label>Căn cứ / quyết định <input value={policy.note} onChange={e => setPolicy(p => ({ ...p, note: e.target.value }))} /></label>
          <button disabled={busy || !policy.warehouseId || !policy.effectiveFrom || !policy.note} onClick={() => act(() => post('/warehouses/costing-policy', policy), 'Đã đặt phương pháp tính giá.')}>Đặt phương pháp</button>
        </div>
      </section>
      <section className="panel">
        <h3>Vật tư, hàng hóa</h3>
        <table className="grid"><thead><tr><th>Mã</th><th>Tên</th><th>ĐVT</th><th>TK kho</th><th>Đo độ khô (MT/BDMT)</th></tr></thead>
          <tbody>{items.map(i => <tr key={i.id}><td>{i.code}</td><td>{i.name}</td><td>{i.unit}</td><td>{accounts.find(a => a.id === i.inventoryAccountId)?.code}</td>
            <td><input type="checkbox" aria-label={`Đo độ khô ${i.code}`} checked={i.tracksDryness} disabled={busy}
              onChange={e => act(() => post(`/items/${i.id}/tracks-dryness`, { tracks: e.target.checked }), 'Đã cập nhật theo dõi độ khô.')} /></td></tr>)}</tbody></table>
        <div className="form-row" style={{ marginTop: 8 }}>
          <label>Mã <input value={item.code} onChange={e => setItem(x => ({ ...x, code: e.target.value.toUpperCase() }))} /></label>
          <label>Tên <input value={item.name} onChange={e => setItem(x => ({ ...x, name: e.target.value }))} /></label>
          <label>ĐVT <input value={item.unit} onChange={e => setItem(x => ({ ...x, unit: e.target.value }))} size={6} /></label>
          <label>TK kho <AccountSelect accounts={accounts} value={item.inventoryAccountId} onChange={v => setItem(x => ({ ...x, inventoryAccountId: v }))}
            filter={a => a.cashKind === 'None' && a.openItemNature === 'None' && !a.requiresPartner} ariaLabel="TK kho" /></label>
          <label><input type="checkbox" checked={item.tracksDryness} onChange={e => setItem(x => ({ ...x, tracksDryness: e.target.checked }))} /> Đo độ khô</label>
          <button disabled={busy || !item.code || !item.name || !item.unit || !item.inventoryAccountId} onClick={() => act(async () => {
            const created = await post<{ id: string }>('/items', { code: item.code, name: item.name, unit: item.unit, inventoryAccountCode: accounts.find(a => a.id === item.inventoryAccountId)?.code })
            if (item.tracksDryness) await post(`/items/${created.id}/tracks-dryness`, { tracks: true })
            setItem({ code: '', name: '', unit: '', inventoryAccountId: '', tracksDryness: false })
          }, 'Đã thêm vật tư.')}>Thêm vật tư</button>
        </div>
      </section>
    </>
  )
}

function Years() {
  const [y, setY] = useState({ code: '', startDate: '', endDate: '' })
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  return (
    <section className="panel">
      <h3>Mở năm tài chính mới</h3>
      <p className="muted">Kỳ kế toán theo tháng được tạo tự động; khóa / mở kỳ ở màn hình Kỳ kế toán.</p>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="form-row">
        <label>Mã năm <input value={y.code} onChange={e => setY(x => ({ ...x, code: e.target.value }))} placeholder="2027" /></label>
        <label>Từ ngày <DateInput value={y.startDate} onChange={e => setY(x => ({ ...x, startDate: e.target.value }))} /></label>
        <label>Đến ngày <DateInput value={y.endDate} onChange={e => setY(x => ({ ...x, endDate: e.target.value }))} /></label>
        <button disabled={busy || !y.code || !y.startDate || !y.endDate} onClick={() => run(async () => { await post('/fiscal-years', y); setInfo(`Đã mở năm tài chính ${y.code}.`); setY({ code: '', startDate: '', endDate: '' }) })}>Mở năm</button>
      </div>
    </section>
  )
}
