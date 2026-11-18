// Development-only seed: creates a sample company so the UI can be exercised locally.
// Refuses to run unless the API reports Environment=Development. Accounts use neutral codes and are
// NOT the legal chart of accounts (Circular 99/2025 Appendix II is not yet verified — docs/07).
// Generated passwords are written to web/.dev-credentials.json (git-ignored), never printed.
import { randomBytes } from 'node:crypto'
import { writeFileSync } from 'node:fs'

const BASE = process.env.HAK_API ?? 'http://127.0.0.1:5080/api'

async function call(method, path, body, token, company) {
  const headers = { 'Content-Type': 'application/json' }
  if (token) headers.Authorization = `Bearer ${token}`
  if (company) headers['X-Company-Id'] = company
  const res = await fetch(BASE + path, { method, headers, body: body ? JSON.stringify(body) : undefined })
  const text = await res.text()
  if (!res.ok) throw new Error(`${method} ${path} → ${res.status} ${text}`)
  return text ? JSON.parse(text) : undefined
}

const health = await call('GET', '/health')
if (health.environment !== 'Development') {
  console.error(`Từ chối: API đang ở môi trường "${health.environment}". Seed chỉ dành cho Development.`)
  process.exit(1)
}

const pw = () => randomBytes(12).toString('base64url')
const creds = { admin: pw(), ketoan: pw(), ktt: pw() }
await call('POST', '/setup/bootstrap', { userName: 'admin', fullName: 'Quản trị (dev)', password: creds.admin })
const admin = (await call('POST', '/auth/login', { userName: 'admin', password: creds.admin })).token

const regime = (await call('POST', '/admin/regime-versions', {
  regimeCode: 'DEV-REGIME', regimeName: 'Chế độ dùng cho phát triển (chưa xác minh)', versionCode: 'DEV-1',
  effectiveFrom: '2020-01-01', effectiveTo: null, sources: [{ ruleGroup: 'DEV', documentNo: 'DEV-ONLY' }],
}, admin)).id
const company = (await call('POST', '/admin/companies', { code: 'DEV01', name: 'Công ty dữ liệu thử', taxCode: '0101234567', functionalCurrency: 'VND', regimeVersionId: regime }, admin)).id
const ketoan = (await call('POST', '/admin/users', { userName: 'ketoan', fullName: 'Kế toán (dev)', password: creds.ketoan }, admin)).id
const ktt = (await call('POST', '/admin/users', { userName: 'ktt', fullName: 'Kế toán trưởng (dev)', password: creds.ktt }, admin)).id
for (const [userId, roleCode] of [[ketoan, 'GL_ACCOUNTANT'], [ktt, 'CHIEF_ACCOUNTANT'], [ktt, 'ADMIN']])
  await call('POST', '/admin/grants', { userId, companyId: company, roleCode }, admin)

const k = (await call('POST', '/auth/login', { userName: 'ktt', password: creds.ktt })).token
const as = (m, p, b) => call(m, p, b, k, company)
const year = new Date().getFullYear()
await as('POST', '/fiscal-years', { code: String(year), startDate: `${year}-01-01`, endDate: `${year}-12-31` })

const acc = {}
for (const a of [
  { code: 'CASH', name: 'Tiền mặt (thử)', cashKind: 'Cash', negativeBalancePolicy: 'Block' },
  { code: 'BANK', name: 'Tiền gửi ngân hàng (thử)', cashKind: 'Bank', negativeBalancePolicy: 'Warn' },
  { code: 'BANK2', name: 'Tiền gửi ngân hàng 2 (thử)', cashKind: 'Bank' },
  { code: 'AR', name: 'Phải thu khách hàng (thử)', requiresPartner: true, balanceDisplay: 'GrossByPartner', openItemNature: 'Receivable' },
  { code: 'AP', name: 'Phải trả người bán (thử)', requiresPartner: true, balanceDisplay: 'GrossByPartner', openItemNature: 'Payable' },
  { code: 'REV', name: 'Doanh thu (thử)' }, { code: 'EXP', name: 'Chi phí (thử)' },
  { code: 'INV', name: 'Hàng tồn kho (thử)' },
  { code: 'GRNI', name: 'Hàng về chưa có hóa đơn (thử)' }, { code: 'VATIN', name: 'Thuế GTGT đầu vào (thử)' },
  { code: 'VATOUT', name: 'Thuế GTGT đầu ra (thử)' }, { code: 'SALESREV', name: 'Doanh thu bán hàng (thử)' }, { code: 'COGS', name: 'Giá vốn hàng bán (thử)' },
  { code: 'SALESRET', name: 'Hàng bán bị trả lại (thử)' }, { code: 'SALESDISC', name: 'Giảm giá hàng bán (thử)' },
  { code: 'RESULT', name: 'Xác định kết quả (thử)' }, { code: 'RETAINED', name: 'Lợi nhuận chưa phân phối (thử)' },
  { code: 'FA', name: 'TSCĐ (thử)' }, { code: 'FA_ACC', name: 'Hao mòn TSCĐ (thử)' }, { code: 'DEP_EXP', name: 'Chi phí khấu hao (thử)' },
  { code: 'PREPAID', name: 'CCDC / chi phí trả trước (thử)' }, { code: 'LOSS', name: 'Chi phí khác (thử)' },
  { code: 'BANKUSD', name: 'Tiền gửi USD (thử)', cashKind: 'Bank' }, { code: 'FXDIFF', name: 'Chênh lệch đánh giá lại tỷ giá (thử)' },
  { code: 'FXGAIN', name: 'Lãi tỷ giá đã thực hiện (thử)' }, { code: 'FXLOSS', name: 'Lỗ tỷ giá đã thực hiện (thử)' },
  { code: 'SAL_GEN', name: 'Chi phí lương (thử)' }, { code: 'SAL_PAY', name: 'Phải trả người lao động (thử)', requiresPartner: true, balanceDisplay: 'GrossByPartner', openItemNature: 'Payable' },
  { code: 'SI_PAY', name: 'BHXH phải nộp (thử)' }, { code: 'HI_PAY', name: 'BHYT phải nộp (thử)' }, { code: 'UI_PAY', name: 'BHTN phải nộp (thử)' },
  { code: 'PIT_PAY', name: 'Thuế TNCN phải nộp (thử)' }, { code: 'UNION_PAY', name: 'KPCĐ phải nộp (thử)' },
  { code: 'WIP', name: 'Chi phí SX dở dang – dăm (thử)' }, { code: 'RAW', name: 'Nguyên liệu gỗ (thử)' },
  { code: 'LOAN', name: 'Vay ngắn hạn (thử)' }, { code: 'INT_EXP', name: 'Chi phí lãi vay (thử)' }, { code: 'INT_ACC', name: 'Lãi vay phải trả (thử)' },
]) acc[a.code] = (await as('POST', '/accounts', { parentCode: null, ...a })).id
const par = {}
for (const p of [{ code: 'KH01', name: 'Khách hàng A', roles: 'Customer' }, { code: 'NCC01', name: 'Nhà cung cấp B', roles: 'Supplier' }, { code: 'VCB', name: 'Ngân hàng cho vay (thử)', roles: 'Other' }, { code: 'NV01', name: 'Nhân viên A (thử)', roles: 'Employee' }, { code: 'NV02', name: 'Nhân viên B (thử)', roles: 'Employee' }])
  par[p.code] = (await as('POST', '/partners', { taxCode: null, ...p })).id

for (const [roleCode, accountCode] of [['AR_TRADE', 'AR'], ['AP_TRADE', 'AP'], ['CASH_DEFAULT', 'CASH'], ['GRNI', 'GRNI'], ['VAT_INPUT', 'VATIN'], ['VAT_OUTPUT', 'VATOUT'], ['SALES_REVENUE', 'SALESREV'], ['SALES_RETURNS', 'SALESRET'], ['SALES_DISCOUNT', 'SALESDISC'], ['COGS', 'COGS'], ['FX_REVALUATION', 'FXDIFF'], ['FX_REALIZED_GAIN', 'FXGAIN'], ['FX_REALIZED_LOSS', 'FXLOSS'], ['PAYROLL_EXPENSE_GENERAL', 'SAL_GEN'], ['PAYROLL_PAYABLE', 'SAL_PAY'], ['PAYROLL_SI_PAYABLE', 'SI_PAY'], ['PAYROLL_HI_PAYABLE', 'HI_PAY'], ['PAYROLL_UI_PAYABLE', 'UI_PAY'], ['PAYROLL_PIT_PAYABLE', 'PIT_PAY'], ['PAYROLL_UNION_PAYABLE', 'UNION_PAY']])
  await as('POST', '/rules/role-mappings', { roleCode, accountCode })

const receipt = { lines: [{ side: 'D', account: 'DOC:MAIN_ACCOUNT|ROLE:CASH_DEFAULT' }, { side: 'C', account: 'ROLE:AR_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }] }
const rules = [
  ['CR-CUST', 'CR', 'CUSTOMER_RECEIPT', receipt, [{ name: 'thu', document: { partner_code: 'KH01', main_account_code: 'CASH', lines: [{ amount: '100' }] }, expected: [{ side: 'D', account_code: 'CASH', amount: '100' }, { side: 'C', account_code: 'AR', amount: '100', partner_code: 'KH01' }] }]],
  ['BR-CUST', 'BR', 'CUSTOMER_RECEIPT', receipt, [{ name: 'báo có', document: { partner_code: 'KH01', main_account_code: 'BANK', lines: [{ amount: '100' }] }, expected: [{ side: 'D', account_code: 'BANK', amount: '100' }, { side: 'C', account_code: 'AR', amount: '100', partner_code: 'KH01' }] }]],
  ['CP-EXP', 'CP', 'EXPENSE', { lines: [{ side: 'D', account: 'LINE:COUNTER_ACCOUNT', constraint: 'NOT_CASH_EQUIVALENT' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
    [{ name: 'chi', document: { main_account_code: 'CASH', lines: [{ amount: '5', counter_account_code: 'EXP' }] }, expected: [{ side: 'D', account_code: 'EXP', amount: '5' }, { side: 'C', account_code: 'CASH', amount: '5' }] }]],
  ['CP-SUPP', 'CP', 'SUPPLIER_PAYMENT', { lines: [{ side: 'D', account: 'ROLE:AP_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
    [{ name: 'trả', document: { partner_code: 'NCC01', main_account_code: 'CASH', lines: [{ amount: '7' }] }, expected: [{ side: 'D', account_code: 'AP', amount: '7', partner_code: 'NCC01' }, { side: 'C', account_code: 'CASH', amount: '7' }] }]],
  ['BP-SUPP', 'BP', 'SUPPLIER_PAYMENT', { lines: [{ side: 'D', account: 'ROLE:AP_TRADE', partner: 'DOC:PARTNER', settle: 'LINE:SETTLE' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
    [{ name: 'trả', document: { partner_code: 'NCC01', main_account_code: 'BANK', lines: [{ amount: '7' }] }, expected: [{ side: 'D', account_code: 'AP', amount: '7', partner_code: 'NCC01' }, { side: 'C', account_code: 'BANK', amount: '7' }] }]],
  ['BP-XFER', 'BP', 'INTERNAL_TRANSFER', { lines: [{ side: 'D', account: 'LINE:COUNTER_ACCOUNT', constraint: 'CASH_EQUIVALENT' }, { side: 'C', account: 'DOC:MAIN_ACCOUNT' }] },
    [{ name: 'chuyển', document: { main_account_code: 'BANK', lines: [{ amount: '9', counter_account_code: 'BANK2' }] }, expected: [{ side: 'D', account_code: 'BANK2', amount: '9' }, { side: 'C', account_code: 'BANK', amount: '9' }] }]],
]
for (const [code, documentType, businessEvent, definition, testCases] of rules) {
  const r = await as('POST', '/rules', { code, documentType, businessEvent, priority: 10, effectiveFrom: `${year}-01-01`, effectiveTo: null, definition, testCases, description: 'dev' })
  await as('POST', `/rules/${r.id}/activate`)
}
const set = await as('POST', '/closing/rule-sets', { code: 'DEV-CLOSE', effectiveFrom: `${year}-01-01`, effectiveTo: null, description: 'dev', steps: [
  { description: 'Kết chuyển doanh thu', sourcePrefixes: 'REV', targetAccountCode: 'RESULT' },
  { description: 'Kết chuyển chi phí', sourcePrefixes: 'EXP', targetAccountCode: 'RESULT' },
  { description: 'Kết chuyển lãi lỗ', sourcePrefixes: 'RESULT', targetAccountCode: 'RETAINED' }] })
await as('POST', `/closing/rule-sets/${set.id}/activate`)

// Inventory: one warehouse per costing method the business uses, one item measured in tonnes.
for (const [code, name, method] of [['K-FIFO', 'Kho FIFO (thử)', 'Fifo'], ['K-AVG', 'Kho bình quân tức thời (thử)', 'MovingAverage'], ['K-MON', 'Kho bình quân tháng (thử)', 'MonthlyAverage']]) {
  const w = await as('POST', '/warehouses', { code, name })
  await as('POST', '/warehouses/costing-policy', { warehouseId: w.id, method, effectiveFrom: `${year}-01-01`, note: 'dev seed' })
}
const dam = await as('POST', '/items', { code: 'DAM', name: 'Dăm gỗ (thử)', unit: 'tấn', inventoryAccountCode: 'INV' })
// Production: logs chipped into wood chips (a separate finished item without dryness tracking, for the costing screen).
await as('POST', '/items', { code: 'GOKEO', name: 'Gỗ keo nguyên liệu (thử)', unit: 'tấn', inventoryAccountCode: 'RAW' })
const damsx = await as('POST', '/items', { code: 'DAMSX', name: 'Dăm thành phẩm tự sản xuất (thử)', unit: 'MT', inventoryAccountCode: 'INV' })
await as('POST', '/production/cost-objects', { code: 'SX-DAM', name: 'Sản xuất dăm (thử)', itemId: damsx.id, wipAccountId: acc.WIP, plannedUnitCost: '700000' })
await as('POST', `/items/${dam.id}/tracks-dryness`, { tracks: true })
await as('POST', '/dryness-methods', { code: 'DO-KHO', description: 'BDMT = MT × độ khô% / 100; đo khi nhập, đo ở tàu khi xuất (chứng thư)', drynessDecimals: 2, bdmtDecimals: 3, effectiveFrom: `${year}-01-01`, approvalNote: 'Doanh nghiệp xác nhận 14/10/2026 (dev seed)' })

// A posted sales invoice (prepared by ketoan, approved and posted by ktt) to give the screens some data.
const e = (await call('POST', '/auth/login', { userName: 'ketoan', password: creds.ketoan })).token
const now = new Date() // local date, not UTC (toISOString gives yesterday before 07:00 in Vietnam)
const today = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
const inv = await call('POST', '/vouchers', { voucherType: 'GJ', voucherDate: today, postingDate: today, description: 'Bán hàng chưa thu tiền (thử)',
  lines: [{ accountId: acc.AR, side: 'D', amount: '15000000', partnerId: par.KH01 }, { accountId: acc.REV, side: 'C', amount: '15000000' }] }, e, company)
await call('POST', `/vouchers/${inv.id}/submit`, {}, e, company)
await as('POST', `/vouchers/${inv.id}/approve`)
await as('POST', `/vouchers/${inv.id}/post`)

// A posted asset purchase, so the asset screen has a cost line to assign.
const fa = await call('POST', '/vouchers', { voucherType: 'GJ', voucherDate: today, postingDate: today, description: 'Mua máy xúc (thử)',
  lines: [{ accountId: acc.FA, side: 'D', amount: '120000000' }, { accountId: acc.AP, side: 'C', amount: '120000000', partnerId: par.NCC01 }] }, e, company)
await call('POST', `/vouchers/${fa.id}/submit`, {}, e, company)
await as('POST', `/vouchers/${fa.id}/approve`)
await as('POST', `/vouchers/${fa.id}/post`)

// 1,000 USD received at 25,000 into the USD bank account, for the FX screen.
const usd = await call('POST', '/vouchers', { voucherType: 'GJ', voucherDate: today, postingDate: today, description: 'Nhận USD (thử)',
  lines: [{ accountId: acc.BANKUSD, side: 'D', amount: '25000000', foreignCurrency: 'USD', foreignAmount: '1000', exchangeRate: '25000' },
    { accountId: acc.REV, side: 'C', amount: '25000000' }] }, e, company)
await call('POST', `/vouchers/${usd.id}/submit`, {}, e, company)
await as('POST', `/vouchers/${usd.id}/approve`)
await as('POST', `/vouchers/${usd.id}/post`)

// A 1,000 USD receivable from KH01 at 25,000, for "Thu / chi ngoại tệ theo hóa đơn".
const usdSale = await call('POST', '/vouchers', { voucherType: 'GJ', voucherDate: today, postingDate: today, description: 'Bán hàng USD (thử)',
  lines: [{ accountId: acc.AR, side: 'D', amount: '25000000', partnerId: par.KH01, foreignCurrency: 'USD', foreignAmount: '1000', exchangeRate: '25000' },
    { accountId: acc.REV, side: 'C', amount: '25000000' }] }, e, company)
await call('POST', `/vouchers/${usdSale.id}/submit`, {}, e, company)
await as('POST', `/vouchers/${usdSale.id}/approve`)
await as('POST', `/vouchers/${usdSale.id}/post`)

writeFileSync(process.env.HAK_CREDENTIALS_FILE ?? new URL('../.dev-credentials.json', import.meta.url), JSON.stringify({ api: BASE, company: 'DEV01', users: creds }, null, 2))
console.log('Đã seed công ty DEV01. Mật khẩu ghi trong web/.dev-credentials.json (không commit).')

// A bank loan drawdown (not yet linked to a contract), for the loan screen.
const draw = await call('POST', '/vouchers', { voucherType: 'GJ', voucherDate: today, postingDate: today, description: 'Giải ngân khế ước vay (thử)',
  lines: [{ accountId: acc.BANK, side: 'D', amount: '1000000000' }, { accountId: acc.LOAN, side: 'C', amount: '1000000000', partnerId: par.VCB }] }, e, company)
await call('POST', `/vouchers/${draw.id}/submit`, {}, e, company)
await as('POST', `/vouchers/${draw.id}/approve`)
await as('POST', `/vouchers/${draw.id}/post`)