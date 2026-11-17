import { useCallback, useEffect, useMemo, useState } from 'react'
import { get, post, put, session } from './api'
import { askConfirm } from './dialogs'
import { ErrorBox, ExportButton, useAction } from './common'
import { addMoney, formatMoney, parseMoney, today } from './money'

export interface AnalysisCode { id: string; kind: string; code: string; name: string; isActive: boolean }
interface Row { analysisCodeId: string; kind: string; code: string; name: string; accountCode: string; accountName: string; debit: string; credit: string }

/** Thiết lập: analysis codes (mã phân tích) and the company's approval limit (hạn mức duyệt). */
export function AnalysisSetup() {
  const [codes, setCodes] = useState<AnalysisCode[]>([])
  const [nw, setNw] = useState({ kind: '', code: '', name: '' })
  const [limit, setLimit] = useState('')
  const [address, setAddress] = useState('')
  const [templates, setTemplates] = useState<{ id: string; name: string; voucherType: string; description: string; lines: unknown[]; updatedAt: string }[]>([])
  const [info, setInfo] = useState('')
  const { busy, error, setError, run } = useAction()
  const load = useCallback(() => run(async () => {
    setCodes(await get<AnalysisCode[]>('/analysis-codes'))
    setTemplates(await get('/voucher-templates'))
    const l = (await get<{ limit: string | null }>('/company/approval-limit')).limit
    setLimit(l ? formatMoney(l) : '')
    setAddress((await get<{ id: string; address?: string | null }[]>('/me/companies')).find(c => c.id === session.companyId)?.address ?? '')
  }), [run])
  useEffect(() => { load() }, [load])
  const kinds = [...new Set(codes.map(c => c.kind))]

  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="panel">
        <strong>Mã phân tích</strong>
        <p className="muted">Mã quản trị gắn thêm vào từng dòng chứng từ, bên cạnh tài khoản: khoản mục phí, bộ phận, hợp đồng, chuyến tàu… Mỗi mã thuộc một nhóm do doanh nghiệp đặt tên.
          Báo cáo "Phân tích theo mã" cộng doanh thu, chi phí theo từng mã. Mã đã dùng không xóa được, chỉ ngừng dùng.</p>
        <div className="form-row">
          <label>Nhóm<input list="analysis-kinds" value={nw.kind} onChange={e => setNw(n => ({ ...n, kind: e.target.value }))} placeholder="Chuyến tàu" /></label>
          <datalist id="analysis-kinds">{kinds.map(k => <option key={k} value={k} />)}</datalist>
          <label>Mã<input value={nw.code} onChange={e => setNw(n => ({ ...n, code: e.target.value.toUpperCase() }))} placeholder="T2026-03" /></label>
          <label style={{ flex: 1 }}>Tên<input value={nw.name} onChange={e => setNw(n => ({ ...n, name: e.target.value }))} /></label>
          <button className="primary" style={{ alignSelf: 'flex-end' }} disabled={busy || !nw.kind.trim() || !nw.code.trim() || !nw.name.trim()} onClick={() => run(async () => {
            await post('/analysis-codes', nw); setInfo(`Đã thêm mã ${nw.code}.`); setNw(n => ({ kind: n.kind, code: '', name: '' })); await load()
          })}>Thêm</button>
        </div>
        <table className="grid">
          <thead><tr><th>Nhóm</th><th>Mã</th><th>Tên</th><th>Trạng thái</th><th></th></tr></thead>
          <tbody>{codes.map(c => (
            <tr key={c.id} className={c.isActive ? undefined : 'muted'}><td>{c.kind}</td><td>{c.code}</td><td>{c.name}</td><td>{c.isActive ? 'Đang dùng' : 'Ngừng dùng'}</td>
              <td><button disabled={busy} onClick={() => run(async () => { await put(`/analysis-codes/${c.id}`, { name: c.name, isActive: !c.isActive }); await load() })}>{c.isActive ? 'Ngừng dùng' : 'Dùng lại'}</button></td></tr>))}
            {codes.length === 0 && <tr><td colSpan={5} className="muted">Chưa có mã phân tích. Khi có ít nhất một mã, form chứng từ hiện thêm cột "Mã phân tích".</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="panel">
        <strong>Định khoản mẫu</strong>
        <p className="muted">Mẫu được lưu từ form chứng từ (nút "Lưu làm mẫu…") và chọn lại ở ô "Định khoản mẫu" khi lập chứng từ mới. Lưu trùng tên sẽ thay mẫu cũ.</p>
        <table className="grid">
          <thead><tr><th>Tên mẫu</th><th>Loại</th><th>Diễn giải</th><th className="num">Số dòng</th><th>Cập nhật</th><th></th></tr></thead>
          <tbody>{templates.map(t => (
            <tr key={t.id}><td>{t.name}</td><td>{t.voucherType}</td><td>{t.description}</td><td className="num">{t.lines.length}</td><td>{new Date(t.updatedAt).toLocaleDateString('vi-VN')}</td>
              <td><button className="danger" disabled={busy} onClick={async () => { if (await askConfirm(`Xóa định khoản mẫu "${t.name}"?`)) run(async () => { await post(`/voucher-templates/${t.id}/delete`); setInfo(`Đã xóa mẫu ${t.name}.`); await load() }) }}>Xóa</button></td></tr>))}
            {templates.length === 0 && <tr><td colSpan={6} className="muted">Chưa có định khoản mẫu.</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="panel">
        <strong>Thông tin in trên chứng từ</strong>
        <p className="muted">Địa chỉ công ty được in trên phiếu thu, phiếu chi, phiếu kho và ủy nhiệm chi. Số tài khoản và tên ngân hàng của từng tài khoản tiền gửi khai khi sửa tài khoản đó ở tab Hệ thống tài khoản.</p>
        <div className="form-row">
          <label style={{ flex: 1 }}>Địa chỉ công ty<input value={address} onChange={e => setAddress(e.target.value)} /></label>
          <button style={{ alignSelf: 'flex-end' }} disabled={busy} onClick={() => run(async () => { await put('/company/address', { address: address.trim() || null }); setInfo('Đã lưu địa chỉ công ty.'); await load() })}>Lưu địa chỉ</button>
        </div>
      </div>
      <div className="panel">
        <strong>Hạn mức duyệt chứng từ</strong>
        <p className="muted">Chứng từ có tổng tiền lớn hơn hạn mức chỉ được duyệt bởi người có quyền "duyệt vượt hạn mức" (mặc định: Kế toán trưởng, Giám đốc).
          Để trống = không áp dụng. Thay đổi hạn mức cần quyền quản trị cấu hình và được ghi nhật ký.</p>
        <div className="form-row">
          <label>Hạn mức (đồng)<input className="num" value={limit} onChange={e => setLimit(e.target.value)} placeholder="không áp dụng" /></label>
          <button style={{ alignSelf: 'flex-end' }} disabled={busy} onClick={() => run(async () => {
            const v = limit.trim() === '' ? null : parseMoney(limit)
            if (limit.trim() !== '' && !v) { setError(new Error('Hạn mức không hợp lệ.')); return }
            await put('/company/approval-limit', { limit: v }); setInfo(v ? `Đã đặt hạn mức duyệt ${formatMoney(v)}.` : 'Đã bỏ hạn mức duyệt.'); await load()
          })}>Lưu hạn mức</button>
        </div>
      </div>
    </>
  )
}

/** Active analysis codes as picker options (empty when the company uses none: screens then hide the column). */
export function useAnalysisOptions() {
  const [codes, setCodes] = useState<AnalysisCode[]>([])
  useEffect(() => { get<AnalysisCode[]>('/analysis-codes').then(setCodes).catch(() => setCodes([])) }, [])
  return useMemo(() => codes.filter(c => c.isActive).map(c => ({ id: c.id, code: c.code, name: `${c.name} (${c.kind})` })), [codes])
}

const firstOfYear = () => today().slice(0, 4) + '-01-01'
const net = (r: { debit: string; credit: string }) => addMoney([r.credit, '-' + r.debit])

/** Báo cáo: movements per analysis code and account; "Có − Nợ" is the result of the code when its revenue and cost lines are tagged. */
export function AnalysisReportView() {
  const [from, setFrom] = useState(firstOfYear())
  const [to, setTo] = useState(today())
  const [rows, setRows] = useState<Row[] | null>(null)
  const { busy, error, run } = useAction()
  const groups = rows ? [...new Set(rows.map(r => r.analysisCodeId))].map(id => rows.filter(r => r.analysisCodeId === id)) : []
  const total = (g: Row[]) => ({ debit: addMoney(g.map(r => r.debit)), credit: addMoney(g.map(r => r.credit)) })
  return (
    <>
      <div className="toolbar">
        <h2>Phân tích theo mã</h2>
        <label>Từ ngày<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" disabled={busy} onClick={() => run(async () => setRows(await get<Row[]>(`/reports/analysis?from=${from}&to=${to}`)))}>Xem</button>
        <ExportButton fileName={`Phan_tich_theo_ma_${from}_${to}`} sheet="Phân tích" disabled={!rows}
          columns={[{ header: 'Nhóm' }, { header: 'Mã' }, { header: 'Tên', width: 30 }, { header: 'TK' }, { header: 'Tên tài khoản', width: 34 },
            { header: 'Phát sinh Nợ', numeric: true }, { header: 'Phát sinh Có', numeric: true }, { header: 'Có − Nợ', numeric: true }]}
          rows={(rows ?? []).map(r => [r.kind, r.code, r.name, r.accountCode, r.accountName, r.debit, r.credit, net(r)])} />
      </div>
      <ErrorBox error={error} />
      {rows && <div className="scroll"><table className="grid">
        <thead><tr><th>Mã phân tích</th><th>Tài khoản</th><th className="num">Phát sinh Nợ</th><th className="num">Phát sinh Có</th><th className="num">Có − Nợ</th></tr></thead>
        <tbody>
          {groups.map(g => { const t = total(g); return [
            <tr key={g[0].analysisCodeId} style={{ fontWeight: 600 }}><td>{g[0].kind}: {g[0].code} – {g[0].name}</td><td></td>
              <td className="num">{formatMoney(t.debit)}</td><td className="num">{formatMoney(t.credit)}</td><td className="num">{formatMoney(net(t))}</td></tr>,
            ...g.map(r => <tr key={r.analysisCodeId + r.accountCode}><td></td><td>{r.accountCode} – {r.accountName}</td>
              <td className="num">{r.debit === '0' ? '' : formatMoney(r.debit)}</td><td className="num">{r.credit === '0' ? '' : formatMoney(r.credit)}</td><td className="num">{formatMoney(net(r))}</td></tr>),
          ] })}
          {rows.length === 0 && <tr><td colSpan={5} className="muted">Không có dòng chứng từ nào gắn mã phân tích trong khoảng ngày. Khai báo mã ở Thiết lập → Mã phân tích, rồi chọn mã trên dòng chứng từ.</td></tr>}
        </tbody>
      </table></div>}
      <p className="muted">Không tính bút toán kết chuyển. "Có − Nợ" dương là lãi của mã đó khi cả dòng doanh thu và dòng chi phí đều gắn mã. Mã gắn được trên chứng từ nhập tay, phiếu thu / chi, báo Nợ / Có, phiếu xuất kho (vào dòng giá vốn) và hóa đơn (dòng doanh thu, dòng chi phí dịch vụ).</p>
    </>
  )
}
