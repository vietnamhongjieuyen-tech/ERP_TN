import { useCallback, useEffect, useState } from 'react'
import { askText } from './dialogs'
import { get, post } from './api'
import { ErrorBox, PERM_VI, useAction } from './common'
import { formatMoney, today } from './money'
import { downloadXlsx } from './xlsx'
import { DateInput } from './DateInput'

interface Me { id: string; userName: string; fullName: string; isSystemAdmin: boolean }
interface Grant { companyId: string; companyCode: string; roleCode: string; roleName: string }
interface UserRow { id: string; userName: string; fullName: string; isActive: boolean; isSystemAdmin: boolean; lockedUntil: string | null; grants: Grant[] }
interface RoleRow { code: string; name: string; permissions: string[] }
interface Company { id: string; code: string; name: string }
interface BackupInfo { fileName: string; size: number; createdAt: string; databaseBytes: number; databaseSha256: string; note: string }
interface Verification { fileName: string; integrity: string; systemAuditChainOk: boolean; isValid: boolean; restoredPath: string | null
  companies: { companyCode: string; vouchers: number; journalEntries: number; brokenAuditSeq: number | null; postedDebit: string; postedCredit: string; balanced: boolean
    attachments: number; attachmentsDamaged: number; liveVouchers: number | null; liveJournalEntries: number | null; livePostedDebit: string | null }[] }
interface AuditRow { seq: number; at: string; userName: string | null; device: string | null; action: string; entityType: string; entityId: string | null; result: string; dataJson: string | null; hash?: string }

export function AdminView() {
  const [me, setMe] = useState<Me | null>(null)
  const [rev, setRev] = useState(0)
  useEffect(() => { get<Me>('/me').then(setMe).catch(() => setMe(null)) }, [])
  return (
    <>
      <div className="toolbar"><h2>Quản trị</h2></div>
      <OwnPassword />
      {me?.isSystemAdmin && <Regimes onChange={() => setRev(r => r + 1)} />}
      {me?.isSystemAdmin && <Companies reload={rev} onChange={() => setRev(r => r + 1)} />}
      {me?.isSystemAdmin && <Users me={me} reload={rev} />}
      {me?.isSystemAdmin && <Backups />}
      <AuditLog />
    </>
  )
}

function OwnPassword() {
  const [cur, setCur] = useState('')
  const [next, setNext] = useState('')
  const [done, setDone] = useState(false)
  const { busy, error, run } = useAction()
  return (
    <section className="panel">
      <h3>Đổi mật khẩu của tôi</h3>
      <ErrorBox error={error} />
      {done && !error && <div className="msg ok">Đã đổi mật khẩu; các phiên đăng nhập khác của bạn đã bị đóng.</div>}
      <div className="form-row">
        <label>Mật khẩu hiện tại <input type="password" autoComplete="current-password" value={cur} onChange={e => setCur(e.target.value)} /></label>
        <label>Mật khẩu mới <input type="password" autoComplete="new-password" value={next} onChange={e => setNext(e.target.value)} /></label>
        <button disabled={busy || !cur || !next} onClick={() => run(async () => { setDone(false); await post('/me/password', { currentPassword: cur, newPassword: next }); setCur(''); setNext(''); setDone(true) })}>Đổi mật khẩu</button>
      </div>
    </section>
  )
}

interface LegalSourceRow { ruleGroup: string; documentNo: string; issuedDate: string | null; article: string | null; url: string | null }
interface RegimeVersion { id: string; regime: string; regimeName: string; version: string; effectiveFrom: string; effectiveTo: string | null; status: string
  verificationNote: string | null; verifiedAt: string | null; sources: LegalSourceRow[] }

const EMPTY_SOURCE: LegalSourceRow = { ruleGroup: '', documentNo: '', issuedDate: '', article: '', url: '' }

/** Chế độ kế toán: phiên bản + căn cứ pháp lý; chỉ xác minh sau khi đối chiếu văn bản gốc (docs/07). */
function Regimes({ onChange }: { onChange: () => void }) {
  const [list, setList] = useState<RegimeVersion[]>([])
  const [f, setF] = useState({ regimeCode: '', regimeName: '', versionCode: '', effectiveFrom: '', effectiveTo: '' })
  const [sources, setSources] = useState<LegalSourceRow[]>([{ ...EMPTY_SOURCE }])
  const [notes, setNotes] = useState<Record<string, string>>({})
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => setList(await get<RegimeVersion[]>('/admin/regime-versions'))), [run])
  useEffect(() => { load() }, [load])
  const src = (i: number, k: keyof LegalSourceRow, v: string) => setSources(s => s.map((x, j) => (j === i ? { ...x, [k]: v } : x)))
  const validSources = sources.filter(s => s.ruleGroup.trim() && s.documentNo.trim())
  return (
    <section className="panel">
      <h3>Chế độ kế toán</h3>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <table className="grid"><thead><tr><th>Chế độ</th><th>Phiên bản</th><th>Hiệu lực</th><th>Căn cứ</th><th>Trạng thái</th></tr></thead>
        <tbody>{list.map(r => <tr key={r.id}>
          <td>{r.regime} – {r.regimeName}</td><td>{r.version}</td><td>{r.effectiveFrom}{r.effectiveTo ? ` → ${r.effectiveTo}` : ''}</td>
          <td>{r.sources.map((s, i) => <div key={i}>{s.ruleGroup}: {s.documentNo}{s.article ? `, ${s.article}` : ''}{s.issuedDate ? ` (${s.issuedDate})` : ''}</div>)}</td>
          <td>{r.status === 'Verified'
            ? <span>Đã xác minh {r.verifiedAt?.slice(0, 10)} — {r.verificationNote}</span>
            : <div className="form-row"><span className="muted">CHƯA xác minh</span>
              <input aria-label={`Nội dung xác minh ${r.version}`} placeholder="Người đối chiếu, văn bản gốc, phạm vi" value={notes[r.id] ?? ''}
                onChange={e => setNotes(n => ({ ...n, [r.id]: e.target.value }))} style={{ minWidth: 260 }} />
              <button disabled={busy || !(notes[r.id] ?? '').trim()} onClick={() => run(async () => {
                await post(`/admin/regime-versions/${r.id}/verify`, { note: notes[r.id] }); setInfo(`Đã ghi nhận xác minh ${r.regime} ${r.version}.`); await load(); onChange()
              })}>Xác minh</button></div>}</td></tr>)}</tbody></table>
      <h4>Khai báo phiên bản mới</h4>
      <div className="form-row">
        <label>Mã chế độ <input value={f.regimeCode} onChange={e => setF(x => ({ ...x, regimeCode: e.target.value.toUpperCase() }))} /></label>
        <label style={{ flex: 1 }}>Tên chế độ <input value={f.regimeName} onChange={e => setF(x => ({ ...x, regimeName: e.target.value }))} /></label>
        <label>Mã phiên bản <input value={f.versionCode} onChange={e => setF(x => ({ ...x, versionCode: e.target.value }))} /></label>
        <label>Hiệu lực từ <DateInput value={f.effectiveFrom} onChange={e => setF(x => ({ ...x, effectiveFrom: e.target.value }))} /></label>
        <label>đến <DateInput value={f.effectiveTo} onChange={e => setF(x => ({ ...x, effectiveTo: e.target.value }))} /></label>
      </div>
      {sources.map((s, i) => <div className="form-row" key={i}>
        <label>Nhóm quy định <input value={s.ruleGroup} onChange={e => src(i, 'ruleGroup', e.target.value)} placeholder="VD: Hệ thống tài khoản" /></label>
        <label>Số văn bản <input value={s.documentNo} onChange={e => src(i, 'documentNo', e.target.value)} /></label>
        <label>Ngày ban hành <DateInput value={s.issuedDate ?? ''} onChange={e => src(i, 'issuedDate', e.target.value)} /></label>
        <label>Điều / phụ lục <input value={s.article ?? ''} onChange={e => src(i, 'article', e.target.value)} /></label>
        <label style={{ flex: 1 }}>Đường dẫn văn bản <input value={s.url ?? ''} onChange={e => src(i, 'url', e.target.value)} /></label>
        {sources.length > 1 && <button onClick={() => setSources(x => x.filter((_, j) => j !== i))}>Bỏ</button>}
      </div>)}
      <div className="toolbar">
        <button title="Chỉ điền form theo legal/TT99-2025/README.md — xem lại rồi tự bấm Khai báo; xác minh là bước riêng" onClick={async () => {
          setF({ regimeCode: 'TT99', regimeName: 'Chế độ kế toán doanh nghiệp (Thông tư 99/2025/TT-BTC)', versionCode: '2025', effectiveFrom: '2026-01-01', effectiveTo: '' })
          setSources([['Hệ thống tài khoản', 'Điều 11, Phụ lục II'], ['Báo cáo tài chính', 'Điều 14–27, Phụ lục IV'], ['Chứng từ kế toán', 'Điều 8–10, Phụ lục I'],
            ['Sổ kế toán', 'Điều 12–13, Phụ lục III']].map(([ruleGroup, article]) => ({ ruleGroup, documentNo: '99/2025/TT-BTC', issuedDate: '2025-10-27', article, url: '' })))
        }}>Điền sẵn TT 99/2025</button>
        <button onClick={() => setSources(x => [...x, { ...EMPTY_SOURCE }])}>+ Căn cứ pháp lý</button>
        <button className="primary" disabled={busy || !f.regimeCode || !f.regimeName || !f.versionCode || !f.effectiveFrom || validSources.length === 0} onClick={() => run(async () => {
          await post('/admin/regime-versions', { ...f, effectiveTo: f.effectiveTo || null,
            sources: validSources.map(s => ({ ...s, issuedDate: s.issuedDate || null, article: s.article || null, url: s.url || null })) })
          setInfo(`Đã khai báo ${f.regimeCode} ${f.versionCode} (chưa xác minh).`)
          setF({ regimeCode: '', regimeName: '', versionCode: '', effectiveFrom: '', effectiveTo: '' }); setSources([{ ...EMPTY_SOURCE }]); await load(); onChange()
        })}>Khai báo</button>
      </div>
      <p className="muted">Phiên bản mới luôn ở trạng thái CHƯA xác minh. Chỉ bấm Xác minh khi đã đối chiếu văn bản gốc và có người chịu trách nhiệm ký xác nhận.</p>
    </section>
  )
}

/** reload: changes when a regime version was added / verified elsewhere; onChange: a company was created (refresh the grant form). */
function Companies({ reload, onChange }: { reload: number; onChange: () => void }) {
  const [list, setList] = useState<Company[]>([])
  const [regimes, setRegimes] = useState<RegimeVersion[]>([])
  const [f, setF] = useState({ code: '', name: '', taxCode: '', functionalCurrency: 'VND', regimeVersionId: '' })
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => { setList(await get<Company[]>('/admin/companies')); setRegimes(await get<RegimeVersion[]>('/admin/regime-versions')) }), [run])
  useEffect(() => { load() }, [load, reload])
  return (
    <section className="panel">
      <h3>Công ty</h3>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <table className="grid"><tbody>{list.map(c => <tr key={c.id}><td>{c.code}</td><td>{c.name}</td></tr>)}</tbody></table>
      <div className="form-row" style={{ marginTop: 8 }}>
        <label>Mã <input value={f.code} onChange={e => setF(x => ({ ...x, code: e.target.value.toUpperCase() }))} /></label>
        <label style={{ flex: 1 }}>Tên công ty <input value={f.name} onChange={e => setF(x => ({ ...x, name: e.target.value }))} /></label>
        <label>Mã số thuế <input value={f.taxCode} onChange={e => setF(x => ({ ...x, taxCode: e.target.value }))} /></label>
        <label>Đồng tiền hạch toán <select value={f.functionalCurrency} onChange={e => setF(x => ({ ...x, functionalCurrency: e.target.value }))}><option>VND</option><option>USD</option></select></label>
        <label>Chế độ kế toán <select value={f.regimeVersionId} onChange={e => setF(x => ({ ...x, regimeVersionId: e.target.value }))}><option value="">—</option>
          {regimes.map(r => <option key={r.id} value={r.id}>{r.regime} {r.version} (từ {r.effectiveFrom}{r.status === 'Verified' ? ', đã xác minh' : ', CHƯA xác minh'})</option>)}</select></label>
        <button disabled={busy || !f.code || !f.name || !f.regimeVersionId} onClick={() => run(async () => {
          await post('/admin/companies', { ...f, taxCode: f.taxCode || null }); setInfo(`Đã tạo công ty ${f.code}. Cấp vai trò cho người dùng ở phần dưới.`)
          setF({ code: '', name: '', taxCode: '', functionalCurrency: 'VND', regimeVersionId: '' }); await load(); onChange()
        })}>Tạo công ty</button>
      </div>
      {regimes.length === 0 && <p className="muted">Chưa có phiên bản chế độ kế toán nào — cần khai báo (kèm căn cứ pháp lý) trước khi tạo công ty.</p>}
    </section>
  )
}

function Users({ me, reload }: { me: Me; reload: number }) {
  const [users, setUsers] = useState<UserRow[]>([])
  const [roles, setRoles] = useState<RoleRow[]>([])
  const [companies, setCompanies] = useState<Company[]>([])
  const [form, setForm] = useState({ userName: '', fullName: '', password: '' })
  const [grant, setGrant] = useState({ userId: '', companyId: '', roleCode: '' })
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => {
    setUsers(await get<UserRow[]>('/admin/users'))
    setRoles(await get<RoleRow[]>('/admin/roles'))
    setCompanies(await get<Company[]>('/admin/companies'))
  }), [run])
  useEffect(() => { load() }, [load, reload])   // a company created above must appear in the grant form at once
  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })

  return (
    <section className="panel">
      <h3>Người dùng & phân quyền</h3>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="scroll"><table className="grid"><thead><tr><th>Tên đăng nhập</th><th>Họ tên</th><th>Trạng thái</th><th>Vai trò theo công ty</th><th>Thao tác</th></tr></thead>
        <tbody>{users.map(u => <tr key={u.id}>
          <td>{u.userName}{u.isSystemAdmin && <span className="muted"> · quản trị hệ thống</span>}</td><td>{u.fullName}</td>
          <td>{u.isActive ? (u.lockedUntil ? `Tạm khóa đến ${new Date(u.lockedUntil).toLocaleTimeString()}` : 'Hoạt động') : 'Đã khóa'}</td>
          <td>{u.grants.map(g => <div key={g.companyId + g.roleCode}>{g.companyCode}: {g.roleName}
            <button disabled={busy} style={{ marginLeft: 6 }} onClick={async () => { const reason = await askText(`Lý do thu hồi ${g.roleName} của ${u.userName} tại ${g.companyCode}:`)
              if (reason) act(() => post(`/admin/users/${u.id}/revoke`, { companyId: g.companyId, roleCode: g.roleCode, reason }), 'Đã thu hồi quyền.') }}>Thu hồi</button></div>)}</td>
          <td style={{ display: 'flex', gap: 6 }}>
            {u.id !== me.id && <button disabled={busy} onClick={async () => { const reason = await askText(u.isActive ? 'Lý do khóa tài khoản:' : 'Lý do mở khóa:')
              if (reason) act(() => post(`/admin/users/${u.id}/active`, { active: !u.isActive, reason }), u.isActive ? 'Đã khóa tài khoản và đóng mọi phiên.' : 'Đã mở khóa.') }}>{u.isActive ? 'Khóa' : 'Mở khóa'}</button>}
            <button disabled={busy} onClick={async () => { const p = await askText(`Mật khẩu tạm cho ${u.userName} (giao trực tiếp, người dùng đổi lại):`)
              if (p) act(() => post(`/admin/users/${u.id}/reset-password`, { newPassword: p }), 'Đã đặt lại mật khẩu và đóng các phiên.') }}>Đặt lại mật khẩu</button>
          </td></tr>)}</tbody></table></div>

      <h4>Thêm người dùng</h4>
      <div className="form-row">
        <label>Tên đăng nhập <input value={form.userName} onChange={e => setForm(f => ({ ...f, userName: e.target.value }))} /></label>
        <label>Họ tên <input value={form.fullName} onChange={e => setForm(f => ({ ...f, fullName: e.target.value }))} /></label>
        <label>Mật khẩu ban đầu <input type="password" autoComplete="new-password" value={form.password} onChange={e => setForm(f => ({ ...f, password: e.target.value }))} /></label>
        <button disabled={busy || !form.userName || !form.password} onClick={() => act(async () => { await post('/admin/users', form); setForm({ userName: '', fullName: '', password: '' }) }, 'Đã tạo người dùng.')}>Tạo</button>
      </div>

      <h4>Cấp vai trò tại công ty</h4>
      <div className="form-row">
        <label>Người dùng <select value={grant.userId} onChange={e => setGrant(g => ({ ...g, userId: e.target.value }))}><option value="">—</option>{users.map(u => <option key={u.id} value={u.id}>{u.userName}</option>)}</select></label>
        <label>Công ty <select value={grant.companyId} onChange={e => setGrant(g => ({ ...g, companyId: e.target.value }))}><option value="">—</option>{companies.map(c => <option key={c.id} value={c.id}>{c.code} – {c.name}</option>)}</select></label>
        <label>Vai trò <select value={grant.roleCode} onChange={e => setGrant(g => ({ ...g, roleCode: e.target.value }))}><option value="">—</option>{roles.map(r => <option key={r.code} value={r.code}>{r.name}</option>)}</select></label>
        <button disabled={busy || !grant.userId || !grant.companyId || !grant.roleCode} onClick={() => act(() => post('/admin/grants', grant), 'Đã cấp vai trò.')}>Cấp</button>
      </div>
      <EffectivePermissions users={users} roles={roles} companies={companies} />
      <details><summary>Quyền của từng vai trò mặc định</summary>
        <table className="grid"><tbody>{roles.map(r => <tr key={r.code}><td>{r.name}</td><td style={{ whiteSpace: 'normal' }}>{r.permissions.join(', ')}</td></tr>)}</tbody></table>
      </details>
    </section>
  )
}

/** Quyền hiệu lực: what one person may do at one company — the union of the roles granted there, with the role each comes from. */
function EffectivePermissions({ users, roles, companies }: { users: UserRow[]; roles: RoleRow[]; companies: Company[] }) {
  const [userId, setUserId] = useState('')
  const [companyId, setCompanyId] = useState('')
  const user = users.find(u => u.id === userId)
  const granted = user?.grants.filter(g => g.companyId === companyId) ?? []
  const from = (perm: string) => granted.filter(g => roles.find(r => r.code === g.roleCode)?.permissions.includes(perm)).map(g => g.roleName)
  const all = Object.keys(PERM_VI)
  return (
    <details>
      <summary>Quyền hiệu lực của một người tại một công ty</summary>
      <div className="form-row">
        <label>Người dùng <select value={userId} onChange={e => setUserId(e.target.value)}><option value="">—</option>{users.map(u => <option key={u.id} value={u.id}>{u.userName}</option>)}</select></label>
        <label>Công ty <select value={companyId} onChange={e => setCompanyId(e.target.value)}><option value="">—</option>{companies.map(c => <option key={c.id} value={c.id}>{c.code} – {c.name}</option>)}</select></label>
      </div>
      {user && companyId && <>
        {!user.isActive && <div className="field-warn">Tài khoản đang bị khóa: không đăng nhập được, quyền bên dưới chưa có hiệu lực.</div>}
        {granted.length === 0
          ? <p className="muted">{user.userName} chưa có vai trò nào tại công ty này: không mở được sổ sách của công ty.</p>
          : <table className="grid"><thead><tr><th>Quyền</th><th>Có</th><th>Từ vai trò</th></tr></thead>
            <tbody>{all.map(p => { const src = from(p); return (
              <tr key={p} className={src.length ? undefined : 'muted'}><td>{PERM_VI[p]}</td><td>{src.length ? '✓ Có' : '— Không'}</td><td>{src.join(', ')}</td></tr>) })}</tbody></table>}
        <p className="muted">Người lập có tự duyệt / ghi sổ chứng từ của mình được hay không còn tùy chế độ phân quyền của công ty (Thiết lập → Mẫu, mã phân tích, hạn mức duyệt). Máy chủ kiểm tra quyền ở mọi thao tác; thay đổi vai trò có hiệu lực ở lần thao tác kế tiếp.</p>
      </>}
    </details>
  )
}

function Backups() {
  const [list, setList] = useState<BackupInfo[]>([])
  const [pass, setPass] = useState('')
  const [note, setNote] = useState('')
  const [result, setResult] = useState<Verification | null>(null)
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => setList(await get<BackupInfo[]>('/admin/backups'))), [run])
  useEffect(() => { load() }, [load])
  const check = async (fileName: string, restore: boolean) => {
    const p = await askText(`Mật khẩu của bản sao lưu ${fileName}:`, '', 'password')
    if (p) run(async () => { setInfo(''); setResult(await post<Verification>(`/admin/backups/${restore ? 'restore' : 'verify'}`, { fileName, passphrase: p })) })
  }
  return (
    <section className="panel">
      <h3>Sao lưu / khôi phục (mã hóa AES-256)</h3>
      <p className="muted">Bản sao lưu chứa toàn bộ dữ liệu mọi công ty, mã hóa bằng mật khẩu sao lưu — <strong>mất mật khẩu là mất bản sao lưu</strong>; lưu mật khẩu tách riêng. Khôi phục không ghi đè dữ liệu đang chạy: hệ thống giải mã, kiểm tra toàn vẹn và chuỗi nhật ký rồi tạo file riêng để người vận hành chuyển sang.</p>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="form-row">
        <label>Mật khẩu sao lưu (≥ 12 ký tự) <input type="password" autoComplete="new-password" value={pass} onChange={e => setPass(e.target.value)} /></label>
        <label>Ghi chú <input value={note} onChange={e => setNote(e.target.value)} placeholder="Trước khi khóa sổ tháng…" /></label>
        <button className="primary" disabled={busy || pass.length < 12} onClick={() => run(async () => {
          const b = await post<BackupInfo>('/admin/backups', { passphrase: pass, note }); setPass(''); setInfo(`Đã sao lưu ${b.fileName}.`); await load()
        })}>Sao lưu ngay</button>
      </div>
      <BackupSchedule onRan={load} />
      <table className="grid"><thead><tr><th>File</th><th>Thời điểm</th><th className="num">Dung lượng</th><th>Ghi chú</th><th /></tr></thead>
        <tbody>{list.map(b => <tr key={b.fileName}><td>{b.fileName}</td><td>{new Date(b.createdAt).toLocaleString()}</td><td className="num">{formatMoney(String(b.size))}</td><td>{b.note}</td>
          <td style={{ display: 'flex', gap: 6 }}><button disabled={busy} onClick={() => check(b.fileName, false)}>Kiểm tra</button>
            <button disabled={busy} onClick={() => check(b.fileName, true)}>Khôi phục ra file mới</button></td></tr>)}</tbody></table>
      {result && <div className={`msg ${result.isValid ? 'ok' : 'error'}`}>
        {result.fileName}: toàn vẹn CSDL {result.integrity}; chuỗi nhật ký hệ thống {result.systemAuditChainOk ? 'nguyên vẹn' : 'BỊ HỎNG'}.
        <table className="grid" style={{ marginTop: 6 }}>
          <thead><tr><th>Công ty</th><th className="num">Chứng từ (bản sao lưu / hiện tại)</th><th className="num">Bút toán</th><th className="num">Tổng phát sinh Nợ (đ)</th><th>Nợ = Có</th><th className="num">Tệp đính kèm</th><th>Chuỗi nhật ký</th></tr></thead>
          <tbody>{result.companies.map(c => <tr key={c.companyCode}>
            <td>{c.companyCode}</td>
            <td className="num">{c.vouchers}{c.liveVouchers != null && ` / ${c.liveVouchers}`}</td>
            <td className="num">{c.journalEntries}{c.liveJournalEntries != null && ` / ${c.liveJournalEntries}`}</td>
            <td className="num">{formatMoney(c.postedDebit)}{c.livePostedDebit != null && c.livePostedDebit !== c.postedDebit && <div className="muted">hiện tại {formatMoney(c.livePostedDebit)}</div>}</td>
            <td>{c.balanced ? 'Cân' : <b>KHÔNG CÂN</b>}</td>
            <td className="num">{c.attachments}{c.attachmentsDamaged > 0 && <b> — {c.attachmentsDamaged} hỏng</b>}</td>
            <td>{c.brokenAuditSeq == null ? 'Nguyên vẹn' : <b>Hỏng từ #{c.brokenAuditSeq}</b>}</td></tr>)}</tbody>
        </table>
        <p className="muted">Số "hiện tại" là dữ liệu đang chạy: lớn hơn bản sao lưu là bình thường nếu đã nhập thêm sau lúc sao lưu. Diễn tập khôi phục: bấm "Khôi phục ra file mới", mở thử bằng bản thử nghiệm và so số dư, công nợ, tồn kho với bản đang chạy.</p>
        {result.restoredPath && <div>Đã khôi phục ra <code>{result.restoredPath}</code>. Dừng phần mềm, đặt <code>Hak:DatabasePath</code> trỏ tới file này rồi khởi động lại.</div>}
      </div>}
    </section>
  )
}

interface ScheduleInfo { enabled: boolean; hour: number; keep: number; copyTo: string | null; hasPassphrase: boolean; configuredBy: string | null
  lastRunAt: string | null; lastFile: string | null; lastError: string | null; directory: string; lastVerified: boolean | null; lastCopyVerified: boolean | null }

/** Daily automatic backup: hour, how many automatic copies to keep, an optional second folder (USB / NAS). */
function BackupSchedule({ onRan }: { onRan: () => void }) {
  const [s, setS] = useState<ScheduleInfo | null>(null)
  const [v, setV] = useState({ enabled: false, hour: '18', keep: '14', copyTo: '', pass: '' })
  const [msg, setMsg] = useState('')
  const { busy, error, run } = useAction()
  const show = (x: ScheduleInfo) => { setS(x); setV({ enabled: x.enabled, hour: String(x.hour), keep: String(x.keep), copyTo: x.copyTo ?? '', pass: '' }) }
  useEffect(() => { get<ScheduleInfo>('/admin/backups/schedule').then(show).catch(() => setS(null)) }, [])
  if (!s) return null
  const save = () => run(async () => {
    show(await post<ScheduleInfo>('/admin/backups/schedule', { enabled: v.enabled, hour: Number(v.hour), keep: Number(v.keep), copyTo: v.copyTo || null, passphrase: v.pass || null }))
    setMsg('Đã lưu lịch sao lưu tự động.')
  })
  return (
    <details className="panel" open={s.enabled || undefined} style={{ margin: '8px 0' }}>
      <summary><b>Lịch sao lưu tự động</b> — {s.enabled ? `hằng ngày từ ${s.hour}:00, giữ ${s.keep} bản` : 'đang tắt'}
        {s.lastRunAt && <span className={s.lastError ? 'field-warn' : 'muted'}> · lần chạy gần nhất {new Date(s.lastRunAt).toLocaleString()}: {s.lastError ?? s.lastFile}{!s.lastError && s.lastVerified ? ' — đã giải mã kiểm tra' : ''}{!s.lastError && s.lastCopyVerified ? ', bản chép ngoài khớp mã băm' : ''}</span>}</summary>
      <ErrorBox error={error} />
      {msg && !error && <div className="msg ok">{msg}</div>}
      <div className="form-row">
        <label className="check"><input type="checkbox" checked={v.enabled} onChange={e => setV(x => ({ ...x, enabled: e.target.checked }))} /> Bật</label>
        <label>Từ giờ<select value={v.hour} onChange={e => setV(x => ({ ...x, hour: e.target.value }))}>{Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{h}:00</option>)}</select></label>
        <label>Giữ lại (bản tự động)<input className="num" size={4} value={v.keep} onChange={e => setV(x => ({ ...x, keep: e.target.value }))} /></label>
        <label style={{ flex: 1 }}>Chép thêm sang thư mục (USB / máy khác, không bắt buộc)<input value={v.copyTo} onChange={e => setV(x => ({ ...x, copyTo: e.target.value }))} placeholder="E:\SaoLuuHAK" /></label>
        <label>{s.hasPassphrase ? 'Đổi mật khẩu (để trống: giữ)' : 'Mật khẩu sao lưu (≥ 12 ký tự)'}<input type="password" autoComplete="new-password" value={v.pass} onChange={e => setV(x => ({ ...x, pass: e.target.value }))} /></label>
        <button className="primary" disabled={busy} onClick={save}>Lưu lịch</button>
        <button disabled={busy || !s.enabled} onClick={() => run(async () => { const r = await post<ScheduleInfo>('/admin/backups/schedule/run'); show(r); setMsg(r.lastError ? '' : `Đã sao lưu ${r.lastFile}.`); onRan() })}>Chạy ngay</button>
      </div>
      <p className="muted">Phần mềm phải đang chạy vào giờ đã chọn (nếu tắt máy, bản sao lưu được tạo ở lần mở kế tiếp trong ngày). Bản tự động tên <code>hak-auto-…</code>; bản quá số giữ lại bị xóa, bản sao lưu tay không bị xóa.
        Mật khẩu được giữ trên máy này (mã hóa bằng Windows); vẫn phải ghi mật khẩu ở nơi riêng để khôi phục khi hỏng máy. Thư mục sao lưu chính: <code>{s.directory}</code>.</p>
    </details>
  )
}

function AuditLog() {
  const [rows, setRows] = useState<AuditRow[] | null>(null)
  const [total, setTotal] = useState(0)
  const [filter, setFilter] = useState({ from: '', to: '', action: '' })
  const [skip, setSkip] = useState(0)
  const [chain, setChain] = useState('')
  const { busy, error, run } = useAction()
  const take = 50
  const load = useCallback((s: number) => run(async () => {
    const q = new URLSearchParams({ skip: String(s), take: String(take) })
    if (filter.from) q.set('from', filter.from)
    if (filter.to) q.set('to', filter.to)
    if (filter.action) q.set('action', filter.action)
    const page = await get<{ total: number; rows: AuditRow[] }>(`/audit?${q}`)
    setRows(page.rows); setTotal(page.total); setSkip(s)
  }), [run, filter])
  useEffect(() => { get('/audit?take=1').then(() => load(0)).catch(() => setRows(null)) }, [])   // hidden without audit.view
  if (rows === null) return null
  return (
    <section className="panel">
      <h3>Nhật ký kiểm toán (công ty đang chọn)</h3>
      <ErrorBox error={error} />
      <div className="form-row">
        <label>Từ <DateInput value={filter.from} onChange={e => setFilter(f => ({ ...f, from: e.target.value }))} /></label>
        <label>Đến <DateInput value={filter.to} onChange={e => setFilter(f => ({ ...f, to: e.target.value }))} /></label>
        <label>Hành động bắt đầu bằng <input value={filter.action} onChange={e => setFilter(f => ({ ...f, action: e.target.value }))} placeholder="voucher.post, loan., …" /></label>
        <button disabled={busy} onClick={() => load(0)}>Lọc</button>
        <button disabled={busy} onClick={() => run(async () => {
          const r = await get<{ intact: boolean; firstBrokenSeq: number | null }>('/audit/verify')
          setChain(r.intact ? 'Chuỗi băm nhật ký nguyên vẹn.' : `Chuỗi băm BỊ HỎNG từ bản ghi #${r.firstBrokenSeq} — dữ liệu nhật ký đã bị sửa ngoài phần mềm.`)
        })}>Kiểm tra chuỗi băm</button>
        <button disabled={busy || total === 0} title="Mọi bản ghi theo bộ lọc hiện tại, kèm mã băm để đối chiếu" onClick={() => run(async () => {
          const all: AuditRow[] = []
          for (let s = 0; s < total && s < 200000; s += 500) {
            const q = new URLSearchParams({ skip: String(s), take: '500' })
            if (filter.from) q.set('from', filter.from)
            if (filter.to) q.set('to', filter.to)
            if (filter.action) q.set('action', filter.action)
            all.push(...(await get<{ total: number; rows: AuditRow[] }>(`/audit?${q}`)).rows)
          }
          downloadXlsx(`nhat-ky-kiem-toan-${filter.from || 'dau'}-${filter.to || today()}.xlsx`, 'Nhật ký',
            [{ header: '#', numeric: true, width: 8 }, { header: 'Thời điểm (giờ máy)', width: 20 }, { header: 'Người dùng', width: 14 }, { header: 'Máy', width: 16 }, { header: 'Hành động', width: 22 },
              { header: 'Loại đối tượng', width: 14 }, { header: 'Mã đối tượng', width: 38 }, { header: 'Kết quả', width: 10 }, { header: 'Dữ liệu', width: 80 }, { header: 'Mã băm', width: 66 }],
            all.map(r => [String(r.seq), new Date(r.at).toLocaleString('vi-VN'), r.userName, r.device, r.action, r.entityType, r.entityId, r.result, r.dataJson, r.hash]))
        })}>Xuất Excel</button>
      </div>
      {chain && <div className={`msg ${chain.startsWith('Chuỗi băm nhật ký nguyên') ? 'ok' : 'error'}`}>{chain}</div>}
      <div className="scroll"><table className="grid"><thead><tr><th>#</th><th>Thời điểm</th><th>Người dùng</th><th>Máy</th><th>Hành động</th><th>Đối tượng</th><th>Kết quả</th><th>Dữ liệu</th></tr></thead>
        <tbody>{rows.map(r => <tr key={r.seq}><td>{r.seq}</td><td>{new Date(r.at).toLocaleString()}</td><td>{r.userName}</td><td>{r.device}</td><td>{r.action}</td>
          <td>{r.entityType}{r.entityId ? ` ${r.entityId.slice(0, 8)}` : ''}</td><td>{r.result}</td>
          <td title={r.dataJson ?? ''} style={{ maxWidth: 360, overflow: 'hidden', textOverflow: 'ellipsis' }}>{r.dataJson}</td></tr>)}</tbody></table></div>
      <div className="form-row" style={{ marginTop: 6 }}>
        <span className="muted">{skip + 1}–{Math.min(skip + take, total)} / {total}</span>
        <button disabled={busy || skip === 0} onClick={() => load(Math.max(0, skip - take))}>Trước</button>
        <button disabled={busy || skip + take >= total} onClick={() => load(skip + take)}>Sau</button>
      </div>
    </section>
  )
}
