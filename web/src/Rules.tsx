import { useCallback, useEffect, useMemo, useState } from 'react'
import { get, post } from './api'
import { askConfirm } from './dialogs'
import { AccountSelect, ErrorBox, Modal, PartnerSelect, TYPE_VI, useAction, useCatalog } from './common'
import { formatMoney, parseMoney, today } from './money'
import { DateInput } from './DateInput'

// Quy tắc định khoản: how a cash / bank document (phiếu thu, phiếu chi, báo Có, báo Nợ) becomes journal lines. Each rule is a
// versioned set of line templates; a version is activated only after its test cases pass against the current chart and role
// mappings. Account codes are never written into a rule: lines point at the document's own accounts or at account roles.

export const RULE_TYPES = ['CR', 'CP', 'BR', 'BP'] as const

/** Business events with a Vietnamese name (a rule may use any other code; it is then shown as is). */
export const EVENT_VI: Record<string, string> = {
  CUSTOMER_RECEIPT: 'Thu tiền khách hàng', SUPPLIER_PAYMENT: 'Trả nhà cung cấp', EXPENSE: 'Chi phí', INTERNAL_TRANSFER: 'Chuyển tiền nội bộ',
  ADVANCE: 'Chi tạm ứng', ADVANCE_RETURN: 'Thu hoàn tạm ứng', OTHER_RECEIPT: 'Thu khác', OTHER_PAYMENT: 'Chi khác',
}

export interface RuleLine { side: 'D' | 'C'; account: string; partner?: string | null; settle?: string | null; due_date?: string | null; constraint?: string | null }
export interface Rule {
  id: string; code: string; version: number; documentType: string; businessEvent: string; priority: number; effectiveFrom: string; effectiveTo: string | null
  status: 'Draft' | 'Active' | 'Retired'; definitionJson: string; testCasesJson: string; lastTestRunJson: string | null; description: string | null
}
interface RoleInfo { code: string; module: string; description: string; optional: boolean; account: string | null }
interface Mapping { roleCode: string; accountId: string; accountCode: string }
interface TestRun { passed: boolean; cases: { name: string; passed: boolean; detail: string | null }[] }

export const ruleLines = (r: Pick<Rule, 'definitionJson'>): RuleLine[] => { try { return JSON.parse(r.definitionJson).lines ?? [] } catch { return [] } }

/** What a document of this rule needs on screen: a partner, a counter account per line, the invoice it pays. */
export function ruleNeeds(lines: RuleLine[]) {
  const partnerLine = lines.find(l => l.partner === 'DOC:PARTNER')
  return {
    partner: !!partnerLine,
    counter: lines.some(l => l.account.split('|').some(a => a.trim() === 'LINE:COUNTER_ACCOUNT')),
    settle: lines.some(l => l.settle === 'LINE:SETTLE'),
    /** Role of the partner line (its account is where the open invoices are). */
    partnerRole: partnerLine?.account.split('|').map(a => a.trim()).find(a => a.startsWith('ROLE:'))?.slice(5) ?? null,
  }
}

const STATUS_VI: Record<Rule['status'], string> = { Draft: 'Nháp', Active: 'Đang hiệu lực', Retired: 'Đã ngừng' }

/** "DOC:MAIN_ACCOUNT|ROLE:CASH_DEFAULT" → "TK tiền của chứng từ, nếu trống: vai trò CASH_DEFAULT (1111)". */
function accountText(expr: string, mappings: Mapping[]): string {
  return expr.split('|').map(a => a.trim()).map(a =>
    a === 'DOC:MAIN_ACCOUNT' ? 'TK tiền của chứng từ'
      : a === 'LINE:COUNTER_ACCOUNT' ? 'TK đối ứng nhập trên dòng'
        : a.startsWith('ROLE:') ? `vai trò ${a.slice(5)}${mappings.find(m => m.roleCode === a.slice(5)) ? ` (${mappings.find(m => m.roleCode === a.slice(5))!.accountCode})` : ' (chưa ánh xạ)'}` : a,
  ).join(', nếu trống: ')
}

export function RulesSetup() {
  const [rules, setRules] = useState<Rule[]>([])
  const [mappings, setMappings] = useState<Mapping[]>([])
  const [roles, setRoles] = useState<RoleInfo[]>([])
  const [editing, setEditing] = useState<Partial<Rule> | null>(null)
  const [info, setInfo] = useState('')
  const [runs, setRuns] = useState<Record<string, TestRun>>({})
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => {
    setRules(await get<Rule[]>('/rules'))
    setMappings(await get<Mapping[]>('/rules/role-mappings'))
    setRoles(await get<RoleInfo[]>('/rules/account-roles'))
  }), [run])
  useEffect(() => { load() }, [load])

  const test = (r: Rule) => run(async () => { const t = await post<TestRun>(`/rules/${r.id}/test`); setRuns(x => ({ ...x, [r.id]: t })) })
  const activate = (r: Rule) => run(async () => {
    const res = await fetchActivate(r.id)
    setRuns(x => ({ ...x, [r.id]: res }))
    setInfo(res.passed ? `Đã kích hoạt ${r.code} phiên bản ${r.version}${rules.some(o => o.code === r.code && o.status === 'Active') ? '; phiên bản cũ đã ngừng' : ''}.` : `Chưa kích hoạt ${r.code}: có ca thử không đạt (xem bên dưới).`)
    await load()
  })
  const retire = async (r: Rule) => {
    if (await askConfirm(`Ngừng quy tắc ${r.code} phiên bản ${r.version}? Chứng từ ${TYPE_VI[r.documentType]} / ${EVENT_VI[r.businessEvent] ?? r.businessEvent} sẽ không lập được cho tới khi có quy tắc khác hiệu lực.`))
      run(async () => { await post(`/rules/${r.id}/retire`); setInfo(`Đã ngừng ${r.code} phiên bản ${r.version}.`); await load() })
  }

  const byType = RULE_TYPES.map(t => ({ t, list: rules.filter(r => r.documentType === t) }))
  const missing = (r: Rule) => ruleLines(r).flatMap(l => l.account.split('|').map(a => a.trim()).filter(a => a.startsWith('ROLE:')).map(a => a.slice(5)))
    .filter(code => !mappings.some(m => m.roleCode === code))
  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="panel">
        <div className="toolbar">
          <strong>Quy tắc định khoản thu / chi</strong>
          <span style={{ marginLeft: 'auto' }} />
          <button className="primary" onClick={() => setEditing({})}>Thêm quy tắc…</button>
        </div>
        <p className="muted">Màn Thu / Chi tiền lập chứng từ theo quy tắc đang hiệu lực của loại chứng từ và nghiệp vụ đã chọn. Quy tắc không ghi số tài khoản: dòng định khoản trỏ tới TK tiền của chứng từ,
          TK đối ứng người dùng nhập, hoặc một <b>vai trò tài khoản</b> (ánh xạ ở tab Vai trò tài khoản). Sửa quy tắc = tạo phiên bản mới; phiên bản chỉ kích hoạt được khi mọi ca thử đạt, phiên bản cũ tự ngừng.</p>
        {byType.map(({ t, list }) => (
          <div key={t} style={{ marginTop: 10 }}>
            <b>{TYPE_VI[t]}</b>
            <table className="grid">
              <thead><tr><th>Mã</th><th>PB</th><th>Nghiệp vụ</th><th>Định khoản</th><th>Hiệu lực</th><th>Trạng thái</th><th></th></tr></thead>
              <tbody>{list.map(r => {
                const lines = ruleLines(r), miss = missing(r), tr = runs[r.id] ?? (r.lastTestRunJson ? safeRun(r.lastTestRunJson) : null)
                return (
                  <tr key={r.id} className={r.status === 'Retired' ? 'muted' : undefined}>
                    <td>{r.code}</td><td>{r.version}</td><td>{EVENT_VI[r.businessEvent] ?? r.businessEvent}</td>
                    <td style={{ whiteSpace: 'normal', maxWidth: 520 }}>
                      {lines.map((l, i) => <div key={i}>{l.side === 'D' ? 'Nợ' : 'Có'}: {accountText(l.account, mappings)}{l.partner ? ' · theo đối tượng' : ''}{l.settle ? ' · thanh toán chứng từ đã chọn' : ''}</div>)}
                      {miss.length > 0 && <div className="field-warn">Chưa ánh xạ vai trò: {miss.join(', ')}</div>}
                      {tr && !tr.passed && <div className="field-warn">Ca thử không đạt: {tr.cases.filter(c => !c.passed).map(c => `${c.name}: ${c.detail ?? ''}`).join(' | ')}</div>}
                    </td>
                    <td>{r.effectiveFrom}{r.effectiveTo ? ` → ${r.effectiveTo}` : ''}</td>
                    <td><span className={`badge ${r.status === 'Active' ? 'Posted' : r.status === 'Draft' ? 'Draft' : 'Cancelled'}`}>{STATUS_VI[r.status]}</span>
                      {tr?.passed && r.status === 'Draft' && <div className="muted">thử: đạt</div>}</td>
                    <td style={{ display: 'flex', gap: 4 }}>
                      <button disabled={busy} onClick={() => test(r)}>Chạy thử</button>
                      {r.status === 'Draft' && <button className="primary" disabled={busy} onClick={() => activate(r)}>Kích hoạt</button>}
                      {r.status === 'Active' && <button className="danger" disabled={busy} onClick={() => retire(r)}>Ngừng</button>}
                      <button disabled={busy} onClick={() => setEditing(r)} title="Tạo phiên bản mới từ phiên bản này">Phiên bản mới…</button>
                    </td>
                  </tr>)
              })}
                {list.length === 0 && <tr><td colSpan={7} className="muted">Chưa có quy tắc — chưa lập được {TYPE_VI[t].toLowerCase()} ở màn Thu / Chi tiền.</td></tr>}
              </tbody>
            </table>
          </div>))}
      </div>
      {editing && <RuleEditor base={editing} roles={roles} mappings={mappings} onClose={() => setEditing(null)}
        onSaved={msg => { setEditing(null); setInfo(msg); load() }} />}
    </>
  )
}

const safeRun = (json: string): TestRun | null => { try { const r = JSON.parse(json); return { passed: r.Passed ?? r.passed, cases: (r.Cases ?? r.cases ?? []).map((c: Record<string, unknown>) => ({ name: c.Name ?? c.name, passed: c.Passed ?? c.passed, detail: c.Detail ?? c.detail })) } } catch { return null } }

/** Activation answers 422 with the test run when a case fails: both outcomes are returned as a run. */
async function fetchActivate(id: string): Promise<TestRun> {
  try { return await post<TestRun>(`/rules/${id}/activate`) }
  catch (e) {
    const err = e as { code?: string; message?: string }
    if (err.code === 'RULE_TEST_FAILED') return { ...(await post<TestRun>(`/rules/${id}/test`)), passed: false }
    throw e
  }
}

/** Starting points for common documents; the user still reviews, tests and activates them. */
const PRESETS: { label: string; documentType: string; businessEvent: string; lines: RuleLine[] }[] = [
  { label: 'Thu tiền khách hàng (phiếu thu)', documentType: 'CR', businessEvent: 'CUSTOMER_RECEIPT', lines: [{ side: 'D', account: 'DOC:MAIN_ACCOUNT|ROLE:CASH_DEFAULT' }, { side: 'C', account: 'ROLE:AR_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }] },
  { label: 'Khách hàng chuyển khoản (báo Có)', documentType: 'BR', businessEvent: 'CUSTOMER_RECEIPT', lines: [{ side: 'D', account: 'DOC:MAIN_ACCOUNT' }, { side: 'C', account: 'ROLE:AR_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }] },
  { label: 'Trả nhà cung cấp (phiếu chi)', documentType: 'CP', businessEvent: 'SUPPLIER_PAYMENT', lines: [{ side: 'D', account: 'ROLE:AP_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
  { label: 'Trả nhà cung cấp (báo Nợ)', documentType: 'BP', businessEvent: 'SUPPLIER_PAYMENT', lines: [{ side: 'D', account: 'ROLE:AP_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
  { label: 'Chi phí bằng tiền mặt', documentType: 'CP', businessEvent: 'EXPENSE', lines: [{ side: 'D', account: 'LINE:COUNTER_ACCOUNT', constraint: 'NOT_CASH_EQUIVALENT' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
  { label: 'Chuyển tiền nội bộ (báo Nợ)', documentType: 'BP', businessEvent: 'INTERNAL_TRANSFER', lines: [{ side: 'D', account: 'LINE:COUNTER_ACCOUNT', constraint: 'CASH_EQUIVALENT' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
  { label: 'Chi tạm ứng cho nhân viên', documentType: 'CP', businessEvent: 'ADVANCE', lines: [{ side: 'D', account: 'ROLE:EMPLOYEE_ADVANCE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
  { label: 'Thu hoàn tạm ứng', documentType: 'CR', businessEvent: 'ADVANCE_RETURN', lines: [{ side: 'D', account: 'DOC:MAIN_ACCOUNT' }, { side: 'C', account: 'ROLE:EMPLOYEE_ADVANCE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }] },
  { label: 'Thu khác (TK đối ứng nhập trên dòng)', documentType: 'CR', businessEvent: 'OTHER_RECEIPT', lines: [{ side: 'D', account: 'DOC:MAIN_ACCOUNT' }, { side: 'C', account: 'LINE:COUNTER_ACCOUNT', constraint: 'NOT_CASH_EQUIVALENT' }] },
]

type Source = 'DOC:MAIN_ACCOUNT' | 'LINE:COUNTER_ACCOUNT' | 'ROLE'
interface LineDraft { side: 'D' | 'C'; source: Source; role: string; fallbackRole: string; partner: boolean; settle: boolean; constraint: string }
const toDraft = (l: RuleLine): LineDraft => {
  const alts = l.account.split('|').map(a => a.trim())
  const first = alts[0], roleAlts = alts.filter(a => a.startsWith('ROLE:')).map(a => a.slice(5))
  return {
    side: l.side, source: first.startsWith('ROLE:') ? 'ROLE' : (first as Source), role: first.startsWith('ROLE:') ? roleAlts[0] : '',
    fallbackRole: first.startsWith('ROLE:') ? '' : roleAlts[0] ?? '', partner: l.partner === 'DOC:PARTNER', settle: l.settle === 'LINE:SETTLE', constraint: l.constraint ?? '',
  }
}
const fromDraft = (d: LineDraft): RuleLine => ({
  side: d.side,
  account: d.source === 'ROLE' ? `ROLE:${d.role}` : d.fallbackRole ? `${d.source}|ROLE:${d.fallbackRole}` : d.source,
  ...(d.partner ? { partner: 'DOC:PARTNER' } : {}), ...(d.settle ? { settle: 'LINE:SETTLE' } : {}), ...(d.constraint ? { constraint: d.constraint } : {}),
})

function RuleEditor({ base, roles, mappings, onClose, onSaved }: { base: Partial<Rule>; roles: RoleInfo[]; mappings: Mapping[]; onClose: () => void; onSaved: (msg: string) => void }) {
  const { accounts, partners } = useCatalog()
  const [code, setCode] = useState(base.code ?? '')
  const [documentType, setDocumentType] = useState(base.documentType ?? 'CR')
  const [event, setEvent] = useState(base.businessEvent ?? 'CUSTOMER_RECEIPT')
  const [description, setDescription] = useState(base.description ?? '')
  const [priority, setPriority] = useState(String(base.priority ?? 10))
  const [from, setFrom] = useState(base.effectiveFrom ?? today().slice(0, 4) + '-01-01')
  const [to, setTo] = useState(base.effectiveTo ?? '')
  const [lines, setLines] = useState<LineDraft[]>(base.definitionJson ? ruleLines(base as Rule).map(toDraft) : [toDraft({ side: 'D', account: 'DOC:MAIN_ACCOUNT' }), toDraft({ side: 'C', account: 'LINE:COUNTER_ACCOUNT' })])
  // One test case: a sample document, the lines the rule gives for it now; saved as the expected result.
  const [sample, setSample] = useState({ partnerId: '', mainAccountId: '', counterAccountId: '', amount: '1.000.000' })
  const { busy, error, setError, run } = useAction()
  const upd = (i: number, p: Partial<LineDraft>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l)))
  const def = lines.map(fromDraft)
  const needs = ruleNeeds(def)
  const roleOptions = useMemo(() => [...roles].sort((a, b) => a.code.localeCompare(b.code)), [roles])
  const acc = (id: string) => accounts.find(a => a.id === id)?.code ?? null

  // The journal lines the rule produces for the sample, resolved the way the server does (first alternative that has a value).
  const preview = (() => {
    const amount = parseMoney(sample.amount)
    if (!amount) return { error: 'Nhập số tiền mẫu.' }
    const out: { side: 'D' | 'C'; account_code: string; amount: string; partner_code?: string }[] = []
    for (const l of def) {
      let code: string | null = null
      for (const a of l.account.split('|').map(x => x.trim())) {
        if (a === 'DOC:MAIN_ACCOUNT' && sample.mainAccountId) { code = acc(sample.mainAccountId); break }
        if (a === 'LINE:COUNTER_ACCOUNT' && sample.counterAccountId) { code = acc(sample.counterAccountId); break }
        if (a.startsWith('ROLE:')) { const m = mappings.find(x => x.roleCode === a.slice(5)); if (m) { code = m.accountCode; break } }
      }
      if (!code) return { error: `Dòng ${l.side === 'D' ? 'Nợ' : 'Có'}: chưa xác định được tài khoản (${accountText(l.account, mappings)}) — chọn TK mẫu hoặc ánh xạ vai trò trước.` }
      const partner = l.partner ? partners.find(p => p.id === sample.partnerId)?.code : undefined
      if (l.partner && !partner) return { error: 'Quy tắc ghi theo đối tượng: chọn đối tượng mẫu.' }
      out.push({ side: l.side, account_code: code, amount, ...(partner ? { partner_code: partner } : {}) })
    }
    return { lines: out }
  })()

  const save = () => run(async () => {
    if (!code.trim()) { setError(new Error('Nhập mã quy tắc.')); return }
    if (def.filter(l => l.side === 'D').length !== def.filter(l => l.side === 'C').length) { setError(new Error('Số dòng Nợ phải bằng số dòng Có.')); return }
    if (!('lines' in preview) || !preview.lines) { setError(new Error(preview.error)); return }
    const testCases = [{
      name: `Mẫu ${formatMoney(parseMoney(sample.amount)!)}`,
      document: { partner_code: needs.partner ? partners.find(p => p.id === sample.partnerId)?.code ?? null : null, main_account_code: acc(sample.mainAccountId),
        lines: [{ amount: parseMoney(sample.amount), counter_account_code: needs.counter ? acc(sample.counterAccountId) : null }] },
      expected: preview.lines,
    }]
    const r = await post<{ id: string; version: number }>('/rules', {
      code: code.trim().toUpperCase(), documentType, businessEvent: event.trim().toUpperCase(), priority: Number.parseInt(priority, 10) || 10,
      effectiveFrom: from, effectiveTo: to || null, definition: { lines: def }, testCases, description: description.trim() || null,
    })
    onSaved(`Đã lưu ${code.trim().toUpperCase()} phiên bản ${r.version} (Nháp). Bấm "Kích hoạt" để dùng.`)
  })

  return (
    <Modal title={base.id ? `Phiên bản mới của ${base.code}` : 'Thêm quy tắc định khoản'} onClose={onClose} busy={busy}
      footer={<><button onClick={onClose} disabled={busy}>Hủy</button><button className="primary" disabled={busy} onClick={save}>Lưu (nháp)</button></>}>
      <ErrorBox error={error} />
      {!base.id && <label>Bắt đầu từ mẫu
        <select value="" onChange={e => { const p = PRESETS[Number(e.target.value)]; if (!p) return
          setDocumentType(p.documentType); setEvent(p.businessEvent); setLines(p.lines.map(toDraft)); setCode(code || `${p.documentType}-${p.businessEvent.split('_')[0]}`); setDescription(description || p.label) }}>
          <option value="">— chọn mẫu thường dùng —</option>{PRESETS.map((p, i) => <option key={i} value={i}>{p.label}</option>)}
        </select></label>}
      <div className="form-row">
        <label>Mã<input value={code} disabled={!!base.id} onChange={e => setCode(e.target.value.toUpperCase())} placeholder="CR-CUST" /></label>
        <label>Loại chứng từ<select value={documentType} onChange={e => setDocumentType(e.target.value)} disabled={!!base.id}>{RULE_TYPES.map(t => <option key={t} value={t}>{TYPE_VI[t]}</option>)}</select></label>
        <label>Nghiệp vụ<input list="rule-events" value={event} onChange={e => setEvent(e.target.value.toUpperCase())} disabled={!!base.id} />
          <datalist id="rule-events">{Object.entries(EVENT_VI).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</datalist></label>
        <label>Ưu tiên<input className="num" size={3} value={priority} onChange={e => setPriority(e.target.value)} /></label>
        <label>Hiệu lực từ<DateInput value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến<DateInput value={to} onChange={e => setTo(e.target.value)} /></label>
      </div>
      <label>Diễn giải<input value={description} onChange={e => setDescription(e.target.value)} /></label>
      <table className="grid" style={{ marginTop: 8 }}>
        <thead><tr><th>Nợ/Có</th><th>Tài khoản lấy từ</th><th>Vai trò</th><th>Theo đối tượng</th><th>Thanh toán chứng từ</th><th>Ràng buộc</th><th /></tr></thead>
        <tbody>{lines.map((l, i) => (
          <tr key={i}>
            <td><select aria-label="Nợ/Có" value={l.side} onChange={e => upd(i, { side: e.target.value as 'D' | 'C' })}><option value="D">Nợ</option><option value="C">Có</option></select></td>
            <td><select aria-label="Tài khoản lấy từ" value={l.source} onChange={e => upd(i, { source: e.target.value as Source })}>
              <option value="DOC:MAIN_ACCOUNT">TK tiền của chứng từ</option><option value="LINE:COUNTER_ACCOUNT">TK đối ứng nhập trên dòng</option><option value="ROLE">Vai trò tài khoản</option></select></td>
            <td><select aria-label="Vai trò" value={l.source === 'ROLE' ? l.role : l.fallbackRole} onChange={e => upd(i, l.source === 'ROLE' ? { role: e.target.value } : { fallbackRole: e.target.value })}
              title={l.source === 'ROLE' ? 'Vai trò tài khoản' : 'Vai trò dùng khi chứng từ không có tài khoản (không bắt buộc)'}>
              <option value="">{l.source === 'ROLE' ? '— chọn —' : '(không dùng)'}</option>
              {roleOptions.map(r => <option key={r.code} value={r.code}>{r.code}{r.account ? ` (${r.account.split(' – ')[0]})` : ' (chưa ánh xạ)'}</option>)}</select></td>
            <td><input type="checkbox" checked={l.partner} onChange={e => upd(i, { partner: e.target.checked })} aria-label="Theo đối tượng" /></td>
            <td><input type="checkbox" checked={l.settle} onChange={e => upd(i, { settle: e.target.checked })} aria-label="Thanh toán chứng từ" /></td>
            <td><select aria-label="Ràng buộc" value={l.constraint} onChange={e => upd(i, { constraint: e.target.value })}><option value="">—</option><option value="CASH_EQUIVALENT">phải là TK tiền</option><option value="NOT_CASH_EQUIVALENT">không được là TK tiền</option></select></td>
            <td><button onClick={() => setLines(ls => ls.filter((_, j) => j !== i))} aria-label="Xóa dòng">✕</button></td>
          </tr>))}
        </tbody>
      </table>
      <button style={{ marginTop: 6 }} onClick={() => setLines(ls => [...ls, toDraft({ side: ls.at(-1)?.side === 'D' ? 'C' : 'D', account: 'DOC:MAIN_ACCOUNT' })])}>+ Thêm dòng</button>
      <div className="panel" style={{ marginTop: 10 }}>
        <strong>Ca thử</strong> <span className="muted">— chứng từ mẫu và định khoản quy tắc cho ra; lưu làm kết quả mong đợi. Khi sau này đổi ánh xạ vai trò làm định khoản khác đi, chạy thử sẽ báo.</span>
        <div className="form-row">
          <label>TK tiền mẫu<AccountSelect accounts={accounts} value={sample.mainAccountId} onChange={v => setSample(s => ({ ...s, mainAccountId: v }))} filter={a => a.cashKind !== 'None'} /></label>
          {needs.counter && <label>TK đối ứng mẫu<AccountSelect accounts={accounts} value={sample.counterAccountId} onChange={v => setSample(s => ({ ...s, counterAccountId: v }))} /></label>}
          {needs.partner && <label>Đối tượng mẫu<PartnerSelect partners={partners} value={sample.partnerId} onChange={v => setSample(s => ({ ...s, partnerId: v }))} /></label>}
          <label>Số tiền<input className="num" value={sample.amount} onChange={e => setSample(s => ({ ...s, amount: e.target.value }))} /></label>
        </div>
        {'lines' in preview && preview.lines
          ? <table className="grid"><tbody>{preview.lines.map((l, i) => <tr key={i}><td>{l.side === 'D' ? 'Nợ' : 'Có'}</td><td>{l.account_code}</td><td>{l.partner_code ?? ''}</td><td className="num">{formatMoney(l.amount)}</td></tr>)}</tbody></table>
          : <div className="field-warn">{preview.error}</div>}
      </div>
    </Modal>
  )
}
