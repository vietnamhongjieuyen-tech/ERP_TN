import { useCallback, useEffect, useState } from 'react'
import { askText } from './dialogs'
import { get, post, saveOnce } from './api'
import { ErrorBox, PartnerSelect, useAction, useCatalog, useRequestKey } from './common'
import { useUnsaved } from './leave'
import { formatMoney, parseMoney, today } from './money'
import { DateInput } from './DateInput'

export interface Order { id: string; number: string; kind: string; partnerId: string; contractNo: string | null; orderDate: string; description: string; overDeliveryTolerancePercent: string; status: string; statusReason: string | null; lines: { lineNo: number; itemId: string; quantity: string; basis: string; unitPrice: string; vatRate: string | null }[] }
interface Item { id: string; code: string; name: string; unit: string; tracksDryness: boolean }
interface FulfilLine { lineNo: number; itemCode: string; ordered: string; basis: string; unitPrice: string; stockPosted: string; stockDraft: string; bdmt: string; invoiced: string; invoicedAmount: string; returnedQty: string; returnedAmount: string; outstanding: string }
interface Fulfil { number: string; status: string; lines: FulfilLine[]; invoices: { invoiceId: string; number: string; invoiceSeries: string; invoiceNo: string; purpose: string; total: string; settled: string; open: string }[] }
interface LineDraft { itemId: string; qty: string; basis: 'Quantity' | 'Bdmt'; price: string; vat: string }

const STATUS_VI: Record<string, string> = { Draft: 'Nháp', Confirmed: 'Đã xác nhận', Closed: 'Đã đóng', Cancelled: 'Đã hủy' }

/** Đơn mua / đơn bán: kiểm soát số lượng nhận/giao (có dung sai) và theo dõi đơn → phiếu kho → hóa đơn → thanh toán. */
export function OrdersView() {
  const { partners } = useCatalog()
  const [items, setItems] = useState<Item[]>([])
  const [kind, setKind] = useState<'Sales' | 'Purchase'>('Sales')
  const [list, setList] = useState<Order[]>([])
  const [head, setHead] = useState({ partnerId: '', contractNo: '', orderDate: today(), description: '', tolerance: '0' })
  const [lines, setLines] = useState<LineDraft[]>([{ itemId: '', qty: '', basis: 'Bdmt', price: '', vat: '' }])
  useUnsaved(lines.some(l => l.itemId || l.qty.trim() || l.price.trim()), 'đơn hàng')
  const [detail, setDetail] = useState<Fulfil | null>(null)
  const [info, setInfo] = useState('')
  const { busy, error, setError, run } = useAction()
  const rk = useRequestKey()
  useEffect(() => { get<Item[]>('/items').then(setItems).catch(() => setItems([])) }, [])
  const load = useCallback(() => run(async () => setList(await get<Order[]>(`/orders?kind=${kind}`))), [run, kind])
  useEffect(() => { load() }, [load])
  const upd = (i: number, p: Partial<LineDraft>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l)))
  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })

  const save = () => run(async () => {
    const parsed = lines.map(l => ({ ...l, q: parseMoney(l.qty), p: parseMoney(l.price), v: l.vat === '' ? null : parseMoney(l.vat) }))
    if (parsed.some(l => !l.itemId || !l.q || l.p == null)) { setError(new Error('Dòng đơn hàng thiếu vật tư, số lượng hoặc đơn giá.')); return }
    const item = (id: string) => items.find(i => i.id === id)
    await saveOnce('POST', '/orders', {
      kind, partnerId: head.partnerId, orderDate: head.orderDate, description: head.description, contractNo: head.contractNo || null,
      overDeliveryTolerancePercent: parseMoney(head.tolerance) ?? '0', currency: null, expectedDate: null,
      lines: parsed.map(l => ({ itemId: l.itemId, quantity: l.q, unitPrice: l.p, basis: item(l.itemId)?.tracksDryness ? l.basis : 'Quantity', vatRate: l.v, description: null })),
    }, rk.key())
    rk.done()
    setInfo('Đã lập đơn hàng (nháp). Người khác có quyền duyệt sẽ xác nhận đơn.')
    setLines([{ itemId: '', qty: '', basis: 'Bdmt', price: '', vat: '' }])
    await load()
  })

  const n = (v: string) => (v === '0' ? '' : formatMoney(v))
  return (
    <>
      <div className="toolbar">
        <h2>Đơn hàng</h2>
        <button className={kind === 'Sales' ? 'primary' : ''} onClick={() => setKind('Sales')}>Đơn bán</button>
        <button className={kind === 'Purchase' ? 'primary' : ''} onClick={() => setKind('Purchase')}>Đơn mua</button>
      </div>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="panel">
        <div className="form-row">
          <label>{kind === 'Sales' ? 'Khách hàng' : 'Nhà cung cấp'}<PartnerSelect partners={partners} value={head.partnerId} onChange={v => setHead(h => ({ ...h, partnerId: v }))} /></label>
          <label>Số hợp đồng<input value={head.contractNo} onChange={e => setHead(h => ({ ...h, contractNo: e.target.value }))} /></label>
          <label>Ngày đơn<DateInput value={head.orderDate} onChange={e => setHead(h => ({ ...h, orderDate: e.target.value }))} /></label>
          <label>Dung sai giao vượt %<input className="num" size={4} value={head.tolerance} onChange={e => setHead(h => ({ ...h, tolerance: e.target.value }))} /></label>
          <label style={{ flex: 1, minWidth: 200 }}>Diễn giải<input value={head.description} onChange={e => setHead(h => ({ ...h, description: e.target.value }))} /></label>
          <button className="primary" style={{ alignSelf: 'flex-end' }} disabled={busy} onClick={save}>Lưu đơn nháp</button>
        </div>
        <table className="grid">
          <thead><tr><th>#</th><th>Vật tư</th><th className="num">Số lượng (ĐVT chính)</th><th>Giá theo</th><th className="num">Đơn giá</th><th className="num">Thuế %</th><th /></tr></thead>
          <tbody>{lines.map((l, i) => (
            <tr key={i}><td>{i + 1}</td>
              <td><select aria-label="Vật tư" value={l.itemId} onChange={e => upd(i, { itemId: e.target.value })}><option value="">— chọn —</option>{items.map(it => <option key={it.id} value={it.id}>{it.code} – {it.name} ({it.unit})</option>)}</select></td>
              <td><input className="num" aria-label="Số lượng" value={l.qty} onChange={e => upd(i, { qty: e.target.value })} /></td>
              <td><select aria-label="Giá theo" value={items.find(it => it.id === l.itemId)?.tracksDryness ? l.basis : 'Quantity'} disabled={!items.find(it => it.id === l.itemId)?.tracksDryness} onChange={e => upd(i, { basis: e.target.value as LineDraft['basis'] })}><option value="Bdmt">BDMT</option><option value="Quantity">Số lượng</option></select></td>
              <td><input className="num" aria-label="Đơn giá" value={l.price} onChange={e => upd(i, { price: e.target.value })} /></td>
              <td><input className="num" aria-label="Thuế suất" size={4} value={l.vat} placeholder="KCT" onChange={e => upd(i, { vat: e.target.value })} /></td>
              <td><button aria-label="Xóa dòng" onClick={() => setLines(ls => ls.filter((_, j) => j !== i))}>✕</button></td></tr>))}
          </tbody>
        </table>
        <button style={{ marginTop: 8 }} onClick={() => setLines(ls => [...ls, { itemId: '', qty: '', basis: 'Bdmt', price: '', vat: '' }])}>+ Thêm dòng</button>
      </div>
      {detail && (
        <div className="panel">
          <div className="toolbar"><strong>Tiến độ đơn {detail.number}</strong><button onClick={() => setDetail(null)}>Đóng</button></div>
          <table className="grid">
            <thead><tr><th>Dòng</th><th>Vật tư</th><th className="num">Đặt</th><th className="num">Đã {kind === 'Sales' ? 'giao' : 'nhận'}</th><th className="num">Phiếu nháp</th><th className="num">BDMT</th><th className="num">Đã lập HĐ</th><th className="num">Tiền HĐ</th><th className="num">Trả lại</th><th className="num">Còn lại</th></tr></thead>
            <tbody>{detail.lines.map(l => <tr key={l.lineNo}><td>{l.lineNo}</td><td>{l.itemCode}</td>
              {[l.ordered, l.stockPosted, l.stockDraft, l.bdmt, l.invoiced, l.invoicedAmount, l.returnedQty, l.outstanding].map((v, i) => <td key={i} className="num">{n(v)}</td>)}</tr>)}</tbody>
          </table>
          <table className="grid" style={{ marginTop: 8 }}>
            <thead><tr><th>Hóa đơn</th><th>Loại</th><th className="num">Tổng thanh toán</th><th className="num">Đã thu/trả</th><th className="num">Còn lại</th></tr></thead>
            <tbody>{detail.invoices.map(i => <tr key={i.invoiceId}><td>{i.number} · {i.invoiceSeries}/{i.invoiceNo}</td><td>{i.purpose}</td><td className="num">{formatMoney(i.total)}</td><td className="num">{formatMoney(i.settled)}</td><td className="num">{formatMoney(i.open)}</td></tr>)}
              {detail.invoices.length === 0 && <tr><td colSpan={5} className="muted">Chưa có hóa đơn.</td></tr>}</tbody>
          </table>
        </div>
      )}
      <div className="scroll">
        <table className="grid">
          <thead><tr><th>Số đơn</th><th>Ngày</th><th>Đối tượng</th><th>Hợp đồng</th><th>Diễn giải</th><th>Trạng thái</th><th /></tr></thead>
          <tbody>{list.map(o => (
            <tr key={o.id}><td>{o.number}</td><td>{o.orderDate}</td><td>{partners.find(p => p.id === o.partnerId)?.code}</td><td>{o.contractNo}</td><td>{o.description}</td>
              <td><span className="badge">{STATUS_VI[o.status] ?? o.status}</span></td>
              <td style={{ display: 'flex', gap: 4 }}>
                <button onClick={() => run(async () => setDetail(await get<Fulfil>(`/orders/${o.id}/fulfilment`)))}>Tiến độ</button>
                {o.status === 'Draft' && <button className="primary" onClick={() => act(() => post(`/orders/${o.id}/confirm`), `Đã xác nhận ${o.number}.`)}>Xác nhận</button>}
                {o.status === 'Confirmed' && <button onClick={async () => { const r = await askText('Lý do đóng đơn:'); if (r) act(() => post(`/orders/${o.id}/close`, { reason: r }), `Đã đóng ${o.number}.`) }}>Đóng đơn</button>}
                {(o.status === 'Draft' || o.status === 'Confirmed') && <button className="danger" onClick={async () => { const r = await askText('Lý do hủy đơn:'); if (r) act(() => post(`/orders/${o.id}/cancel`, { reason: r }), `Đã hủy ${o.number}.`) }}>Hủy</button>}
              </td></tr>))}
            {list.length === 0 && <tr><td colSpan={7} className="muted">Chưa có đơn hàng.</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  )
}
