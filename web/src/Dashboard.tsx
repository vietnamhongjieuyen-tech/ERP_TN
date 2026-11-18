import { useEffect, useState } from 'react'
import { get } from './api'
import { ErrorBox, useAction } from './common'
import { formatMoney, today } from './money'
import { goTo } from './nav'

interface PeriodSummary { id: string; periodNo: number; startDate: string; endDate: string; status: string }
interface Dashboard {
  asOf: string; myDrafts: number; drafts: number; awaitingApproval: number; awaitingPosting: number; draftStockDocuments: number
  unmatchedStatementLines: number; cash: { accountId: string; accountCode: string; accountName: string; kind: string; balance: string }[] | null
  overdueReceivables: string | null; overduePayables: string | null; currentPeriod: PeriodSummary | null; oldestOpenPeriod: PeriodSummary | null
}

const d = (s: string) => s.split('-').reverse().join('/')

/** Start page: what is waiting for the user, cash on hand and where the month-end close stands. Every tile opens its screen. */
export function DashboardView() {
  const [data, setData] = useState<Dashboard | null>(null)
  const { error, run } = useAction()
  useEffect(() => { run(async () => setData(await get<Dashboard>(`/dashboard?asOf=${today()}`))) }, [run])
  if (!data) return <><div className="toolbar"><h2>Tổng quan</h2></div><ErrorBox error={error} />{!error && <span className="muted">Đang tải…</span>}</>

  const year = today().slice(0, 4) + '-01-01'
  const work: { label: string; count: number; hint: string; go: () => void }[] = [
    { label: 'Chứng từ nháp của tôi', count: data.myDrafts, hint: 'Hoàn thiện rồi gửi duyệt', go: () => goTo('vouchers', { status: 'Draft', from: year }) },
    { label: 'Chờ duyệt', count: data.awaitingApproval, hint: 'Người duyệt xem và duyệt / trả lại', go: () => goTo('vouchers', { status: 'Submitted', from: year }) },
    { label: 'Đã duyệt, chờ ghi sổ', count: data.awaitingPosting, hint: 'Ghi sổ để vào sổ cái', go: () => goTo('vouchers', { status: 'Approved', from: year }) },
    { label: 'Phiếu kho nháp', count: data.draftStockDocuments, hint: 'Ghi sổ hoặc hủy phiếu', go: () => goTo('stock') },
    { label: 'Dòng sao kê chưa khớp', count: data.unmatchedStatementLines, hint: 'Phí, lãi, giao dịch chưa hạch toán', go: () => goTo('bank') },
  ]
  const p = data.oldestOpenPeriod
  return (
    <>
      <div className="toolbar"><h2>Tổng quan</h2><span className="muted">Số liệu đến {d(data.asOf)}</span></div>
      <ErrorBox error={error} />
      <div className="tiles">
        {work.map(w => (
          <button key={w.label} className={'tile' + (w.count > 0 ? ' due' : '')} onClick={w.go} title={w.hint}>
            <span className="n">{w.count}</span><span>{w.label}</span><span className="muted">{w.count > 0 ? w.hint : 'Không có'}</span>
          </button>))}
      </div>
      <div className="form-grid">
        <div className="panel">
          <strong>Khóa sổ</strong>
          {p ? <>
            <p>Kỳ mở sớm nhất: <b>kỳ {p.periodNo}</b> ({d(p.startDate)} – {d(p.endDate)}).
              {p.endDate < data.asOf ? ' Kỳ này đã kết thúc, nên kiểm tra và khóa sổ.' : ' Kỳ này đang diễn ra.'}</p>
            <button onClick={() => goTo('periods', { periodId: p.id })}>Mở danh sách kiểm tra khóa sổ kỳ {p.periodNo}</button>
          </> : <p className="muted">Không có kỳ nào đang mở{data.currentPeriod ? '' : ' và chưa có kỳ kế toán chứa ngày hôm nay — tạo năm tài chính ở Thiết lập'}.</p>}
        </div>
        {data.cash && <div className="panel">
          <strong>Tiền</strong>
          <table className="grid">
            <tbody>{data.cash.map(c => (
              <tr key={c.accountId} className="link" title="Mở sổ quỹ / sổ tiền gửi" onClick={() => goTo('ledger', { accountId: c.accountId, from: today().slice(0, 8) + '01', to: today() })}>
                <td>{c.accountCode} – {c.accountName}</td><td className="num">{formatMoney(c.balance)}</td></tr>))}
              {data.cash.length === 0 && <tr><td className="muted">Chưa khai báo tài khoản tiền.</td></tr>}
            </tbody>
          </table>
        </div>}
        {data.cash && <div className="panel">
          <strong>Công nợ quá hạn</strong>
          <table className="grid"><tbody>
            <tr className="link" onClick={() => goTo('arap')}><td>Phải thu quá hạn</td><td className="num">{data.overdueReceivables === null ? <span className="muted" title="Chưa có tài khoản nào đánh dấu theo dõi công nợ phải thu theo hóa đơn (Thiết lập → Hệ thống tài khoản)">chưa khai báo</span> : formatMoney(data.overdueReceivables)}</td></tr>
            <tr className="link" onClick={() => goTo('arap')}><td>Phải trả quá hạn</td><td className="num">{data.overduePayables === null ? <span className="muted" title="Chưa có tài khoản nào đánh dấu theo dõi công nợ phải trả theo hóa đơn (Thiết lập → Hệ thống tài khoản)">chưa khai báo</span> : formatMoney(data.overduePayables)}</td></tr>
          </tbody></table>
          <p className="muted">Số liệu đến ngày {d(data.asOf)}, đơn vị: đồng; chỉ gồm chứng từ đã ghi sổ.</p>
          <p className="muted">Chi tiết theo đối tượng và tuổi nợ ở màn hình Công nợ.</p>
        </div>}
      </div>
    </>
  )
}
