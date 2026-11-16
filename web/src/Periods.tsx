import { useCallback, useEffect, useState } from 'react'
import { askConfirm, askText } from './dialogs'
import { get, post, type Period, type Voucher } from './api'
import { ErrorBox, useAction } from './common'
import { goTo, takeParams } from './nav'

interface ChecklistItem { key: string; title: string; status: 'Done' | 'Todo' | 'Optional' | 'NotApplicable'; detail: string; screen: string }
interface Checklist { period: { id: string; periodNo: number; startDate: string; endDate: string; status: string }; readyToLock: boolean; items: ChecklistItem[] }

const CHECK_VI: Record<ChecklistItem['status'], string> = { Done: '✔ Xong', Todo: '● Cần làm', Optional: '○ Nên xem', NotApplicable: '– Không áp dụng' }
const SCREEN_VI: Record<string, string> = {
  vouchers: 'Chứng từ', stock: 'Kho', bank: 'Ngân hàng', production: 'Giá thành', assets: 'Tài sản / CCDC', loans: 'Vay', fx: 'Ngoại tệ',
  arap: 'Công nợ', tb: 'Cân đối phát sinh', periods: '',
}

export function PeriodsView() {
  const [boot] = useState(() => takeParams('periods'))   // opened from the start page with a period to check
  const [periods, setPeriods] = useState<Period[]>([])
  const [info, setInfo] = useState('')
  const [checkId, setCheckId] = useState(boot?.periodId ?? '')
  const [check, setCheck] = useState<Checklist | null>(null)
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => {
    setPeriods(await get<Period[]>('/periods'))
    setCheck(checkId ? await get<Checklist>(`/closing/checklist?periodId=${checkId}`) : null)
  }), [run, checkId])
  useEffect(() => { load() }, [load])

  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })
  const lock = async (p: { id: string; periodNo: number }, ready: boolean | null) => {
    const q = ready === false ? `Kỳ ${p.periodNo} còn việc cần làm trong danh sách kiểm tra. Vẫn khóa sổ?` : `Khóa sổ kỳ ${p.periodNo}?`
    if (await askConfirm(q)) act(() => post(`/periods/${p.id}/lock`), `Đã khóa kỳ ${p.periodNo}.`)
  }

  return (
    <>
      <div className="toolbar"><h2>Kỳ kế toán, khóa sổ và kết chuyển</h2><button onClick={() => load()}>Tải lại</button></div>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      {check && (
        <div className="panel">
          <div className="toolbar">
            <strong>Danh sách kiểm tra khóa sổ — kỳ {check.period.periodNo} ({check.period.startDate} → {check.period.endDate})</strong>
            <span className={`badge ${check.readyToLock ? 'Posted' : 'Submitted'}`}>{check.readyToLock ? 'Sẵn sàng khóa sổ' : 'Còn việc cần làm'}</span>
            <span style={{ marginLeft: 'auto' }} />
            {check.period.status === 'Open' && <button className="primary" disabled={busy} onClick={() => lock(check.period, check.readyToLock)}>Khóa sổ kỳ {check.period.periodNo}</button>}
            <button onClick={() => setCheckId('')}>Đóng</button>
          </div>
          <table className="grid">
            <thead><tr><th>#</th><th>Việc</th><th>Trạng thái</th><th>Chi tiết</th><th></th></tr></thead>
            <tbody>{check.items.map((i, n) => (
              <tr key={i.key} className={i.status === 'Todo' ? 'warn' : undefined}>
                <td>{n + 1}</td><td>{i.title}</td><td>{CHECK_VI[i.status]}</td><td style={{ whiteSpace: 'normal' }}>{i.detail}</td>
                <td>{SCREEN_VI[i.screen] && i.status !== 'NotApplicable' && <button onClick={() => goTo(i.screen)}>Mở {SCREEN_VI[i.screen]}</button>}</td>
              </tr>))}
            </tbody>
          </table>
          <p className="muted">Thứ tự trên xuống là thứ tự nên làm. "Cần làm" chặn việc khóa sổ sạch; "Nên xem" không chặn. Danh sách chỉ đọc số liệu, không ghi sổ gì.</p>
        </div>
      )}
      <table className="grid">
        <thead><tr><th>Kỳ</th><th>Từ</th><th>Đến</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
        <tbody>{periods.map(p => (
          <tr key={p.id} className={p.id === checkId ? 'selected' : undefined}>
            <td>{p.periodNo}</td><td>{p.startDate}</td><td>{p.endDate}</td>
            <td><span className={`badge ${p.status}`}>{p.status === 'Open' ? 'Mở' : 'Đã khóa'}</span></td>
            <td style={{ display: 'flex', gap: 6 }}>
              <button disabled={busy} onClick={() => setCheckId(p.id)}>Kiểm tra khóa sổ</button>
              {p.status === 'Open' && <>
                <button disabled={busy} onClick={() => act(async () => {
                  const v = await post<Voucher>('/closing/generate', { periodId: p.id })
                  setInfo(`Đã tạo chứng từ kết chuyển ${v.number} (Nháp) — duyệt và ghi sổ ở màn hình Chứng từ.`)
                }, 'Đã tạo chứng từ kết chuyển (Nháp).')}>Tạo bút toán kết chuyển</button>
                <button disabled={busy} onClick={() => lock(p, p.id === checkId && check ? check.readyToLock : null)}>Khóa sổ</button>
              </>}
              {p.status === 'Locked' && <button disabled={busy} onClick={async () => {
                const reason = await askText('Lý do mở lại kỳ (bắt buộc, được ghi nhật ký):')
                if (reason) act(() => post(`/periods/${p.id}/reopen`, { reason }), `Đã mở lại kỳ ${p.periodNo}.`)
              }}>Mở lại</button>}
            </td>
          </tr>))}
        </tbody>
      </table>
    </>
  )
}
