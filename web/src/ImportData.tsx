import { useEffect, useState } from 'react'
import { get, post, type Account } from './api'
import { ErrorBox, useAction } from './common'
import { formatMoney, today } from './money'
import { excelDate, headerKey, readSheetFile } from './sheetread'
import { downloadXlsx } from './xlsx'
import { DateInput } from './DateInput'

interface Field { key: string; label: string; aliases: string[]; required?: boolean; date?: boolean; numeric?: boolean }
interface Kind { title: string; path: string; fields: Field[]; example: string[][]; help: string }

// Header aliases are matched without diacritics (headerKey). The example rows use placeholder codes, not legal account codes.
const KINDS: Record<string, Kind> = {
  accounts: {
    title: 'Hệ thống tài khoản', path: '/import/accounts',
    help: 'TK cấp trên phải là TK tổng hợp (cột "Chi tiết" = Không). Thứ tự dòng tùy ý — cấp trên được tạo trước. Mã đã có thì bỏ qua, không ghi đè.',
    fields: [
      { key: 'code', label: 'Mã TK', aliases: ['ma tk', 'ma tai khoan', 'so hieu tk', 'so hieu tai khoan', 'tai khoan', 'code'], required: true },
      { key: 'name', label: 'Tên TK', aliases: ['ten tk', 'ten tai khoan', 'ten', 'name'], required: true },
      { key: 'parentCode', label: 'TK cấp trên', aliases: ['tk cap tren', 'tai khoan cap tren', 'tk me', 'parent'] },
      { key: 'isPostable', label: 'Chi tiết', aliases: ['chi tiet', 'tk chi tiet', 'hach toan', 'hach toan truc tiep', 'postable'] },
      { key: 'requiresPartner', label: 'Theo dõi đối tượng', aliases: ['theo doi doi tuong', 'bat buoc doi tuong', 'doi tuong'] },
      { key: 'openItem', label: 'Công nợ', aliases: ['cong no', 'cong no theo chung tu', 'open item'] },
      { key: 'cashKind', label: 'Loại tiền', aliases: ['loai tien', 'tien', 'cash kind'] },
      { key: 'negativePolicy', label: 'Số dư âm', aliases: ['so du am', 'negative'] },
      { key: 'balanceDisplay', label: 'Trình bày số dư', aliases: ['trinh bay so du', 'luong tinh'] },
      { key: 'reportTags', label: 'Nhãn BC', aliases: ['nhan bc', 'nhan bao cao', 'nhan'] },
    ],
    example: [['TIEN', 'Tiền (tổng hợp)', '', 'Không', '', '', '', '', ''], ['TM01', 'Tiền mặt quỹ chính', 'TIEN', 'Có', '', '', 'Tiền mặt', 'Chặn', ''],
      ['PTKH', 'Phải thu khách hàng', '', 'Có', 'Có', 'Phải thu', '', '', 'Lưỡng tính']],
  },
  partners: {
    title: 'Đối tượng (khách hàng, NCC, nhân viên)', path: '/import/partners',
    help: 'Vai trò: KH, NCC, NV, KHAC (nhiều vai trò cách nhau dấu phẩy). Mã đã có thì bỏ qua, không ghi đè.',
    fields: [
      { key: 'code', label: 'Mã đối tượng', aliases: ['ma doi tuong', 'ma', 'ma kh', 'ma ncc', 'ma khach hang', 'ma nha cung cap', 'code'], required: true },
      { key: 'name', label: 'Tên', aliases: ['ten doi tuong', 'ten', 'ten khach hang', 'ten nha cung cap', 'name'], required: true },
      { key: 'roles', label: 'Vai trò', aliases: ['vai tro', 'loai', 'nhom', 'loai doi tuong', 'roles'] },
      { key: 'taxCode', label: 'Mã số thuế', aliases: ['ma so thue', 'mst', 'tax code'] },
    ],
    example: [['KH001', 'Công ty TNHH Ví Dụ', 'KH', ''], ['NCC001', 'Nhà cung cấp Ví Dụ', 'NCC, KH', '']],
  },
  items: {
    title: 'Vật tư, hàng hóa', path: '/import/items',
    help: 'TK kho là TK chi tiết hàng tồn kho (không phải TK tiền/công nợ). Mã đã có thì bỏ qua, không ghi đè.',
    fields: [
      { key: 'code', label: 'Mã vật tư', aliases: ['ma vat tu', 'ma hang', 'ma', 'ma hang hoa', 'code'], required: true },
      { key: 'name', label: 'Tên vật tư', aliases: ['ten vat tu', 'ten hang', 'ten', 'ten hang hoa', 'name'], required: true },
      { key: 'unit', label: 'Đơn vị tính', aliases: ['don vi tinh', 'dvt', 'don vi', 'unit'], required: true },
      { key: 'inventoryAccount', label: 'TK kho', aliases: ['tk kho', 'tai khoan kho', 'tk hang ton kho', 'tai khoan'], required: true },
    ],
    example: [['VT001', 'Vật tư ví dụ', 'cái', 'HTK'], ['VT002', 'Hàng hóa ví dụ', 'kg', 'HTK']],
  },
  stock: {
    title: 'Tồn kho đầu kỳ', path: '/import/opening-stock',
    help: 'Mỗi kho tạo một phiếu nhập kho NHÁP: Nợ TK kho của vật tư / Có TK trung gian chọn bên dưới; ghi sổ phiếu ở màn hình Kho. ' +
      'Chứng từ số dư đầu kỳ (OPB) ghi Nợ đúng TK trung gian này (thay cho các TK hàng tồn kho) để TK trung gian về 0 — tránh tính tồn kho hai lần. ' +
      'Kho phải đã có phương pháp tính giá trước khi ghi sổ.',
    fields: [
      { key: 'warehouse', label: 'Mã kho', aliases: ['ma kho', 'kho', 'warehouse'], required: true },
      { key: 'item', label: 'Mã vật tư', aliases: ['ma vat tu', 'ma hang', 'vat tu', 'item'], required: true },
      { key: 'quantity', label: 'Số lượng', aliases: ['so luong', 'sl', 'luong ton', 'quantity'], required: true, numeric: true },
      { key: 'value', label: 'Giá trị', aliases: ['gia tri', 'thanh tien', 'gia tri ton', 'value'], required: true, numeric: true },
      { key: 'description', label: 'Diễn giải', aliases: ['dien giai', 'ghi chu', 'description'] },
    ],
    example: [['KHO1', 'VT001', '100', '5000000', ''], ['KHO1', 'VT002', '12.5', '1250000', '']],
  },
  assets: {
    title: 'TSCĐ / CCDC đầu kỳ', path: '/import/assets',
    help: 'Làm SAU khi chứng từ số dư đầu kỳ (OPB) đã ghi sổ: nguyên giá từng thẻ được lấy từ dòng Nợ TK tài sản trên chứng từ OPB (không gõ tay vào sổ). ' +
      'TSCĐ: nhập nguyên giá, hao mòn lũy kế, số tháng sử dụng và số tháng CÒN LẠI — khấu hao tiếp từ ngày số dư, không trích bù kỳ cũ. ' +
      'CCDC: nhập giá trị còn lại vào cột Nguyên giá, hao mòn 0, để trống TK hao mòn. Thẻ tạo ở trạng thái nháp; ghi tăng ở màn hình Tài sản / CCDC.',
    fields: [
      { key: 'code', label: 'Mã tài sản', aliases: ['ma tai san', 'ma ts', 'ma the', 'ma', 'code'], required: true },
      { key: 'name', label: 'Tên tài sản', aliases: ['ten tai san', 'ten ts', 'ten', 'name'], required: true },
      { key: 'class', label: 'Loại', aliases: ['loai', 'loai tai san', 'phan loai'] },
      { key: 'assetAccount', label: 'TK tài sản', aliases: ['tk tai san', 'tk nguyen gia', 'tai khoan tai san'], required: true },
      { key: 'accumulatedAccount', label: 'TK hao mòn', aliases: ['tk hao mon', 'tai khoan hao mon'] },
      { key: 'expenseAccount', label: 'TK chi phí', aliases: ['tk chi phi', 'tai khoan chi phi'], required: true },
      { key: 'originalCost', label: 'Nguyên giá', aliases: ['nguyen gia', 'gia tri con lai'], required: true, numeric: true },
      { key: 'openingAccumulated', label: 'Hao mòn lũy kế', aliases: ['hao mon luy ke', 'hao mon', 'da khau hao'], numeric: true },
      { key: 'usefulLifeMonths', label: 'Số tháng sử dụng', aliases: ['so thang su dung', 'thoi gian su dung thang', 'so thang khau hao'], required: true, numeric: true },
      { key: 'remainingLifeMonths', label: 'Số tháng còn lại', aliases: ['so thang con lai', 'thang con lai'], required: true, numeric: true },
      { key: 'inServiceDate', label: 'Ngày sử dụng', aliases: ['ngay su dung', 'ngay dua vao su dung', 'ngay bat dau su dung'], required: true, date: true },
      { key: 'proration', label: 'Cách tính tháng', aliases: ['cach tinh thang', 'cach tinh'] },
      { key: 'residualValue', label: 'Giá trị thu hồi', aliases: ['gia tri thu hoi', 'gia tri thanh ly uoc tinh'], numeric: true },
    ],
    example: [['TS001', 'Máy móc ví dụ', 'TSCĐ', 'TKTS', 'TKHM', 'TKCP', '200000000', '80000000', '60', '36', '2023-03-15', 'Tròn tháng', ''],
      ['CC001', 'Dụng cụ ví dụ', 'CCDC', 'TKCCDC', '', 'TKCP', '30000000', '0', '12', '6', '2025-07-01', '', '']],
  },
  opening: {
    title: 'Số dư đầu kỳ', path: '/import/opening-balances',
    help: 'Mỗi dòng ghi một bên Nợ hoặc Có. Công nợ nhập chi tiết theo đối tượng (mỗi hóa đơn còn nợ một dòng, có hạn thanh toán) để theo dõi thanh toán. ' +
      'Ngoại tệ: ghi loại tiền và số nguyên tệ, số tiền quy đổi ở cột Nợ/Có. Kết quả là một chứng từ OPB ở trạng thái Nháp — gửi duyệt và ghi sổ ở màn hình Chứng từ. ' +
      'Số tiền: không dùng dấu phân cách nghìn, hoặc ghi đủ (1.000.000); "1.000" / "1,500" bị từ chối vì không rõ nghĩa.',
    fields: [
      { key: 'accountCode', label: 'Mã TK', aliases: ['ma tk', 'tai khoan', 'tk', 'so hieu tk', 'ma tai khoan'], required: true },
      { key: 'partnerCode', label: 'Mã đối tượng', aliases: ['ma doi tuong', 'doi tuong', 'ma kh', 'ma ncc'] },
      { key: 'debit', label: 'Dư Nợ', aliases: ['du no', 'no', 'so du no', 'debit'], numeric: true },
      { key: 'credit', label: 'Dư Có', aliases: ['du co', 'co', 'so du co', 'credit'], numeric: true },
      { key: 'currency', label: 'Loại tiền', aliases: ['loai tien', 'ma loai tien', 'ma ngoai te', 'currency'] },
      { key: 'foreignAmount', label: 'Số nguyên tệ', aliases: ['so nguyen te', 'nguyen te', 'so tien nguyen te', 'foreign amount'], numeric: true },
      { key: 'dueDate', label: 'Hạn thanh toán', aliases: ['han thanh toan', 'han tt', 'ngay den han', 'due date'], date: true },
      { key: 'description', label: 'Diễn giải', aliases: ['dien giai', 'noi dung', 'ghi chu', 'description'] },
    ],
    example: [['TM01', '', '15000000', '', '', '', '', 'Tồn quỹ'], ['PTKH', 'KH001', '22000000', '', '', '', '2026-02-15', 'HĐ 0000123 còn nợ'],
      ['PTKH', 'KH002', '25350000', '', 'USD', '1000', '', 'HĐ xuất khẩu còn nợ'], ['VON', '', '', '62350000', '', '', '', 'Vốn góp']],
  },
  vouchers: {
    title: 'Chứng từ (chuyển dữ liệu)', path: '/import/vouchers',
    help: 'Các dòng cùng "Mã chứng từ" là một chứng từ (phải cân Nợ/Có, cùng ngày). Mỗi chứng từ thành một chứng từ Nháp loại GJ; ' +
      'số và diễn giải gốc ghi vào diễn giải. Mã chứng từ đã nhập sẽ bị từ chối nếu nhập lại. Sau khi nhập: người lập bấm "Gửi duyệt tất cả", ' +
      'người duyệt (khác người lập) bấm "Duyệt và ghi sổ tất cả". Cột "Loại chứng từ": để trống (chứng từ thường) hoặc CLS cho bút toán kết chuyển xác định kết quả.',
    fields: [
      { key: 'key', label: 'Mã chứng từ', aliases: ['ma chung tu', 'stt rec', 'khoa', 'key'], required: true },
      { key: 'date', label: 'Ngày', aliases: ['ngay', 'ngay ct', 'ngay chung tu', 'ngay hach toan', 'date'], required: true, date: true },
      { key: 'number', label: 'Số chứng từ gốc', aliases: ['so chung tu goc', 'so ct', 'so chung tu', 'number'] },
      { key: 'description', label: 'Diễn giải', aliases: ['dien giai', 'noi dung', 'description'] },
      { key: 'accountCode', label: 'Mã TK', aliases: ['ma tk', 'tai khoan', 'tk', 'so hieu tk'], required: true },
      { key: 'partnerCode', label: 'Mã đối tượng', aliases: ['ma doi tuong', 'doi tuong', 'ma kh', 'ma ncc'] },
      { key: 'debit', label: 'Nợ', aliases: ['no', 'ps no', 'phat sinh no', 'debit'], numeric: true },
      { key: 'credit', label: 'Có', aliases: ['co', 'ps co', 'phat sinh co', 'credit'], numeric: true },
      { key: 'currency', label: 'Loại tiền', aliases: ['loai tien', 'ma nt', 'currency'] },
      { key: 'foreignAmount', label: 'Số nguyên tệ', aliases: ['so nguyen te', 'nguyen te', 'foreign amount'], numeric: true },
      { key: 'lineDescription', label: 'Diễn giải dòng', aliases: ['dien giai dong', 'ghi chu dong'] },
      { key: 'type', label: 'Loại chứng từ', aliases: ['loai chung tu', 'loai ct', 'type'] },
    ],
    example: [['CT001', '2026-01-05', 'PC 12', 'Chi tạm ứng', 'TU', 'NV01', '5000000', '', '', '', ''],
      ['CT001', '2026-01-05', 'PC 12', 'Chi tạm ứng', 'TM01', '', '', '5000000', '', '', '']],
  },
}

/** Bulk submit / approve-and-post of imported vouchers (each voucher keeps its own maker-checker checks). */
function ImportedVoucherActions() {
  const [info, setInfo] = useState('')
  const { busy, error, run } = useAction()
  const act = (path: string, label: string) => run(async () => {
    const r = await post<{ done: number; failures: string[] }>(path)
    setInfo(`${label}: ${r.done} chứng từ.${r.failures.length ? ` Lỗi ${r.failures.length}: ${r.failures.slice(0, 10).join(' | ')}` : ''}`)
  })
  return <section className="panel">
    <ErrorBox error={error} />
    {info && <div className={info.includes('Lỗi') ? 'msg error' : 'msg ok'}>{info}</div>}
    <div className="toolbar">
      <button disabled={busy} onClick={() => act('/import/vouchers/submit', 'Đã gửi duyệt')}>Gửi duyệt tất cả chứng từ đã nhập (người lập)</button>
      <button className="primary" disabled={busy} onClick={() => act('/import/vouchers/approve-post', 'Đã duyệt và ghi sổ')}>Duyệt và ghi sổ tất cả (người duyệt)</button>
    </div>
    <p className="muted">Ghi sổ theo thứ tự ngày; chứng từ lỗi (kỳ đã khóa, số dư âm bị chặn…) được liệt kê, các chứng từ khác vẫn tiếp tục.</p>
  </section>
}

interface Issue { line: number; field: string; message: string }
interface Result { committed: boolean; valid: number; skipped: number; issues: Issue[]; notes: string[]; voucherNumber?: string | null; totalDebit?: string; totalCredit?: string; documents?: string[] | null }

/** Maps the sheet's header row to fields: the first row (of the first 10) where at least two headers are recognised. */
function mapSheet(grid: string[][], fields: Field[]) {
  for (let h = 0; h < Math.min(10, grid.length); h++) {
    const keys = grid[h].map(headerKey)
    const map: Record<string, number> = {}
    for (const f of fields) {
      const i = keys.findIndex(k => f.aliases.includes(k) || k === headerKey(f.label))
      if (i >= 0 && !Object.values(map).includes(i)) map[f.key] = i
    }
    if (Object.keys(map).length >= 2) {
      const rows = grid.slice(h + 1).map((r, i) => ({ r, line: h + 2 + i })).filter(x => x.r.some(c => c.trim() !== ''))
        .map(({ r, line }) => {
          const o: Record<string, string | number | null> = { line }
          for (const f of fields) {
            const v = map[f.key] === undefined ? '' : (r[map[f.key]] ?? '').trim()
            o[f.key] = v === '' ? null : f.date ? excelDate(v) : v
          }
          return o
        })
      return { map, rows, missing: fields.filter(f => f.required && map[f.key] === undefined).map(f => f.label), unknown: grid[h].filter((c, i) => c.trim() && !Object.values(map).includes(i)) }
    }
  }
  return null
}

/** Nhập danh mục và số dư đầu kỳ từ Excel/CSV: đọc tệp → kiểm tra (không ghi) → nhập (tất cả hoặc không gì cả). */
export function ImportData() {
  const [kind, setKind] = useState<keyof typeof KINDS>('accounts')
  const [fileName, setFileName] = useState('')
  const [sheet, setSheet] = useState<ReturnType<typeof mapSheet>>(null)
  const [result, setResult] = useState<Result | null>(null)
  const [date, setDate] = useState(() => `${today().slice(0, 4)}-01-01`)
  const [description, setDescription] = useState('')
  const [counter, setCounter] = useState('')
  const [opb, setOpb] = useState('')
  const [accounts, setAccounts] = useState<Account[]>([])
  const { busy, error, run } = useAction()
  useEffect(() => { if (kind === 'stock' && accounts.length === 0) get<Account[]>('/accounts').then(setAccounts).catch(() => undefined) }, [kind, accounts.length])
  const dated = kind === 'opening' || kind === 'stock'
  const k = KINDS[kind]
  const reset = () => { setSheet(null); setResult(null); setFileName('') }

  const send = (commit: boolean) => run(async () => {
    if (!sheet) return
    const body = kind === 'opening' ? { date, description: description || null, rows: sheet.rows, commit }
      : kind === 'stock' ? { date, counterAccountCode: counter, description: description || null, rows: sheet.rows, commit }
      : kind === 'assets' ? { openingVoucherNumber: opb, rows: sheet.rows, commit }
      : { rows: sheet.rows, commit }
    setResult(await post<Result>(k.path, body))
  })

  return (
    <>
      <ErrorBox error={error} />
      <section className="panel">
        <div className="form-row">
          <label>Loại dữ liệu <select value={kind} onChange={e => { setKind(e.target.value as keyof typeof KINDS); reset() }}>
            {Object.entries(KINDS).map(([key, v]) => <option key={key} value={key}>{v.title}</option>)}</select></label>
          <button onClick={() => downloadXlsx(`mau-nhap-${kind}`, k.title, k.fields.map(f => ({ header: f.label, numeric: f.numeric })), k.example)}>Tải tệp mẫu (.xlsx)</button>
          <label>Tệp dữ liệu (.xlsx, .csv) <input type="file" accept=".xlsx,.csv,.txt" onChange={e => {
            const f = e.target.files?.[0]
            e.target.value = ''
            if (!f) return
            run(async () => {
              setResult(null); setFileName(f.name)
              const mapped = mapSheet(await readSheetFile(f), k.fields)
              if (!mapped) throw new Error('Không tìm thấy dòng tiêu đề (cần ít nhất 2 cột trùng tên với tệp mẫu trong 10 dòng đầu).')
              setSheet(mapped)
            })
          }} /></label>
        </div>
        <p className="muted">{k.help}</p>
        {dated && <div className="form-row">
          <label>Ngày số dư (ngày ghi sổ) <DateInput value={date} onChange={e => setDate(e.target.value)} /></label>
          {kind === 'stock' && <label>TK trung gian (Có) <select value={counter} onChange={e => setCounter(e.target.value)}><option value="">—</option>
            {accounts.filter(a => a.isPostable && a.isActive && !a.requiresPartner).map(a => <option key={a.id} value={a.code}>{a.code} – {a.name}</option>)}</select></label>}
          <label style={{ flex: 1 }}>Diễn giải chứng từ <input value={description} onChange={e => setDescription(e.target.value)} placeholder={kind === 'stock' ? 'Tồn kho đầu kỳ' : 'Số dư đầu kỳ chuyển sang'} /></label>
        </div>}
        {kind === 'assets' && <div className="form-row">
          <label>Số chứng từ OPB đã ghi sổ <input value={opb} onChange={e => setOpb(e.target.value.toUpperCase())} placeholder="VD: OPB2026-000001" /></label>
        </div>}
      </section>

      {sheet && <section className="panel">
        <h3>{fileName}: {sheet.rows.length} dòng dữ liệu</h3>
        <p>Cột nhận ra: {k.fields.filter(f => sheet.map[f.key] !== undefined).map(f => f.label).join(', ')}
          {sheet.unknown.length > 0 && <> · <span className="muted">bỏ qua cột: {sheet.unknown.join(', ')}</span></>}</p>
        {sheet.missing.length > 0 && <div className="msg error">Thiếu cột bắt buộc: {sheet.missing.join(', ')}</div>}
        <div className="scroll" style={{ maxHeight: 260 }}><table className="grid">
          <thead><tr><th>Dòng</th>{k.fields.map(f => <th key={f.key}>{f.label}</th>)}</tr></thead>
          <tbody>{sheet.rows.slice(0, 200).map(r => <tr key={String(r.line)}><td>{r.line}</td>
            {k.fields.map(f => <td key={f.key} className={f.numeric ? 'num' : undefined}>{r[f.key] ?? ''}</td>)}</tr>)}</tbody></table></div>
        {sheet.rows.length > 200 && <p className="muted">(hiển thị 200 dòng đầu)</p>}
        <div className="toolbar">
          <button disabled={busy || sheet.missing.length > 0 || sheet.rows.length === 0 || (kind === 'stock' && !counter) || (kind === 'assets' && !opb)} onClick={() => send(false)}>Kiểm tra</button>
          <button className="primary" disabled={busy || !result || result.committed || result.issues.length > 0} onClick={() => send(true)}
            title="Chỉ nhập khi lần kiểm tra không còn lỗi">{kind === 'vouchers' ? 'Tạo chứng từ (Nháp)' : kind === 'opening' ? 'Tạo chứng từ số dư (Nháp)' : kind === 'stock' ? 'Tạo phiếu nhập kho (Nháp)' : kind === 'assets' ? 'Tạo thẻ tài sản (Nháp)' : 'Nhập'}</button>
        </div>
      </section>}

      {result && <section className="panel">
        {result.committed
          ? <div className="msg ok">{kind === 'vouchers'
            ? `Đã tạo ${result.valid} chứng từ nháp${result.documents?.length ? ` (${result.documents[0]} … ${result.documents[result.documents.length - 1]})` : ''}.`
            : kind === 'stock'
            ? `Đã tạo phiếu nhập kho nháp ${result.voucherNumber} (${result.valid} dòng). Ghi sổ ở màn hình Kho.`
            : kind === 'opening'
            ? `Đã tạo chứng từ ${result.voucherNumber} (Nháp, ${result.valid} dòng). Vào màn hình Chứng từ để gửi duyệt và ghi sổ.`
            : `Đã nhập ${result.valid} dòng${result.skipped ? `, bỏ qua ${result.skipped} mã đã có` : ''}.`}</div>
          : result.issues.length === 0
            ? <div className="msg ok">Hợp lệ: {result.valid} dòng sẽ được nhập{result.skipped ? `, ${result.skipped} mã đã có sẽ bỏ qua` : ''}. Bấm nút nhập để ghi.</div>
            : <div className="msg error">{result.issues.length} lỗi — chưa ghi gì. Sửa tệp rồi chọn lại.</div>}
        {(kind === 'opening' || kind === 'vouchers') && result.totalDebit !== undefined &&
          <p>Tổng Nợ <b>{formatMoney(result.totalDebit)}</b> · Tổng Có <b>{formatMoney(result.totalCredit)}</b></p>}
        {result.notes.length > 0 && <ul>{result.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>}
        {result.issues.length > 0 && <div className="scroll" style={{ maxHeight: 320 }}><table className="grid">
          <thead><tr><th>Dòng</th><th>Cột</th><th>Lỗi</th></tr></thead>
          <tbody>{result.issues.map((i, n) => <tr key={n}><td>{i.line || '—'}</td><td>{i.field}</td><td>{i.message}</td></tr>)}</tbody></table></div>}
      </section>}
      {kind === 'vouchers' && <ImportedVoucherActions />}
    </>
  )
}
