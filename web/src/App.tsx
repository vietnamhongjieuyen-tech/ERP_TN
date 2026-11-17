import { Fragment, useEffect, useState } from 'react'
import { get, post, session } from './api'
import { AdminView } from './Admin'
import { AnalysisReportView } from './Analysis'
import { AssetsView } from './Assets'
import { BankingView } from './Banking'
import { CashDocsView } from './CashDocs'
import { CatalogView } from './Catalog'
import { DashboardView } from './Dashboard'
import { ErrorBox, useAction } from './common'
import { DialogHost } from './dialogs'
import { FxView } from './Fx'
import { InventoryView } from './Inventory'
import { confirmLeave } from './leave'
import { LoansView } from './Loans'
import { OrdersView } from './Orders'
import { PayrollView } from './Payroll'
import { SetupView } from './Setup'
import { ProductionView } from './Production'
import { ReportEngineView } from './ReportEngine'
import { PeriodsView } from './Periods'
import { TradeView } from './Trade'
import { VatView } from './Vat'
import { AccountLedgerView, GeneralJournalView, ReceivablesView, TrialBalanceView } from './Reports'
import { VouchersView } from './Vouchers'

interface Company { id: string; code: string; name: string; roles?: string[]; permissions?: string[] }

// Menu groups in working order; a screen with perm is hidden from users whose roles at this company lack that permission
// (the backend still checks every request).
const SCREENS = [
  { key: 'home', group: 'Tổng hợp', label: 'Tổng quan', el: () => <DashboardView /> },
  { key: 'vouchers', group: 'Tổng hợp', label: 'Chứng từ', el: () => <VouchersView /> },
  { key: 'periods', group: 'Tổng hợp', label: 'Kỳ kế toán', el: () => <PeriodsView /> },
  { key: 'cash', group: 'Tiền', label: 'Thu / Chi tiền', el: () => <CashDocsView /> },
  { key: 'bank', group: 'Tiền', label: 'Ngân hàng', el: () => <BankingView /> },
  { key: 'fx', group: 'Tiền', label: 'Ngoại tệ', el: () => <FxView /> },
  { key: 'loans', group: 'Tiền', label: 'Vay', el: () => <LoansView /> },
  { key: 'orders', group: 'Mua – Bán – Kho', label: 'Đơn hàng', el: () => <OrdersView /> },
  { key: 'trade', group: 'Mua – Bán – Kho', label: 'Mua / Bán', el: () => <TradeView /> },
  { key: 'stock', group: 'Mua – Bán – Kho', label: 'Kho', el: () => <InventoryView /> },
  { key: 'arap', group: 'Mua – Bán – Kho', label: 'Công nợ', perm: 'report.view', el: () => <ReceivablesView /> },
  { key: 'production', group: 'Mua – Bán – Kho', label: 'Giá thành', el: () => <ProductionView /> },
  { key: 'assets', group: 'Tài sản – Lương – Thuế', label: 'Tài sản / CCDC', el: () => <AssetsView /> },
  { key: 'payroll', group: 'Tài sản – Lương – Thuế', label: 'Lương', el: () => <PayrollView /> },
  { key: 'vat', group: 'Tài sản – Lương – Thuế', label: 'Thuế GTGT', el: () => <VatView /> },
  { key: 'tb', group: 'Báo cáo', label: 'Cân đối phát sinh', perm: 'report.view', el: () => <TrialBalanceView /> },
  { key: 'ledger', group: 'Báo cáo', label: 'Sổ chi tiết / Sổ quỹ', perm: 'report.view', el: () => <AccountLedgerView /> },
  { key: 'journal', group: 'Báo cáo', label: 'Sổ Nhật ký chung', perm: 'report.view', el: () => <GeneralJournalView /> },
  { key: 'reports', group: 'Báo cáo', label: 'Báo cáo theo mẫu', perm: 'report.view', el: () => <ReportEngineView /> },
  { key: 'analysis', group: 'Báo cáo', label: 'Phân tích theo mã', perm: 'report.view', el: () => <AnalysisReportView /> },
  { key: 'catalog', group: 'Hệ thống', label: 'Danh mục đối tượng', el: () => <CatalogView /> },
  { key: 'setup', group: 'Hệ thống', label: 'Thiết lập', el: () => <SetupView /> },
  { key: 'admin', group: 'Hệ thống', label: 'Quản trị', el: () => <AdminView /> },
] as const
type ScreenKey = (typeof SCREENS)[number]['key']

export default function App() {
  const [token, setToken] = useState(session.token)
  const [companies, setCompanies] = useState<Company[] | null>(null)
  const [companyId, setCompanyId] = useState(session.companyId)
  const [isSystemAdmin, setIsSystemAdmin] = useState(false)
  const [systemAdmin, setSystemAdmin] = useState(false)
  const [screen, setScreen] = useState<ScreenKey>('home')
  const [visit, setVisit] = useState(0)   // bumped by goTo so the target screen remounts and takes its parameters

  useEffect(() => {
    const onGoto = async (e: Event) => { const to = (e as CustomEvent<ScreenKey>).detail; if (await confirmLeave()) { setScreen(to); setVisit(v => v + 1) } }
    window.addEventListener('hak:goto', onGoto)
    return () => window.removeEventListener('hak:goto', onGoto)
  }, [])

  useEffect(() => {
    if (!token) return
    setCompanies(null)
    get<Company[]>('/me/companies').then(setCompanies).catch(() => { session.setToken(null); setToken(null) })
    get<{ isSystemAdmin: boolean }>('/me').then(m => setIsSystemAdmin(m.isSystemAdmin)).catch(() => setIsSystemAdmin(false))
  }, [token])

  const logout = async () => {
    if (!(await confirmLeave())) return
    try { await post('/auth/logout') } catch { /* already expired */ }
    session.setToken(null); session.setCompany(null); setCompanyId(null); setCompanies(null); setSystemAdmin(false); setToken(null)
  }
  if (!token) return <Login onLogin={t => { session.setToken(t); setToken(t) }} />
  if (companies === null) return <div className="login"><div className="panel"><span className="muted">Đang tải…</span></div></div>
  if (systemAdmin) {
    // System administration needs no company: users, grants, backups (a fresh installation has no company role yet).
    return (
      <main className="main">
        <div className="toolbar"><button onClick={() => setSystemAdmin(false)}>← Chọn công ty</button><button onClick={logout}>Đăng xuất</button></div>
        <AdminView />
        <DialogHost />
      </main>
    )
  }
  const company = companies.find(c => c.id === companyId)
  if (!company) {
    return (
      <div className="login"><div className="panel">
        <h2>Chọn công ty</h2>
        {companies.length === 0 && <p className="muted">Tài khoản chưa được cấp quyền ở công ty nào{isSystemAdmin ? ' — dùng "Quản trị hệ thống" để cấp quyền.' : '.'}</p>}
        {companies.map(c => <button key={c.id} onClick={() => { session.setCompany(c.id); setCompanyId(c.id) }}>{c.code} – {c.name}{c.roles?.length ? ` (vai trò: ${c.roles.join(', ')})` : ''}</button>)}
        {isSystemAdmin && <button onClick={() => setSystemAdmin(true)}>Quản trị hệ thống (người dùng, sao lưu)</button>}
        <button onClick={logout}>Đăng xuất</button>
      </div></div>
    )
  }

  // A grant without voucher.view (e.g. only 'Administrator') cannot open the accounting screens: say so and how to fix it.
  if (company.permissions && !company.permissions.includes('voucher.view')) {
    return (
      <div className="login"><div className="panel">
        <h2>{company.code} – {company.name}</h2>
        <div className="msg error">Tài khoản này tại công ty {company.code} chỉ có vai trò: <b>{(company.roles ?? []).join(', ') || '—'}</b>, chưa được xem chứng từ và sổ sách.
          Vai trò &quot;Administrator&quot; chỉ dùng để quản trị; để làm kế toán cần cấp thêm vai trò <b>Kế toán trưởng</b> hoặc <b>Kế toán tổng hợp</b>.</div>
        <p className="muted">Cách cấp: Đổi công ty → Quản trị hệ thống → Người dùng &amp; phân quyền → Cấp vai trò tại công ty (chọn tài khoản, công ty {company.code}, vai trò Kế toán trưởng) → Cấp; sau đó đăng xuất rồi đăng nhập lại.</p>
        <button onClick={() => { session.setCompany(null); setCompanyId(null) }}>Đổi công ty</button>
        <button onClick={logout}>Đăng xuất</button>
      </div></div>
    )
  }
  const allowed = SCREENS.filter(s => !('perm' in s) || !company.permissions || company.permissions.includes(s.perm))
  const current = allowed.find(s => s.key === screen) ?? allowed[0]
  return (
    <div className="shell">
      <nav className="sidebar" aria-label="Phân hệ">
        <h1>HAK Accounting</h1>
        <div className="company">{company.code} – {company.name}</div>
        {allowed.map((s, i) => <Fragment key={s.key}>
          {(i === 0 || allowed[i - 1].group !== s.group) && <div className="group">{s.group}</div>}
          <button className={s.key === current.key ? 'active' : ''} onClick={async () => { if (s.key !== current.key && await confirmLeave()) setScreen(s.key) }}>{s.label}</button>
        </Fragment>)}
        <button onClick={async () => { if (await confirmLeave()) { session.setCompany(null); setCompanyId(null) } }}>Đổi công ty</button>
        <button onClick={logout}>Đăng xuất</button>
      </nav>
      <main className="main" key={`${company.id}:${current.key}:${visit}`}>{current.el()}</main>
      <DialogHost />
    </div>
  )
}

function Login({ onLogin }: { onLogin: (token: string) => void }) {
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  // null until the server says whether this is the first run — shows "đang kết nối" instead of a login form that may not apply.
  const [firstRun, setFirstRun] = useState<boolean | null>(null)
  const [statusError, setStatusError] = useState(false)
  const [fullName, setFullName] = useState('')
  const [confirm, setConfirm] = useState('')
  const { busy, error, run } = useAction()
  useEffect(() => {
    let stop = false
    const check = (attempt: number) => get<{ needsBootstrap: boolean }>('/setup/status')
      .then(s => { if (!stop) { setFirstRun(s.needsBootstrap); setStatusError(false) } })
      .catch(() => { if (stop) return; setStatusError(attempt >= 3); setTimeout(() => check(attempt + 1), 2000) })
    check(0)
    return () => { stop = true }
  }, [])
  if (firstRun === null)
    return <div className="login"><div className="panel"><h2 style={{ margin: 0 }}>HAK Accounting</h2>
      <span className="muted">{statusError ? 'Chưa kết nối được máy chủ HAK — kiểm tra cửa sổ HAK còn chạy không; trang sẽ tự thử lại.' : 'Đang kết nối máy chủ…'}</span></div></div>
  const login = async () => onLogin((await post<{ token: string }>('/auth/login', { userName, password, device: 'web' })).token)
  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    run(async () => {
      if (firstRun) {
        if (password !== confirm) throw new Error('Hai lần nhập mật khẩu không khớp.')
        await post('/setup/bootstrap', { userName, fullName, password })
      }
      await login()
    })
  }
  return (
    <form className="login" onSubmit={submit}>
      <div className="panel">
        <h2 style={{ margin: 0 }}>HAK Accounting</h2>
        {firstRun
          ? <span className="muted">Lần chạy đầu: cơ sở dữ liệu chưa có người dùng. Tạo <b>quản trị hệ thống</b> đầu tiên — người này tạo công ty, người dùng và phân quyền. Ghi nhớ mật khẩu và nên tạo thêm một quản trị dự phòng; nếu quên, đặt lại tại máy chủ bằng lệnh khôi phục (docs/08).</span>
          : <span className="muted">Đăng nhập — dữ liệu chỉ lưu trên máy chủ nội bộ</span>}
        <ErrorBox error={error} />
        {firstRun && <label>Họ tên<input value={fullName} onChange={e => setFullName(e.target.value)} /></label>}
        <label>Tên đăng nhập<input autoFocus value={userName} onChange={e => setUserName(e.target.value)} autoComplete="username" /></label>
        <label>Mật khẩu<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete={firstRun ? 'new-password' : 'current-password'} /></label>
        {firstRun && <label>Nhập lại mật khẩu<input type="password" value={confirm} onChange={e => setConfirm(e.target.value)} autoComplete="new-password" /></label>}
        <button className="primary" disabled={busy || !userName || !password || (firstRun && (!fullName || !confirm))}>{firstRun ? 'Tạo quản trị viên và đăng nhập' : 'Đăng nhập'}</button>
      </div>
    </form>
  )
}
