import { useCallback, useEffect, useState } from 'react'
import { get, post } from './api'
import { ErrorBox, ExportButton, useAction } from './common'
import { formatMoney, today } from './money'
import { DateInput } from './DateInput'

interface DefInfo { id: string; code: string; version: number; name: string; category: string; legalReference: string | null; status: 'Draft' | 'Verified'
  verificationNote: string | null; verifiedAt: string | null; lineCount: number; snapshots: number; createdAt: string }
interface LineInput { code: string; name: string; formula: string | null; indent: number; bold: boolean; note: string | null }
interface CheckInput { rule: string; message: string }
interface DefInput { code: string; name: string; category: string; legalReference: string | null; lines: LineInput[]; checks: CheckInput[]; columns?: string[] | null }
interface Contribution { source: string; amount: string }
interface RunLine { lineNo: number; code: string; name: string; indent: number; bold: boolean; note: string | null; value: string | null; compareValue: string | null
  contributions: Contribution[]; error: string | null; values?: (string | null)[] | null; compareValues?: (string | null)[] | null }
interface RunCheck { rule: string; message: string; left: string; right: string; passed: boolean; error: string | null; compareLeft: string | null; compareRight: string | null; comparePassed: boolean | null }
interface Run { definitionId: string; code: string; version: number; name: string; legalReference: string | null; verified: boolean; companyName: string
  fromDate: string; toDate: string; compareFrom: string | null; compareTo: string | null; lines: RunLine[]; checks: RunCheck[]; columns?: string[] | null }
interface SnapshotInfo { id: string; definitionCode: string; definitionVersion: number; definitionVerified: boolean; fromDate: string; toDate: string
  compareFrom: string | null; compareTo: string | null; sha256: string; note: string; createdAt: string; createdBy: string }

const TABS = { run: 'Chạy báo cáo', defs: 'Mẫu báo cáo', snaps: 'Kết quả đã chốt' } as const

/** Báo cáo theo mẫu (Report Engine). Mẫu do doanh nghiệp khai báo; mẫu BCTC pháp lý chưa xác minh không được dựng sẵn. */
export function ReportEngineView() {
  const [tab, setTab] = useState<keyof typeof TABS>('run')
  const [defs, setDefs] = useState<DefInfo[]>([])
  const [editing, setEditing] = useState<DefInput | null>(null)
  const { error, run } = useAction()
  const load = useCallback(() => run(async () => setDefs(await get<DefInfo[]>('/report-definitions'))), [run])
  useEffect(() => { load() }, [load])
  return (
    <>
      <div className="toolbar"><h2>Báo cáo theo mẫu</h2>
        {Object.entries(TABS).map(([k, v]) => <button key={k} className={tab === k ? 'primary' : ''} onClick={() => setTab(k as keyof typeof TABS)}>{v}</button>)}
      </div>
      <ErrorBox error={error} />
      {tab === 'run' && <RunReport defs={defs} onSnapshot={load} />}
      {tab === 'defs' && (editing
        ? <Editor initial={editing} onDone={() => { setEditing(null); load() }} />
        : <Definitions defs={defs} reload={load} onEdit={setEditing} />)}
      {tab === 'snaps' && <Snapshots />}
    </>
  )
}

const statusVi = (d: { status: string }) => (d.status === 'Verified' ? 'đã xác minh' : 'CHƯA xác minh (nháp)')

function ResultTable({ r, onPick, picked }: { r: Run; onPick?: (l: RunLine) => void; picked?: string }) {
  const compare = r.compareFrom !== null
  return (
    <>
      {!r.verified && <div className="msg error">MẪU CHƯA XÁC MINH — số liệu chỉ để kiểm tra nội bộ, không dùng nộp cơ quan nhà nước.</div>}
      <p><b>{r.companyName}</b> · {r.name} ({r.code} v{r.version}{r.legalReference ? ` · ${r.legalReference}` : ''}) · kỳ {r.fromDate} → {r.toDate}
        {compare && <> · so sánh {r.compareFrom} → {r.compareTo}</>}</p>
      <div className="scroll"><table className="grid">
        <thead><tr><th>Chỉ tiêu</th><th>Mã số</th><th>Thuyết minh</th>{valueHeaders(r).map(h => <th key={h} className="num">{h}</th>)}</tr></thead>
        <tbody>{r.lines.map(l => <tr key={l.lineNo} onClick={() => onPick?.(l)} style={{ cursor: onPick && hasValue(l) ? 'pointer' : undefined,
          fontWeight: l.bold ? 600 : undefined, background: picked === l.code ? 'var(--hover, #eef3fb)' : undefined }}>
          <td style={{ paddingLeft: 8 + 16 * l.indent }}>{l.name}{l.error && <div className="msg error" style={{ margin: 0 }}>{l.error}</div>}</td>
          <td>{/^H\d+$/.test(l.code) && !hasValue(l) ? '' : l.code}</td><td>{l.note}</td>
          {cells(r, l).map((v, i) => <td key={i} className="num">{v === null ? '' : formatMoney(v)}</td>)}</tr>)}</tbody></table></div>
      {r.checks.length > 0 && <><h4>Kiểm tra</h4><ul>{r.checks.map((c, i) => <li key={i} className={c.passed && c.comparePassed !== false ? '' : 'msg error'}>
        {c.passed && c.comparePassed !== false ? '✓' : '✗'} {c.message} — {c.rule}: {formatMoney(c.left)} / {formatMoney(c.right)}
        {c.compareLeft !== null && <> · kỳ so sánh {formatMoney(c.compareLeft)} / {formatMoney(c.compareRight)}</>}{c.error && ` — ${c.error}`}</li>)}</ul></>}
    </>
  )
}

/** Value column headers: one or the template's columns, each followed by its comparison column when a comparison period was run. */
function valueHeaders(r: Run): string[] {
  const cols = r.columns?.length ? r.columns : ['Kỳ này']
  return r.compareFrom ? cols.flatMap(c => [c, r.columns?.length ? `${c} (kỳ so sánh)` : 'Kỳ so sánh']) : cols
}
function cells(r: Run, l: RunLine): (string | null)[] {
  const cols = r.columns ?? []
  const now = cols.length ? (l.values ?? cols.map(() => null)) : [l.value]
  const then = cols.length ? (l.compareValues ?? cols.map(() => null)) : [l.compareValue]
  return r.compareFrom ? now.flatMap((v, i) => [v, then[i] ?? null]) : now
}
const hasValue = (l: RunLine) => l.value !== null || (l.values ?? []).some(v => v !== null)

function exportColumns(r: Run) {
  return [{ header: 'Chỉ tiêu', width: 50 }, { header: 'Mã số' }, { header: 'Thuyết minh' }, ...valueHeaders(r).map(h => ({ header: h, numeric: true }))]
}
function exportRows(r: Run) {
  return r.lines.map(l => [' '.repeat(l.indent * 3) + l.name, l.code, l.note ?? '', ...cells(r, l)])
}

function RunReport({ defs, onSnapshot }: { defs: DefInfo[]; onSnapshot: () => void }) {
  const year = today().slice(0, 4)
  const [p, setP] = useState({ definitionId: '', fromDate: `${year}-01-01`, toDate: today(), compare: false, compareFrom: `${Number(year) - 1}-01-01`, compareTo: `${Number(year) - 1}-12-31` })
  const [result, setResult] = useState<Run | null>(null)
  const [picked, setPicked] = useState<RunLine | null>(null)
  const [note, setNote] = useState('')
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const body = () => ({ definitionId: p.definitionId, fromDate: p.fromDate, toDate: p.toDate,
    compareFrom: p.compare ? p.compareFrom : null, compareTo: p.compare ? p.compareTo : null })
  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <section className="panel"><div className="form-row">
        <label style={{ flex: 1 }}>Mẫu <select value={p.definitionId} onChange={e => { setP(x => ({ ...x, definitionId: e.target.value })); setResult(null) }}>
          <option value="">—</option>{defs.map(d => <option key={d.id} value={d.id}>{d.code} v{d.version} – {d.name} ({statusVi(d)})</option>)}</select></label>
        <label>Từ ngày <DateInput value={p.fromDate} onChange={e => setP(x => ({ ...x, fromDate: e.target.value }))} /></label>
        <label>Đến ngày <DateInput value={p.toDate} onChange={e => setP(x => ({ ...x, toDate: e.target.value }))} /></label>
        <label><input type="checkbox" checked={p.compare} onChange={e => setP(x => ({ ...x, compare: e.target.checked }))} /> Cột so sánh</label>
        {p.compare && <>
          <label>Từ <DateInput value={p.compareFrom} onChange={e => setP(x => ({ ...x, compareFrom: e.target.value }))} /></label>
          <label>Đến <DateInput value={p.compareTo} onChange={e => setP(x => ({ ...x, compareTo: e.target.value }))} /></label></>}
        <button className="primary" disabled={busy || !p.definitionId} onClick={() => run(async () => { setInfo(''); setPicked(null); setResult(await post<Run>('/report-runs', body())) })}>Chạy</button>
      </div>
      {defs.length === 0 && <p className="muted">Chưa có mẫu nào — khai báo ở tab "Mẫu báo cáo".</p>}</section>
      {result && <section className="panel">
        <div className="toolbar">
          <ExportButton fileName={`${result.code}-${result.toDate}`} sheet={result.code} columns={exportColumns(result)} rows={exportRows(result)} />
          <label style={{ flex: 1 }}>Ghi chú khi chốt <input value={note} onChange={e => setNote(e.target.value)} placeholder="VD: số liệu trình ban giám đốc quý 3" /></label>
          <button disabled={busy} title="Lưu nguyên kết quả này (kèm mã băm) — chỉ kế toán trưởng" onClick={() => run(async () => {
            const s = await post<{ id: string; sha256: string }>('/report-runs/snapshots', { ...body(), note })
            setInfo(`Đã chốt số liệu (mã băm ${s.sha256.slice(0, 12)}…).`); onSnapshot()
          })}>Chốt số liệu</button>
        </div>
        <ResultTable r={result} onPick={l => hasValue(l) && setPicked(l)} picked={picked?.code} />
      </section>}
      {picked && <section className="panel">
        <h3>Chi tiết chỉ tiêu {picked.code} – {picked.name}{picked.value !== null && <>: {formatMoney(picked.value)}</>}</h3>
        <table className="grid"><thead><tr><th>Nguồn (TK, TK/đối ứng, chỉ tiêu)</th><th className="num">Số tiền</th></tr></thead>
          <tbody>{picked.contributions.map((c, i) => <tr key={i}><td>{c.source}</td><td className="num">{formatMoney(c.amount)}</td></tr>)}</tbody></table>
        <p className="muted">Xem dòng sổ của từng TK ở màn hình "Sổ chi tiết / Sổ quỹ".</p>
      </section>}
    </>
  )
}

function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }))
  const a = document.createElement('a'); a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

const EMPTY: DefInput = { code: '', name: '', category: 'Quản trị', legalReference: null, lines: [{ code: '10', name: '', formula: '', indent: 0, bold: false, note: null }], checks: [] }

function Definitions({ defs, reload, onEdit }: { defs: DefInfo[]; reload: () => void; onEdit: (d: DefInput) => void }) {
  const [notes, setNotes] = useState<Record<string, string>>({})
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <section className="panel">
        <div className="toolbar">
          <button className="primary" onClick={() => onEdit(EMPTY)}>Mẫu mới</button>
          <label>Nhập mẫu từ tệp JSON <input type="file" accept=".json" onChange={e => {
            const f = e.target.files?.[0]; e.target.value = ''
            if (f) run(async () => { const d = JSON.parse(await f.text()) as DefInput; onEdit({ ...EMPTY, ...d, checks: d.checks ?? [] }) })
          }} /></label>
        </div>
        <table className="grid"><thead><tr><th>Mã</th><th>Phiên bản</th><th>Tên</th><th>Nhóm</th><th>Chỉ tiêu</th><th>Đã chốt</th><th>Trạng thái</th><th /></tr></thead>
          <tbody>{defs.map(d => <tr key={d.id}>
            <td>{d.code}</td><td>{d.version}</td><td>{d.name}{d.legalReference && <div className="muted">{d.legalReference}</div>}</td><td>{d.category}</td>
            <td>{d.lineCount}</td><td>{d.snapshots}</td>
            <td>{d.status === 'Verified' ? <span title={d.verificationNote ?? ''}>Đã xác minh {d.verifiedAt?.slice(0, 10)}</span>
              : <div className="form-row"><span className="muted">Nháp</span>
                <input aria-label={`Nội dung xác minh ${d.code}`} placeholder="Đối chiếu với…, người chịu trách nhiệm" value={notes[d.id] ?? ''}
                  onChange={e => setNotes(n => ({ ...n, [d.id]: e.target.value }))} />
                <button disabled={busy || !(notes[d.id] ?? '').trim()} onClick={() => run(async () => {
                  await post(`/report-definitions/${d.id}/verify`, { note: notes[d.id] }); setInfo(`Đã xác minh ${d.code} v${d.version}.`); reload()
                })}>Xác minh</button></div>}</td>
            <td><div className="form-row">
              <button onClick={() => run(async () => onEdit(await get<DefInput>(`/report-definitions/${d.id}`)))}>{d.status === 'Verified' ? 'Tạo phiên bản mới' : 'Sửa'}</button>
              <button onClick={() => run(async () => download(`${d.code}-v${d.version}.json`, JSON.stringify(await get<DefInput>(`/report-definitions/${d.id}`), null, 2)))}>Xuất JSON</button>
              {d.status === 'Draft' && d.snapshots === 0 && <button disabled={busy} onClick={() => run(async () => {
                await post(`/report-definitions/${d.id}/discard`); setInfo(`Đã xóa bản nháp ${d.code} v${d.version}.`); reload()
              })}>Xóa nháp</button>}</div></td></tr>)}</tbody></table>
        <p className="muted">Mẫu do doanh nghiệp khai báo; hệ thống không dựng sẵn mẫu BCTC theo TT 99/2025 khi chưa có văn bản gốc đã đối chiếu.
          Người xác minh phải khác người soạn; mẫu đã xác minh không sửa được — sửa thì tạo phiên bản mới.</p>
      </section>
    </>
  )
}

function Editor({ initial, onDone }: { initial: DefInput; onDone: () => void }) {
  const [d, setD] = useState<DefInput>(initial)
  const { busy, error, run } = useAction()
  const setLine = (i: number, k: keyof LineInput, v: string | number | boolean) => setD(x => ({ ...x, lines: x.lines.map((l, j) => (j === i ? { ...l, [k]: v } : l)) }))
  const move = (i: number, by: number) => setD(x => {
    const lines = [...x.lines]; const j = i + by
    if (j < 0 || j >= lines.length) return x
    ;[lines[i], lines[j]] = [lines[j], lines[i]]
    return { ...x, lines }
  })
  return (
    <>
      <ErrorBox error={error} />
      <section className="panel">
        <h3>{initial.code ? `Soạn mẫu ${initial.code}` : 'Mẫu mới'}</h3>
        <div className="form-row">
          <label>Mã mẫu <input value={d.code} onChange={e => setD(x => ({ ...x, code: e.target.value.toUpperCase() }))} /></label>
          <label style={{ flex: 1 }}>Tên <input value={d.name} onChange={e => setD(x => ({ ...x, name: e.target.value }))} /></label>
          <label>Nhóm <input value={d.category} onChange={e => setD(x => ({ ...x, category: e.target.value }))} /></label>
          <label>Căn cứ (mẫu số, phụ lục) <input value={d.legalReference ?? ''} onChange={e => setD(x => ({ ...x, legalReference: e.target.value || null }))} /></label>
        </div>
        <div className="form-row"><label style={{ flex: 1 }}>Cột số liệu (để trống = một cột; nhiều cột ngăn bởi |, vd Đầu năm|Tăng|Giảm|Cuối năm)
          <input value={(d.columns ?? []).join('|')} onChange={e => setD(x => ({ ...x, columns: e.target.value ? e.target.value.split('|') : null }))} /></label></div>
        <details><summary>Cú pháp công thức</summary><ul className="muted">
          <li><code>BAL_D(111*, 112*)</code> dư Nợ cuối kỳ (thuần theo từng TK); <code>BAL_C</code> dư Có; <code>BAL_NET</code> số dư có dấu (Nợ dương); <code>OPEN_D/OPEN_C/OPEN_NET</code> đầu kỳ.</li>
          <li><code>GROSS_D(131*)</code> / <code>GROSS_C</code> dư theo từng đối tượng (TK lưỡng tính); <code>OPEN_GROSS_D/C</code> đầu kỳ.</li>
          <li><code>MOV_D(632*)</code> / <code>MOV_C</code> phát sinh trong kỳ; <code>MOV_C(511* ; 131*)</code> chỉ phần đối ứng với TK 131* (chứng từ nhiều Nợ – nhiều Có bị báo lỗi, không đoán).</li>
          <li><code>NET_D(632*)</code> / <code>NET_C</code> phát sinh thuần không tính kết chuyển (CLS); <code>PS_D</code> / <code>PS_C</code> phát sinh trừ bút toán đảo; <code>FXR(131*)</code> phần đánh giá lại tỷ giá; <code>CF(01, 02)</code> luồng tiền theo mã B03; <code>MAX0(…)</code> phần dương.</li>
          <li>Nhãn TK: <code>131*[DH]</code>, <code>131*[!DH]</code>, <code>128*[TDT,!HCSD]</code>.</li>
          <li><code>!1388*</code> loại trừ; <code>LINE(110)</code> chỉ tiêu khác; dùng được <code>+ - *</code> và ngoặc. Để trống công thức = dòng tiêu đề.</li>
          <li>Kiểm tra: <code>LINE(270) = LINE(440)</code>.</li>
          <li>Mẫu nhiều cột: mỗi công thức gồm một phần cho mỗi cột, ngăn bởi <code>|</code> (bỏ trống = ô trống), vd <code>OPEN_D(211*) | PS_D(211*) | PS_C(211*) | BAL_D(211*)</code>;
            công thức không có <code>|</code> dùng cho mọi cột; <code>LINE()</code> lấy cùng cột; quy tắc kiểm tra áp dụng cho từng cột.</li></ul></details>
        <div className="scroll"><table className="grid">
          <thead><tr><th>Mã số</th><th>Chỉ tiêu</th><th>Công thức</th><th>Thụt lề</th><th>Đậm</th><th>Thuyết minh</th><th /></tr></thead>
          <tbody>{d.lines.map((l, i) => <tr key={i}>
            <td><input value={l.code} onChange={e => setLine(i, 'code', e.target.value.toUpperCase())} style={{ width: 70 }} aria-label={`Mã số dòng ${i + 1}`} /></td>
            <td><input value={l.name} onChange={e => setLine(i, 'name', e.target.value)} style={{ minWidth: 240 }} aria-label={`Tên dòng ${i + 1}`} /></td>
            <td><input value={l.formula ?? ''} onChange={e => setLine(i, 'formula', e.target.value)} style={{ minWidth: 260, fontFamily: 'monospace' }} aria-label={`Công thức dòng ${i + 1}`} /></td>
            <td><input type="number" min={0} max={6} value={l.indent} onChange={e => setLine(i, 'indent', Number(e.target.value))} style={{ width: 50 }} /></td>
            <td><input type="checkbox" checked={l.bold} onChange={e => setLine(i, 'bold', e.target.checked)} /></td>
            <td><input value={l.note ?? ''} onChange={e => setLine(i, 'note', e.target.value)} style={{ width: 80 }} /></td>
            <td><button onClick={() => move(i, -1)}>↑</button><button onClick={() => move(i, 1)}>↓</button>
              <button onClick={() => setD(x => ({ ...x, lines: x.lines.filter((_, j) => j !== i) }))}>Bỏ</button></td></tr>)}</tbody></table></div>
        <button onClick={() => setD(x => ({ ...x, lines: [...x.lines, { code: '', name: '', formula: '', indent: 0, bold: false, note: null }] }))}>+ Chỉ tiêu</button>
        <h4>Quy tắc kiểm tra</h4>
        {d.checks.map((c, i) => <div className="form-row" key={i}>
          <label style={{ flex: 1 }}>Quy tắc <input value={c.rule} onChange={e => setD(x => ({ ...x, checks: x.checks.map((y, j) => (j === i ? { ...y, rule: e.target.value } : y)) }))} style={{ fontFamily: 'monospace' }} /></label>
          <label style={{ flex: 1 }}>Thông báo <input value={c.message} onChange={e => setD(x => ({ ...x, checks: x.checks.map((y, j) => (j === i ? { ...y, message: e.target.value } : y)) }))} /></label>
          <button onClick={() => setD(x => ({ ...x, checks: x.checks.filter((_, j) => j !== i) }))}>Bỏ</button></div>)}
        <button onClick={() => setD(x => ({ ...x, checks: [...x.checks, { rule: '', message: '' }] }))}>+ Quy tắc</button>
        <div className="toolbar">
          <button className="primary" disabled={busy || !d.code || !d.name} onClick={() => run(async () => {
            await post('/report-definitions', { ...d, lines: d.lines.filter(l => l.code || l.name) }); onDone()
          })}>Lưu nháp</button>
          <button onClick={onDone}>Hủy</button>
        </div>
      </section>
    </>
  )
}

function Snapshots() {
  const [list, setList] = useState<SnapshotInfo[]>([])
  const [shown, setShown] = useState<{ run: Run; hashValid: boolean; info: SnapshotInfo } | null>(null)
  const { error, run } = useAction()
  useEffect(() => { run(async () => setList(await get<SnapshotInfo[]>('/report-runs/snapshots'))) }, [run])
  return (
    <>
      <ErrorBox error={error} />
      <section className="panel"><table className="grid">
        <thead><tr><th>Thời điểm chốt</th><th>Mẫu</th><th>Kỳ</th><th>Người chốt</th><th>Ghi chú</th><th>Mã băm</th><th /></tr></thead>
        <tbody>{list.map(s => <tr key={s.id}><td>{s.createdAt.replace('T', ' ').slice(0, 16)}</td>
          <td>{s.definitionCode} v{s.definitionVersion}{s.definitionVerified ? '' : ' (chưa xác minh)'}</td>
          <td>{s.fromDate} → {s.toDate}{s.compareFrom && <div className="muted">so sánh {s.compareFrom} → {s.compareTo}</div>}</td>
          <td>{s.createdBy}</td><td>{s.note}</td><td><code>{s.sha256.slice(0, 12)}…</code></td>
          <td><button onClick={() => run(async () => setShown(await get(`/report-runs/snapshots/${s.id}`)))}>Xem</button></td></tr>)}</tbody></table>
        {list.length === 0 && <p className="muted">Chưa chốt kết quả nào.</p>}</section>
      {shown && <section className="panel">
        <div className={shown.hashValid ? 'msg ok' : 'msg error'}>{shown.hashValid ? 'Mã băm khớp — nội dung đúng như lúc chốt.' : 'MÃ BĂM KHÔNG KHỚP — dữ liệu đã bị thay đổi sau khi chốt!'}
          {' '}Chốt bởi {shown.info.createdBy} lúc {shown.info.createdAt.replace('T', ' ').slice(0, 16)}.</div>
        <div className="toolbar"><ExportButton fileName={`${shown.run.code}-${shown.run.toDate}-chot`} sheet={shown.run.code}
          columns={exportColumns(shown.run)} rows={exportRows(shown.run)} /></div>
        <ResultTable r={shown.run} />
      </section>}
    </>
  )
}
