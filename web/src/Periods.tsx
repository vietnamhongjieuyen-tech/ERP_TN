import { useCallback, useEffect, useState } from 'react'
import { askConfirm, askText } from './dialogs'
import { get, post, type Period, type Voucher } from './api'
import { ErrorBox, useAction } from './common'

export function PeriodsView() {
  const [periods, setPeriods] = useState<Period[]>([])
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => setPeriods(await get<Period[]>('/periods'))), [run])
  useEffect(() => { load() }, [load])

  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })

  return (
    <>
      <div className="toolbar"><h2>Kỳ kế toán, khóa sổ và kết chuyển</h2><button onClick={() => load()}>Tải lại</button></div>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <table className="grid">
        <thead><tr><th>Kỳ</th><th>Từ</th><th>Đến</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
        <tbody>{periods.map(p => (
          <tr key={p.id}>
            <td>{p.periodNo}</td><td>{p.startDate}</td><td>{p.endDate}</td>
            <td><span className={`badge ${p.status}`}>{p.status === 'Open' ? 'Mở' : 'Đã khóa'}</span></td>
            <td style={{ display: 'flex', gap: 6 }}>
              {p.status === 'Open' && <>
                <button disabled={busy} onClick={() => act(async () => {
                  const v = await post<Voucher>('/closing/generate', { periodId: p.id })
                  setInfo(`Đã tạo chứng từ kết chuyển ${v.number} (Nháp) — duyệt và ghi sổ ở màn hình Chứng từ.`)
                }, 'Đã tạo chứng từ kết chuyển (Nháp).')}>Tạo bút toán kết chuyển</button>
                <button disabled={busy} onClick={async () => { if (await askConfirm(`Khóa sổ kỳ ${p.periodNo}?`)) act(() => post(`/periods/${p.id}/lock`), `Đã khóa kỳ ${p.periodNo}.`) }}>Khóa sổ</button>
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
