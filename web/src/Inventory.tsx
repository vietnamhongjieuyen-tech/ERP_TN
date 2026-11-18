import { useCallback, useEffect, useState } from 'react'
import { MoneyInput } from './MoneyInput'
import { askDate, askText } from './dialogs'
import { get, post, saveOnce, session, type Period } from './api'
import { AccountSelect, ErrorBox, ExportButton, PartnerSelect, PrintSheet, printSheet, useAction, useCatalog, useRequestKey } from './common'
import { useUnsaved } from './leave'
import { FORM_FOOT, FormHeader } from './forms'
import { addMoney, amountInWords, divideMoney, formatMoney, parseMoney, today } from './money'
import type { Order } from './Orders'
import { useAnalysisOptions } from './Analysis'
import { SearchSelect } from './combo'
import { DateInput } from './DateInput'

interface Warehouse { id: string; code: string; name: string; policies: { method: string; effectiveFrom: string }[] }
interface Item { id: string; code: string; name: string; unit: string }
interface StockDoc { id: string; number: string; type: string; warehouseId: string; toWarehouseId: string | null; postingDate: string; description: string; status: string; voucherId: string | null; lines: { lineNo: number; itemId: string; quantity: string; amount: string | null }[] }

const METHOD_VI: Record<string, string> = { Fifo: 'Nhập trước xuất trước', MovingAverage: 'Bình quân tức thời', MonthlyAverage: 'Bình quân cuối tháng' }
const TYPE_VI: Record<string, string> = { Receipt: 'Phiếu nhập', Issue: 'Phiếu xuất', Transfer: 'Điều chuyển' }
const STATUS_VI: Record<string, string> = { Draft: 'Nháp', Posted: 'Đã ghi sổ', Reversed: 'Đã đảo', Cancelled: 'Đã hủy' }
const whLabel = (w: Warehouse) => `${w.code} – ${w.name}${w.policies.length ? ' · ' + METHOD_VI[w.policies[w.policies.length - 1].method] : ' · chưa có phương pháp tính giá'}`

function useStockCatalog() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([])
  const [items, setItems] = useState<Item[]>([])
  useEffect(() => {
    get<Warehouse[]>('/warehouses').then(setWarehouses).catch(() => setWarehouses([]))
    get<Item[]>('/items').then(setItems).catch(() => setItems([]))
  }, [])
  return { warehouses, items }
}

export function InventoryView() {
  const [tab, setTab] = useState<'docs' | 'summary' | 'card' | 'costing' | 'bdmt'>('docs')
  const tabs = { docs: 'Phiếu kho', summary: 'Nhập – xuất – tồn', card: 'Thẻ kho', costing: 'Tính giá cuối tháng', bdmt: 'MT / BDMT' } as const
  return (
    <>
      <div className="toolbar">
        <h2>Kho</h2>
        {(Object.keys(tabs) as (keyof typeof tabs)[]).map(k => <button key={k} className={tab === k ? 'primary' : ''} onClick={() => setTab(k)}>{tabs[k]}</button>)}
      </div>
      {tab === 'docs' && <StockDocs />}
      {tab === 'summary' && <Summary />}
      {tab === 'card' && <StockCard />}
      {tab === 'costing' && <Costing />}
      {tab === 'bdmt' && <Bdmt />}
    </>
  )
}

interface LineDraft { itemId: string; quantity: string; amount: string; counterAccountId: string; orderLine: string; analysisCodeId?: string }

function StockDocs() {
  const { accounts, partners } = useCatalog()
  const { warehouses, items } = useStockCatalog()
  const [list, setList] = useState<StockDoc[]>([])
  const [type, setType] = useState<'Receipt' | 'Issue' | 'Transfer'>('Receipt')
  const [warehouseId, setWarehouseId] = useState('')
  const [toWarehouseId, setToWarehouseId] = useState('')
  const [date, setDate] = useState(today())
  const [partnerId, setPartnerId] = useState('')
  const [description, setDescription] = useState('')
  const [lines, setLines] = useState<LineDraft[]>([{ itemId: '', quantity: '', amount: '', counterAccountId: '', orderLine: '' }])
  useUnsaved(lines.some(l => l.itemId || l.quantity.trim() || l.amount.trim()), 'phiếu nhập / xuất kho')
  const [info, setInfo] = useState('')
  const rk = useRequestKey()
  const { busy, error, setError, run } = useAction()
  const codeOptions = useAnalysisOptions()
  const [printing, setPrinting] = useState<StockDoc | null>(null)
  const [company, setCompany] = useState('')
  const [, setAddress] = useState('')   // phiếu kho 01-VT / 02-VT ghi Bộ phận, không ghi địa chỉ
  useEffect(() => { get<{ id: string; name: string; address?: string | null }[]>('/me/companies').then(cs => { const c = cs.find(x => x.id === session.companyId); setCompany(c?.name ?? ''); setAddress(c?.address ?? '') }).catch(() => undefined) }, [])
  useEffect(() => { if (printing) { printSheet(); setPrinting(null) } }, [printing])   // print once the sheet of the chosen document is rendered
  const [orders, setOrders] = useState<Order[]>([])
  const load = useCallback(() => run(async () => { setList(await get<StockDoc[]>('/stock-documents')); setOrders(await get<Order[]>('/orders')) }), [run])
  useEffect(() => { load() }, [load])
  const orderKind = type === 'Receipt' ? 'Purchase' : type === 'Issue' ? 'Sales' : ''
  const orderLines = orders.filter(o => o.status === 'Confirmed' && o.kind === orderKind && o.partnerId === partnerId)
    .flatMap(o => o.lines.map(l => ({ key: `${o.id}:${l.lineNo}`, itemId: l.itemId, label: `${o.number}/${l.lineNo} · đặt ${formatMoney(l.quantity)}` })))
  const wh = (id: string | null) => warehouses.find(w => w.id === id)?.code ?? ''
  const upd = (i: number, p: Partial<LineDraft>) => setLines(ls => ls.map((l, j) => (j === i ? { ...l, ...p } : l)))

  const save = () => run(async () => {
    const parsed = lines.map(l => ({ q: parseMoney(l.quantity), a: type === 'Receipt' ? parseMoney(l.amount || '0') : null }))
    if (parsed.some(p => !p.q || (type === 'Receipt' && p.a == null))) { setError(new Error('Số lượng/giá trị không hợp lệ.')); return }
    const d = await saveOnce<StockDoc>('POST', '/stock-documents', {
      type, warehouseId, toWarehouseId: type === 'Transfer' ? toWarehouseId : null, documentDate: date, postingDate: date, description,
      partnerId: partnerId || null,
      lines: lines.map((l, i) => ({ itemId: l.itemId, quantity: parsed[i].q, amount: parsed[i].a, counterAccountId: type === 'Transfer' ? null : l.counterAccountId || null, description: null,
        analysisCodeId: type === 'Issue' ? l.analysisCodeId || null : null,
        orderId: l.orderLine ? l.orderLine.split(':')[0] : null, orderLineNo: l.orderLine ? Number.parseInt(l.orderLine.split(':')[1], 10) : null })),
    }, rk.key())
    rk.done()
    setInfo(`Đã lập ${TYPE_VI[d.type]} ${d.number} (Nháp). Người khác có quyền ghi sổ sẽ ghi sổ phiếu.`)
    setLines([{ itemId: '', quantity: '', amount: '', counterAccountId: '', orderLine: '' }])
    await load()
  })
  const act = (fn: () => Promise<unknown>, msg: string) => run(async () => { await fn(); setInfo(msg); await load() })

  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="panel">
        <div className="form-row">
          <label>Loại phiếu<select value={type} onChange={e => setType(e.target.value as typeof type)}>{Object.entries(TYPE_VI).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></label>
          <label>{type === 'Transfer' ? 'Kho xuất' : 'Kho'}<select value={warehouseId} onChange={e => setWarehouseId(e.target.value)}><option value="">— chọn —</option>{warehouses.map(w => <option key={w.id} value={w.id}>{whLabel(w)}</option>)}</select></label>
          {type === 'Transfer' && <label>Kho nhận<select value={toWarehouseId} onChange={e => setToWarehouseId(e.target.value)}><option value="">— chọn —</option>{warehouses.map(w => <option key={w.id} value={w.id}>{whLabel(w)}</option>)}</select></label>}
          <label>Ngày<DateInput value={date} onChange={e => setDate(e.target.value)} /></label>
          {type !== 'Transfer' && <label>Đối tượng<PartnerSelect partners={partners} value={partnerId} onChange={setPartnerId} /></label>}
          <label style={{ flex: 1, minWidth: 200 }}>Diễn giải<input value={description} onChange={e => setDescription(e.target.value)} /></label>
          <button className="primary" onClick={save} disabled={busy} style={{ alignSelf: 'flex-end' }}>Lưu nháp</button>
        </div>
        <table className="grid">
          <thead><tr><th>#</th><th>Vật tư</th><th className="num">Số lượng</th>{type === 'Receipt' && <th className="num">Giá trị</th>}{type !== 'Transfer' && <th>TK đối ứng</th>}{type !== 'Transfer' && <th>Theo đơn hàng</th>}{type === 'Issue' && codeOptions.length > 0 && <th title="Gắn vào dòng giá vốn / chi phí của phiếu xuất">Mã phân tích</th>}<th /></tr></thead>
          <tbody>{lines.map((l, i) => (
            <tr key={i}><td>{i + 1}</td>
              <td><select aria-label="Vật tư" value={l.itemId} onChange={e => upd(i, { itemId: e.target.value })}><option value="">— chọn —</option>{items.map(it => <option key={it.id} value={it.id}>{it.code} – {it.name} ({it.unit})</option>)}</select></td>
              <td><input className="num" aria-label="Số lượng" value={l.quantity} onChange={e => upd(i, { quantity: e.target.value })} /></td>
              {type === 'Receipt' && <td><MoneyInput aria-label="Giá trị" value={l.amount} onChange={e => upd(i, { amount: e.target.value })} /></td>}
              {type !== 'Transfer' && <td><AccountSelect accounts={accounts} value={l.counterAccountId} onChange={v => upd(i, { counterAccountId: v })} ariaLabel="TK đối ứng" /></td>}
              {type !== 'Transfer' && <td><select aria-label="Theo đơn hàng" value={l.orderLine} onChange={e => upd(i, { orderLine: e.target.value })}><option value="">—</option>{orderLines.filter(o => !l.itemId || o.itemId === l.itemId).map(o => <option key={o.key} value={o.key}>{o.label}</option>)}</select></td>}
              {type === 'Issue' && codeOptions.length > 0 && <td><SearchSelect options={codeOptions} value={l.analysisCodeId ?? ''} onChange={id => upd(i, { analysisCodeId: id })} ariaLabel="Mã phân tích" placeholder="—" width={150} /></td>}
              <td><button aria-label="Xóa dòng" onClick={() => setLines(ls => ls.filter((_, j) => j !== i))}>✕</button></td></tr>))}
          </tbody>
        </table>
        <button style={{ marginTop: 8 }} onClick={() => setLines(ls => [...ls, { itemId: '', quantity: '', amount: '', counterAccountId: '', orderLine: '' }])}>+ Thêm dòng</button>
        <p className="muted">Giá xuất kho do hệ thống tính theo phương pháp của kho; kho bình quân cuối tháng chỉ có giá trị sau khi chạy “Tính giá cuối tháng”.</p>
      </div>
      <div className="scroll">
        <table className="grid">
          <thead><tr><th>Ngày</th><th>Số</th><th>Loại</th><th>Kho</th><th>Diễn giải</th><th>Trạng thái</th><th /></tr></thead>
          <tbody>{list.map(d => (
            <tr key={d.id}><td>{d.postingDate}</td><td>{d.number}</td><td>{TYPE_VI[d.type]}</td><td>{wh(d.warehouseId)}{d.toWarehouseId ? ' → ' + wh(d.toWarehouseId) : ''}</td><td>{d.description}</td>
              <td><span className="badge">{STATUS_VI[d.status] ?? d.status}</span></td>
              <td style={{ display: 'flex', gap: 4 }}>
                <button onClick={() => setPrinting(d)} title="In phiếu nhập / xuất kho">In</button>
                {d.status === 'Draft' && <button className="primary" onClick={() => act(() => post(`/stock-documents/${d.id}/post`), `Đã ghi sổ ${d.number}.`)}>Ghi sổ</button>}
                {d.status === 'Draft' && <button className="danger" onClick={async () => { const r = await askText('Lý do hủy phiếu nháp:'); if (r) act(() => post(`/stock-documents/${d.id}/cancel`, { reason: r }), `Đã hủy ${d.number}.`) }}>Hủy</button>}
                {d.status === 'Posted' && !d.number.startsWith('DP') && <button className="danger" onClick={async () => {
                  const reason = await askText('Lý do đảo phiếu:'); if (!reason) return
                  const dt = await askDate('Ngày đảo', d.postingDate); if (dt) act(() => post(`/stock-documents/${d.id}/reverse`, { reversalDate: dt, reason }), `Đã đảo ${d.number}.`)
                }}>Đảo</button>}
              </td></tr>))}
            {list.length === 0 && <tr><td colSpan={7} className="muted">Chưa có phiếu kho.</td></tr>}
          </tbody>
        </table>
      </div>
      {printing && (() => {
        const d = printing, receipt = d.type === 'Receipt', it = (id: string) => items.find(x => x.id === id)
        const [y, m, day] = d.postingDate.split('-')
        const total = addMoney(d.lines.map(l => l.amount ?? '0'))
        const whName = (id: string | null) => { const w = warehouses.find(x => x.id === id); return w ? `${w.code} – ${w.name}` : '' }
        return (
          <PrintSheet>
            {/* Mẫu số 01-VT (phiếu nhập kho) / 02-VT (phiếu xuất kho), Phụ lục I TT 99/2025. A transfer prints on the issue layout. */}
            <FormHeader company={company} second={<>Bộ phận: ................</>} formNo={receipt ? '01 - VT' : '02 - VT'} />
            <div style={{ display: 'grid', gridTemplateColumns: '1fr auto 1fr', alignItems: 'end', marginTop: 8 }}>
              <span />
              <div className="center"><h1 style={{ margin: 0 }}>{receipt ? 'PHIẾU NHẬP KHO' : d.type === 'Issue' ? 'PHIẾU XUẤT KHO' : 'PHIẾU XUẤT KHO (ĐIỀU CHUYỂN)'}</h1><i>Ngày {day} tháng {m} năm {y}</i></div>
              <div style={{ justifySelf: 'end' }}>Số: <b>{d.number}</b><br />Nợ: ..........<br />Có: ..........</div>
            </div>
            {d.status !== 'Posted' && <div className="center" style={{ marginTop: 6 }}><span className="stamp">{(STATUS_VI[d.status] ?? d.status).toUpperCase()} — CHƯA GHI SỔ</span></div>}
            {receipt
              ? <p>- Họ và tên người giao: ..............................................................<br />
                - Theo ............ số ........... ngày ..... tháng ..... năm ..... của ........................ ({d.description})<br />
                Nhập tại kho: {whName(d.warehouseId)} &nbsp; địa điểm ..........................</p>
              : <p>- Họ và tên người nhận hàng: .................................... Địa chỉ (bộ phận) .................<br />
                - Lý do xuất kho: {d.description}<br />
                - Xuất tại kho (ngăn lô): {whName(d.warehouseId)}{d.toWarehouseId ? ` — chuyển đến kho: ${whName(d.toWarehouseId)}` : ''} &nbsp; Địa điểm ..........................</p>}
            <table>
              <thead>
                <tr><th rowSpan={2}>STT</th><th rowSpan={2}>Tên, nhãn hiệu, quy cách, phẩm chất vật tư, dụng cụ, sản phẩm, hàng hóa</th><th rowSpan={2}>Mã số</th><th rowSpan={2}>Đơn vị tính</th>
                  <th colSpan={2}>Số lượng</th><th rowSpan={2}>Đơn giá</th><th rowSpan={2}>Thành tiền</th></tr>
                <tr>{receipt ? <><th>Theo chứng từ</th><th>Thực nhập</th></> : <><th>Yêu cầu</th><th>Thực xuất</th></>}</tr>
                <tr className="center"><td>A</td><td>B</td><td>C</td><td>D</td><td>1</td><td>2</td><td>3</td><td>4</td></tr>
              </thead>
              <tbody>{d.lines.map(l => { const x = it(l.itemId); return (
                <tr key={l.lineNo}><td className="center">{l.lineNo}</td><td>{x?.name}</td><td>{x?.code}</td><td>{x?.unit}</td><td />{/* theo chứng từ / yêu cầu: ghi tay */}<td className="num">{formatMoney(l.quantity)}</td>
                  <td className="num">{l.amount ? formatMoney(divideMoney(l.amount, l.quantity) ?? '') : ''}</td><td className="num">{l.amount ? formatMoney(l.amount) : ''}</td></tr>) })}</tbody>
              <tfoot><tr><td colSpan={2}><b>Cộng</b></td><td className="center">x</td><td className="center">x</td><td className="center">x</td><td className="center">x</td><td className="center">x</td><td className="num"><b>{total !== '0' ? formatMoney(total) : ''}</b></td></tr></tfoot>
            </table>
            <p>- Tổng số tiền (viết bằng chữ): {total !== '0' ? <i>{amountInWords(total)}</i> : '..............................................................................'}<br />
              - Số chứng từ gốc kèm theo: .................................................................................</p>
            {!receipt && total === '0' && <p className="center"><i>Đơn giá, thành tiền xuất kho ghi sau khi tính giá xuất kho theo phương pháp của kho.</i></p>}
            <div className="right"><i>Ngày {day} tháng {m} năm {y}</i></div>
            <div className="signs" style={{ gridTemplateColumns: receipt ? 'repeat(4, 1fr)' : 'repeat(5, 1fr)' }}>
              <div><b>Người lập phiếu</b><br /><i>(Ký, họ tên)</i></div><div><b>Người {receipt ? 'giao' : 'nhận'} hàng</b><br /><i>(Ký, họ tên)</i></div>
              <div><b>Thủ kho</b><br /><i>(Ký, họ tên)</i></div><div><b>Kế toán trưởng</b><br /><i>(Hoặc bộ phận có nhu cầu nhập)<br />(Ký, họ tên)</i></div>
              {!receipt && <div><b>Giám đốc</b><br /><i>(Ký, họ tên)</i></div>}
            </div>
            <div className="foot">{FORM_FOOT}</div>
          </PrintSheet>
        )
      })()}
    </>
  )
}

interface SummaryRow { warehouseCode: string; itemCode: string; itemName: string; unit: string; openingQty: string; openingValue: string; inQty: string; inValue: string; outQty: string; outValue: string; closingQty: string; closingValue: string; pendingOutQty: string }

function Summary() {
  const [from, setFrom] = useState(today().slice(0, 8) + '01')
  const [to, setTo] = useState(today())
  const [rows, setRows] = useState<SummaryRow[] | null>(null)
  const [recon, setRecon] = useState<{ accountCode: string; difference: string; pendingIssueQty: string }[]>([])
  const { busy, error, run } = useAction()
  const load = () => run(async () => {
    setRows(await get(`/reports/inventory-summary?from=${from}&to=${to}`))
    setRecon(await get(`/reports/inventory-reconciliation?asOf=${to}`))
  })
  const f = (v: string) => (v === '0' ? '' : formatMoney(v))
  return (
    <>
      <div className="toolbar">
        <label>Từ ngày<DateInput value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<DateInput value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" onClick={load} disabled={busy}>Xem</button>
        <ExportButton fileName={'NXT_' + from + '_' + to} sheet="Nhập xuất tồn" disabled={!rows}
          columns={[{ header: 'Kho' }, { header: 'Mã vật tư' }, { header: 'Tên vật tư', width: 30 }, { header: 'ĐVT' }, ...['Tồn đầu SL', 'Tồn đầu GT', 'Nhập SL', 'Nhập GT', 'Xuất SL', 'Xuất GT', 'Tồn cuối SL', 'Tồn cuối GT', 'Chờ tính giá'].map(h => ({ header: h, numeric: true }))]}
          rows={(rows ?? []).map(r => [r.warehouseCode, r.itemCode, r.itemName, r.unit, r.openingQty, r.openingValue, r.inQty, r.inValue, r.outQty, r.outValue, r.closingQty, r.closingValue, r.pendingOutQty])} />
      </div>
      <ErrorBox error={error} />
      {recon.map(r => <div key={r.accountCode} className={`msg ${r.difference === '0' ? 'ok' : 'error'}`}>
        TK {r.accountCode}: {r.difference === '0' ? 'sổ kho khớp sổ cái' : `sổ kho lệch sổ cái ${formatMoney(r.difference)}`}{r.pendingIssueQty !== '0' ? ` · ${formatMoney(r.pendingIssueQty)} đơn vị xuất chờ tính giá cuối tháng` : ''}</div>)}
      {rows && <div className="scroll"><table className="grid">
        <thead><tr><th>Kho</th><th>Vật tư</th><th>ĐVT</th><th className="num">Tồn đầu SL</th><th className="num">Tồn đầu GT</th><th className="num">Nhập SL</th><th className="num">Nhập GT</th><th className="num">Xuất SL</th><th className="num">Xuất GT</th><th className="num">Tồn cuối SL</th><th className="num">Tồn cuối GT</th><th className="num">Chờ tính giá</th></tr></thead>
        <tbody>{rows.map(r => <tr key={r.warehouseCode + r.itemCode}><td>{r.warehouseCode}</td><td>{r.itemCode} – {r.itemName}</td><td>{r.unit}</td>
          {[r.openingQty, r.openingValue, r.inQty, r.inValue, r.outQty, r.outValue, r.closingQty, r.closingValue, r.pendingOutQty].map((v, i) => <td key={i} className="num">{f(v)}</td>)}</tr>)}</tbody>
      </table></div>}
    </>
  )
}

interface CardRow { date: string; documentNumber: string; description: string | null; inQty: string; outQty: string; value: string | null; balanceQty: string; balanceValue: string | null }

function StockCard() {
  const { warehouses, items } = useStockCatalog()
  const [warehouseId, setWarehouseId] = useState('')
  const [itemId, setItemId] = useState('')
  const [from, setFrom] = useState(today().slice(0, 8) + '01')
  const [to, setTo] = useState(today())
  const [rows, setRows] = useState<CardRow[] | null>(null)
  const { busy, error, run } = useAction()
  const load = () => run(async () => setRows(await get(`/reports/stock-card?warehouseId=${warehouseId}&itemId=${itemId}&from=${from}&to=${to}`)))
  return (
    <>
      <div className="toolbar">
        <label>Kho<select value={warehouseId} onChange={e => setWarehouseId(e.target.value)}><option value="">— chọn —</option>{warehouses.map(w => <option key={w.id} value={w.id}>{w.code}</option>)}</select></label>
        <label>Vật tư<select value={itemId} onChange={e => setItemId(e.target.value)}><option value="">— chọn —</option>{items.map(i => <option key={i.id} value={i.id}>{i.code} – {i.name}</option>)}</select></label>
        <label>Từ ngày<DateInput value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<DateInput value={to} onChange={e => setTo(e.target.value)} /></label>
        <button className="primary" onClick={load} disabled={busy || !warehouseId || !itemId}>Xem</button>
      </div>
      <ErrorBox error={error} />
      {rows && <table className="grid">
        <thead><tr><th>Ngày</th><th>Số phiếu</th><th>Diễn giải</th><th className="num">Nhập</th><th className="num">Xuất</th><th className="num">Giá trị</th><th className="num">Tồn SL</th><th className="num">Tồn GT</th></tr></thead>
        <tbody>{rows.map((r, i) => <tr key={i}><td>{r.date}</td><td>{r.documentNumber}</td><td>{r.description}</td>
          <td className="num">{r.inQty === '0' ? '' : formatMoney(r.inQty)}</td><td className="num">{r.outQty === '0' ? '' : formatMoney(r.outQty)}</td>
          <td className="num">{r.value == null ? <span className="muted">chờ tính giá</span> : formatMoney(r.value)}</td>
          <td className="num">{formatMoney(r.balanceQty)}</td><td className="num">{r.balanceValue == null ? '—' : formatMoney(r.balanceValue)}</td></tr>)}</tbody>
      </table>}
    </>
  )
}

interface Run { id: string; warehouseId: string; periodStart: string; status: string; resultJson: string }

function Costing() {
  const { warehouses } = useStockCatalog()
  const [periods, setPeriods] = useState<Period[]>([])
  const [runs, setRuns] = useState<Run[]>([])
  const [warehouseId, setWarehouseId] = useState('')
  const [periodId, setPeriodId] = useState('')
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => { setRuns(await get('/costing-runs')); setPeriods(await get('/periods')) }), [run])
  useEffect(() => { load() }, [load])
  const monthly = warehouses.filter(w => w.policies.some(p => p.method === 'MonthlyAverage'))
  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="toolbar">
        <label>Kho (bình quân cuối tháng)<select value={warehouseId} onChange={e => setWarehouseId(e.target.value)}><option value="">— chọn —</option>{monthly.map(w => <option key={w.id} value={w.id}>{w.code} – {w.name}</option>)}</select></label>
        <label>Kỳ<select value={periodId} onChange={e => setPeriodId(e.target.value)}><option value="">— chọn —</option>{periods.map(p => <option key={p.id} value={p.id}>{p.startDate.slice(0, 7)}{p.status === 'Locked' ? ' (đã khóa)' : ''}</option>)}</select></label>
        <button className="primary" disabled={busy || !warehouseId || !periodId} onClick={() => run(async () => { await post('/costing-runs', { warehouseId, periodId }); setInfo('Đã tính giá và ghi sổ giá vốn xuất kho của kỳ.'); await load() })}>Tính giá</button>
      </div>
      <table className="grid">
        <thead><tr><th>Kỳ</th><th>Kho</th><th>Trạng thái</th><th>Kết quả</th><th /></tr></thead>
        <tbody>{runs.map(r => (
          <tr key={r.id}><td>{r.periodStart.slice(0, 7)}</td><td>{warehouses.find(w => w.id === r.warehouseId)?.code}</td><td>{r.status === 'Active' ? 'Hiệu lực' : 'Đã hủy'}</td>
            <td style={{ whiteSpace: 'normal' }}>{(JSON.parse(r.resultJson || '[]') as { item: string; issuedQty: number | string; issuedValue: number | string }[])
              .map(x => `${x.item}: xuất ${formatMoney(String(x.issuedQty))} = ${formatMoney(String(x.issuedValue))}`).join('; ')}</td>
            <td>{r.status === 'Active' && <button className="danger" onClick={async () => { const reason = await askText('Lý do hủy lần tính giá:'); if (reason) run(async () => { await post(`/costing-runs/${r.id}/reverse`, { reason }); await load() }) }}>Hủy</button>}</td></tr>))}
          {runs.length === 0 && <tr><td colSpan={5} className="muted">Chưa có lần tính giá nào.</td></tr>}
        </tbody>
      </table>
    </>
  )
}

interface Pending { documentId: string; documentNumber: string; type: string; postingDate: string; lineNo: number; itemCode: string; quantity: string; expectedKind: string }
interface Measurement { id: string; documentNumber: string; lineNo: number; kind: string; measuredMt: string; lineMt: string; drynessPercent: string; bdmt: string; methodCode: string; evidenceType: string; evidenceNo: string; evidenceIssuer: string | null; evidenceDate: string; supersededAt: string | null; supersedeReason: string | null }
interface DrySummary { warehouseCode: string; itemCode: string; openingMt: string; openingBdmt: string; inMt: string; inBdmt: string; outMt: string; outBdmt: string; closingMt: string; closingBdmt: string; unmeasuredInMt: string; unmeasuredOutMt: string }

const KIND_VI: Record<string, string> = { Receipt: 'Đo khi nhập', Shipment: 'Đo ở tàu (xuất)', Other: 'Khác' }

/** BDMT = MT × độ khô% / 100 (phương pháp đã được doanh nghiệp phê duyệt). Đo khi nhập và đo ở tàu khi xuất (chứng thư). */
function Bdmt() {
  const [from, setFrom] = useState(today().slice(0, 8) + '01')
  const [to, setTo] = useState(today())
  const [rows, setRows] = useState<DrySummary[]>([])
  const [pending, setPending] = useState<Pending[]>([])
  const [history, setHistory] = useState<Measurement[]>([])
  const [form, setForm] = useState<Record<string, { dryness: string; evidenceNo: string; issuer: string; date: string; mt: string }>>({})
  const [info, setInfo] = useState('')
  const { busy, error, setError, run } = useAction()
  const load = useCallback(() => run(async () => {
    setRows(await get(`/reports/dryness-summary?from=${from}&to=${to}`))
    setPending(await get('/measurements/pending'))
    setHistory(await get('/measurements'))
  }), [run, from, to])
  useEffect(() => { load() }, [load])
  const key = (p: Pending) => `${p.documentId}:${p.lineNo}`
  const f = (p: Pending) => form[key(p)] ?? { dryness: '', evidenceNo: '', issuer: '', date: p.postingDate, mt: '' }
  const setF = (p: Pending, patch: Partial<ReturnType<typeof f>>) => setForm(s => ({ ...s, [key(p)]: { ...f(p), ...patch } }))

  const save = (p: Pending) => run(async () => {
    const v = f(p)
    const dryness = parseMoney(v.dryness)
    const mt = v.mt ? parseMoney(v.mt) : null
    if (!dryness || (v.mt && !mt)) { setError(new Error('Độ khô / MT không hợp lệ (dùng dấu phẩy cho phần thập phân, ví dụ 52,35).')); return }
    const r = await post<{ bdmt: string }>('/measurements', {
      documentId: p.documentId, lineNo: p.lineNo, drynessPercent: dryness, evidenceType: p.expectedKind === 'Shipment' ? 'Certificate' : 'InternalReport',
      evidenceNo: v.evidenceNo, evidenceDate: v.date, evidenceIssuer: v.issuer || null, measuredMt: mt, note: null,
    })
    setInfo(`${p.documentNumber} dòng ${p.lineNo}: ${formatMoney(r.bdmt)} BDMT.`)
    await load()
  })

  const supersede = async (m: Measurement) => {
    const dryness = parseMoney(await askText('Độ khô (%) mới:', m.drynessPercent.replace('.', ',')) ?? ''); if (!dryness) return
    const evidenceNo = await askText('Số chứng thư / phiếu đo mới:', m.evidenceNo); if (!evidenceNo) return
    const reason = await askText('Lý do thay thế (bắt buộc):'); if (!reason) return
    const doc = pending.find(p => p.documentNumber === m.documentNumber)
    run(async () => {
      const docs = await get<{ id: string; number: string }[]>('/stock-documents')
      const documentId = doc?.documentId ?? docs.find(d => d.number === m.documentNumber)!.id
      await post(`/measurements/${m.id}/supersede`, {
        reason, measurement: { documentId, lineNo: m.lineNo, drynessPercent: dryness, evidenceType: m.evidenceType, evidenceNo, evidenceDate: m.evidenceDate, evidenceIssuer: m.evidenceIssuer, measuredMt: m.measuredMt, note: null },
      })
      setInfo(`Đã thay thế kết quả đo ${m.documentNumber} dòng ${m.lineNo}.`)
      await load()
    })
  }

  const n = (v: string) => (v === '0' ? '' : formatMoney(v))
  return (
    <>
      <ErrorBox error={error} />
      {info && !error && <div className="msg ok">{info}</div>}
      <div className="toolbar">
        <label>Từ ngày<DateInput value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>Đến ngày<DateInput value={to} onChange={e => setTo(e.target.value)} /></label>
        <button onClick={() => load()} disabled={busy}>Xem</button>
        <span className="muted">BDMT = MT × độ khô % ÷ 100 · không tự hạch toán chênh lệch BDMT</span>
      </div>
      <div className="panel">
        <strong>Tồn MT / BDMT</strong>
        <table className="grid">
          <thead><tr><th>Kho</th><th>Vật tư</th><th className="num">Đầu MT</th><th className="num">Đầu BDMT</th><th className="num">Nhập MT</th><th className="num">Nhập BDMT</th><th className="num">Xuất MT</th><th className="num">Xuất BDMT</th><th className="num">Cuối MT</th><th className="num">Cuối BDMT</th><th className="num">MT chưa có kết quả đo</th></tr></thead>
          <tbody>{rows.map(r => <tr key={r.warehouseCode + r.itemCode}><td>{r.warehouseCode}</td><td>{r.itemCode}</td>
            {[r.openingMt, r.openingBdmt, r.inMt, r.inBdmt, r.outMt, r.outBdmt, r.closingMt, r.closingBdmt].map((v, i) => <td key={i} className="num">{n(v)}</td>)}
            <td className="num">{n(r.unmeasuredInMt)}{r.unmeasuredOutMt !== '0' ? ` / xuất ${formatMoney(r.unmeasuredOutMt)}` : ''}</td></tr>)}</tbody>
        </table>
      </div>
      <div className="panel">
        <strong>Chờ kết quả đo / chứng thư ({pending.length})</strong>
        <table className="grid">
          <thead><tr><th>Ngày</th><th>Phiếu</th><th>Vật tư</th><th className="num">MT trên phiếu</th><th>Loại đo</th><th>Độ khô %</th><th>MT theo chứng thư</th><th>Số chứng thư / phiếu đo</th><th>Đơn vị giám định</th><th>Ngày</th><th /></tr></thead>
          <tbody>{pending.map(p => (
            <tr key={key(p)}><td>{p.postingDate}</td><td>{p.documentNumber}/{p.lineNo}</td><td>{p.itemCode}</td><td className="num">{formatMoney(p.quantity)}</td><td>{KIND_VI[p.expectedKind]}</td>
              <td><input className="num" aria-label="Độ khô %" size={6} value={f(p).dryness} onChange={e => setF(p, { dryness: e.target.value })} /></td>
              <td><input className="num" aria-label="MT theo chứng thư" size={8} placeholder="= phiếu" value={f(p).mt} onChange={e => setF(p, { mt: e.target.value })} /></td>
              <td><input aria-label="Số chứng thư" value={f(p).evidenceNo} onChange={e => setF(p, { evidenceNo: e.target.value })} placeholder={p.expectedKind === 'Shipment' ? 'Chứng thư (bắt buộc)' : 'Phiếu đo'} /></td>
              <td><input aria-label="Đơn vị giám định" value={f(p).issuer} onChange={e => setF(p, { issuer: e.target.value })} /></td>
              <td><DateInput aria-label="Ngày chứng từ" value={f(p).date} onChange={e => setF(p, { date: e.target.value })} /></td>
              <td><button className="primary" disabled={busy} onClick={() => save(p)}>Ghi nhận</button></td></tr>))}
            {pending.length === 0 && <tr><td colSpan={11} className="muted">Không có dòng chờ kết quả đo.</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="panel">
        <strong>Lịch sử kết quả đo</strong>
        <table className="grid">
          <thead><tr><th>Phiếu</th><th>Loại</th><th className="num">MT</th><th className="num">Độ khô %</th><th className="num">BDMT</th><th>Chứng từ</th><th>Trạng thái</th><th /></tr></thead>
          <tbody>{history.map(m => (
            <tr key={m.id} style={m.supersededAt ? { opacity: .6 } : undefined}><td>{m.documentNumber}/{m.lineNo}</td><td>{KIND_VI[m.kind]}</td>
              <td className="num">{formatMoney(m.measuredMt)}{m.measuredMt !== m.lineMt ? ` (phiếu ${formatMoney(m.lineMt)})` : ''}</td>
              <td className="num">{formatMoney(m.drynessPercent)}</td><td className="num">{formatMoney(m.bdmt)}</td>
              <td>{m.evidenceType === 'Certificate' ? 'Chứng thư' : 'Phiếu đo'} {m.evidenceNo}{m.evidenceIssuer ? ` · ${m.evidenceIssuer}` : ''} · {m.evidenceDate}</td>
              <td>{m.supersededAt ? `Đã thay thế: ${m.supersedeReason}` : 'Hiệu lực'}</td>
              <td>{!m.supersededAt && <button onClick={() => supersede(m)}>Thay thế</button>}</td></tr>))}
          </tbody>
        </table>
      </div>
    </>
  )
}