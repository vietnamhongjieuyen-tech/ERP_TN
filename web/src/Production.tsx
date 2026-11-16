import { useCallback, useEffect, useState } from 'react'
import { askText } from './dialogs'
import { get, post, type Period } from './api'
import { AccountSelect, ErrorBox, useAction, useCatalog } from './common'
import { formatMoney, parseMoney } from './money'

interface CostObject { id: string; code: string; name: string; itemId: string; wipAccountId: string; plannedUnitCost: string; isActive: boolean }
interface Item { id: string; code: string; name: string; unit: string }
interface Preview {
  wipBalanceBefore: string; provisionalValue: string; closingWip: string; outputQuantity: string; actualCost: string; unitCost: string; difference: string
  receipts: { documentId: string; documentNumber: string; lineNo: number; date: string; quantity: string; value: string }[]
}
interface Run { id: string; costObjectId: string; periodStart: string; outputQuantity: string; actualCost: string; unitCost: string; closingWip: string; difference: string; status: 'Active' | 'Reversed' }

export function ProductionView() {
  const { accounts } = useCatalog()
  const [objects, setObjects] = useState<CostObject[]>([])
  const [items, setItems] = useState<Item[]>([])
  const [periods, setPeriods] = useState<Period[]>([])
  const [runs, setRuns] = useState<Run[]>([])
  const [form, setForm] = useState({ code: '', name: '', itemId: '', wipAccountId: '', plannedUnitCost: '' })
  const [pick, setPick] = useState({ costObjectId: '', periodId: '', closingWip: '0', note: '' })
  const [preview, setPreview] = useState<Preview | null>(null)
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()

  const load = useCallback(() => run(async () => {
    setObjects(await get<CostObject[]>('/production/cost-objects'))
    setItems(await get<Item[]>('/items'))
    setPeriods(await get<Period[]>('/periods'))
    setRuns(await get<Run[]>('/production/cost-runs'))
  }), [run])
  useEffect(() => { load() }, [load])

  const body = () => {
    const w = parseMoney(pick.closingWip)
    if (w == null) throw new Error('Chi phí dở dang cuối kỳ không hợp lệ.')
    return { costObjectId: pick.costObjectId, periodId: pick.periodId, closingWip: w, note: pick.note }
  }
  const objName = (id: string) => objects.find(o => o.id === id)?.code ?? ''
  const latest = (r: Run) => !runs.some(x => x.costObjectId === r.costObjectId && x.status === 'Active' && x.periodStart > r.periodStart)

  return (
    <>
      <div className="toolbar"><h2>Giá thành sản xuất</h2><button onClick={() => load()}>Tải lại</button></div>
      <p className="muted">Chi phí tập hợp vào TK dở dang của từng đối tượng (xuất nguyên liệu đối ứng TK dở dang; lương, chi phí chung kết chuyển bằng bộ quy tắc kết chuyển). Thành phẩm nhập kho trong kỳ theo giá kế hoạch (phiếu nhập đối ứng TK dở dang). Cuối kỳ nhập dở dang cuối kỳ [cách đánh giá CẦN XÁC MINH] → giá thành thực tế; chênh lệch so với giá kế hoạch: phần còn tồn vào tồn kho, phần đã xuất vào giá vốn.</p>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}

      <section className="panel">
        <h3>Đối tượng tính giá thành</h3>
        <table className="grid"><thead><tr><th>Mã</th><th>Tên</th><th>Thành phẩm</th><th>TK dở dang</th><th className="num">Giá kế hoạch / đơn vị</th></tr></thead>
          <tbody>{objects.map(o => <tr key={o.id}><td>{o.code}</td><td>{o.name}</td><td>{items.find(i => i.id === o.itemId)?.code}</td>
            <td>{accounts.find(a => a.id === o.wipAccountId)?.code}</td><td className="num">{formatMoney(o.plannedUnitCost)}</td></tr>)}</tbody></table>
        <div className="form-row" style={{ marginTop: 8 }}>
          <label>Mã <input value={form.code} onChange={e => setForm(f => ({ ...f, code: e.target.value }))} /></label>
          <label>Tên <input value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} /></label>
          <label>Thành phẩm <select value={form.itemId} onChange={e => setForm(f => ({ ...f, itemId: e.target.value }))}><option value="">— chọn —</option>
            {items.map(i => <option key={i.id} value={i.id}>{i.code} – {i.name}</option>)}</select></label>
          <label>TK dở dang <AccountSelect accounts={accounts} value={form.wipAccountId} onChange={v => setForm(f => ({ ...f, wipAccountId: v }))}
            filter={a => a.cashKind === 'None' && a.openItemNature === 'None'} ariaLabel="TK dở dang" /></label>
          <label>Giá kế hoạch / đơn vị <input inputMode="decimal" value={form.plannedUnitCost} onChange={e => setForm(f => ({ ...f, plannedUnitCost: e.target.value }))} /></label>
          <button disabled={busy || !form.code || !form.itemId || !form.wipAccountId} onClick={() => run(async () => {
            const planned = parseMoney(form.plannedUnitCost || '0'); if (planned == null) throw new Error('Giá kế hoạch không hợp lệ.')
            await post('/production/cost-objects', { ...form, plannedUnitCost: planned }); setForm({ code: '', name: '', itemId: '', wipAccountId: '', plannedUnitCost: '' }); await load()
          })}>Thêm đối tượng</button>
        </div>
      </section>

      <section className="panel">
        <h3>Tính giá thành cuối kỳ</h3>
        <div className="form-row">
          <label>Đối tượng <select value={pick.costObjectId} onChange={e => { setPreview(null); setPick(p => ({ ...p, costObjectId: e.target.value })) }}><option value="">— chọn —</option>
            {objects.map(o => <option key={o.id} value={o.id}>{o.code} – {o.name}</option>)}</select></label>
          <label>Kỳ <select value={pick.periodId} onChange={e => { setPreview(null); setPick(p => ({ ...p, periodId: e.target.value })) }}><option value="">— chọn —</option>
            {periods.filter(p => p.status === 'Open').map(p => <option key={p.id} value={p.id}>{p.startDate.slice(0, 7)}</option>)}</select></label>
          <label>Dở dang cuối kỳ <input inputMode="decimal" value={pick.closingWip} onChange={e => { setPreview(null); setPick(p => ({ ...p, closingWip: e.target.value })) }} /></label>
          <label>Căn cứ đánh giá dở dang <input value={pick.note} onChange={e => setPick(p => ({ ...p, note: e.target.value }))} placeholder="Biên bản kiểm kê dở dang…" /></label>
          <button disabled={busy || !pick.costObjectId || !pick.periodId} onClick={() => run(async () => setPreview(await post<Preview>('/production/cost-runs/preview', body())))}>Xem trước</button>
          <button className="primary" disabled={busy || !preview} onClick={() => run(async () => {
            await post('/production/cost-runs', body()); setPreview(null); setInfo('Đã tính giá thành và ghi sổ.'); await load()
          })}>Tính giá thành</button>
        </div>
        {preview && <>
          <table className="grid"><tbody>
            <tr><td>Số dư TK dở dang cuối kỳ (trước khi tính)</td><td className="num">{formatMoney(preview.wipBalanceBefore)}</td></tr>
            <tr><td>Giá trị thành phẩm đã nhập theo giá kế hoạch</td><td className="num">{formatMoney(preview.provisionalValue)}</td></tr>
            <tr><td>Dở dang cuối kỳ</td><td className="num">{formatMoney(preview.closingWip)}</td></tr>
            <tr><td><strong>Tổng giá thành thực tế</strong> · sản lượng {formatMoney(preview.outputQuantity)}</td><td className="num"><strong>{formatMoney(preview.actualCost)}</strong></td></tr>
            <tr><td>Giá thành đơn vị</td><td className="num">{formatMoney(preview.unitCost)}</td></tr>
            <tr><td>Chênh lệch so với giá kế hoạch (phân bổ theo số lượng từng phiếu nhập)</td><td className="num">{formatMoney(preview.difference)}</td></tr>
          </tbody></table>
          <table className="grid"><thead><tr><th>Phiếu nhập thành phẩm</th><th>Ngày</th><th className="num">Số lượng</th><th className="num">Giá trị đã nhập</th></tr></thead>
            <tbody>{preview.receipts.map(r => <tr key={`${r.documentId}|${r.lineNo}`}><td>{r.documentNumber}</td><td>{r.date}</td><td className="num">{formatMoney(r.quantity)}</td><td className="num">{formatMoney(r.value)}</td></tr>)}</tbody></table>
        </>}
      </section>

      <section className="panel">
        <h3>Các lần tính giá thành</h3>
        <table className="grid"><thead><tr><th>Đối tượng</th><th>Kỳ</th><th className="num">Sản lượng</th><th className="num">Giá thành</th><th className="num">Đơn vị</th><th className="num">Dở dang cuối</th><th>Trạng thái</th><th /></tr></thead>
          <tbody>{runs.map(r => <tr key={r.id}><td>{objName(r.costObjectId)}</td><td>{r.periodStart.slice(0, 7)}</td><td className="num">{formatMoney(r.outputQuantity)}</td>
            <td className="num">{formatMoney(r.actualCost)}</td><td className="num">{formatMoney(r.unitCost)}</td><td className="num">{formatMoney(r.closingWip)}</td>
            <td>{r.status === 'Active' ? 'Hiệu lực' : 'Đã hủy'}</td>
            <td>{r.status === 'Active' && latest(r) && <button disabled={busy} onClick={async () => {
              const reason = await askText('Lý do hủy tính giá thành:')
              if (reason) run(async () => { await post(`/production/cost-runs/${r.id}/reverse`, { reason }); setInfo('Đã hủy lần tính giá thành.'); await load() })
            }}>Hủy</button>}</td></tr>)}</tbody></table>
      </section>
    </>
  )
}
