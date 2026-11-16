export class ApiError extends Error {
  readonly code: string
  readonly status: number
  readonly errors: { code: string; message: string; lineNo?: number | null }[]
  constructor(status: number, code: string, message: string, errors: ApiError['errors'] = []) {
    super(message)
    this.status = status
    this.code = code
    this.errors = errors
  }
}

let token: string | null = sessionStorageGet('hak.token')
let companyId: string | null = sessionStorageGet('hak.company')

function sessionStorageGet(k: string): string | null {
  try { return sessionStorage.getItem(k) } catch { return null }
}
function sessionStorageSet(k: string, v: string | null) {
  try { if (v == null) sessionStorage.removeItem(k); else sessionStorage.setItem(k, v) } catch { /* private mode */ }
}

export const session = {
  get token() { return token },
  get companyId() { return companyId },
  setToken(t: string | null) { token = t; sessionStorageSet('hak.token', t) },
  setCompany(c: string | null) { companyId = c; sessionStorageSet('hak.company', c) },
}

/** fetch that reports a stopped / unreachable HAK server in Vietnamese instead of the browser's "TypeError: Failed to fetch". */
async function send(url: string, init: RequestInit): Promise<Response> {
  try { return await fetch(url, init) }
  catch {
    throw new ApiError(0, 'KHONG_KET_NOI', 'Không kết nối được máy chủ HAK — có thể HAK đã tắt. Mở lại bằng tệp "Chay HAK", chờ trang tự mở rồi thao tác lại (dữ liệu chưa bị thay đổi).')
  }
}

/** Response body as JSON; a non-JSON answer (e.g. a proxy error page) becomes a readable error. */
function parse(text: string, status: number): any {
  if (!text) return undefined
  try { return JSON.parse(text) }
  catch { throw new ApiError(status, 'HTTP_' + status, `Máy chủ trả lời không đúng định dạng (mã ${status}).`) }
}

export async function api<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { 'X-Device': 'web' }
  if (token) headers.Authorization = `Bearer ${token}`
  if (companyId) headers['X-Company-Id'] = companyId
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const res = await send(`/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = parse(text, res.status)
  if (!res.ok) {
    if (res.status === 401) session.setToken(null)
    throw new ApiError(res.status, data?.code ?? 'HTTP_' + res.status, data?.message ?? res.statusText, data?.errors ?? [])
  }
  return data as T
}

/** Sends a text body exactly as given (signed JSON packages must reach the server byte for byte). */
export async function postRaw<T>(path: string, text: string): Promise<T> {
  const headers: Record<string, string> = { 'X-Device': 'web', 'Content-Type': 'application/json' }
  if (token) headers.Authorization = `Bearer ${token}`
  if (companyId) headers['X-Company-Id'] = companyId
  const res = await send(`/api${path}`, { method: 'POST', headers, body: text })
  const data = parse(await res.text(), res.status)
  if (!res.ok) throw new ApiError(res.status, data?.code ?? 'HTTP_' + res.status, data?.message ?? res.statusText, data?.errors ?? [])
  return data as T
}

export const get = <T,>(p: string) => api<T>('GET', p)
export const post = <T,>(p: string, b?: unknown) => api<T>('POST', p, b ?? {})
export const put = <T,>(p: string, b?: unknown) => api<T>('PUT', p, b)

// ---- DTOs (money fields are decimal strings) ----
export interface Account { id: string; code: string; name: string; parentId: string | null; isPostable: boolean; requiresPartner: boolean; isActive: boolean; openItemNature: string; cashKind: string; reportTags?: string | null; negativeBalancePolicy?: string }
export interface Partner { id: string; code: string; name: string; taxCode: string | null; isActive: boolean }
export interface Period { id: string; fiscalYearId: string; periodNo: number; startDate: string; endDate: string; status: 'Open' | 'Locked' }
export interface VoucherLine { lineNo: number; accountId: string; side: 'D' | 'C'; amount: string; partnerId: string | null; description: string | null; cashFlowCode?: string | null; analysisCodeId?: string | null }
export interface Voucher { id: string; number: string; voucherType: string; status: string; voucherDate: string; postingDate: string; description: string; totalDebit: string; totalCredit: string; rowVersion: number; reversesVoucherId: string | null; lines: VoucherLine[] }
export interface TrialBalanceRow { accountId: string; accountCode: string; accountName: string; openingDebit: string; openingCredit: string; periodDebit: string; periodCredit: string; closingDebit: string; closingCredit: string; level?: number; isGroup?: boolean }
export interface OpenItem { journalLineId: string; voucherId: string; voucherNumber: string; voucherType: string; postingDate: string; dueDate: string; partnerId: string; side: 'Debit' | 'Credit'; amount: string; allocated: string; open: string; description: string | null
  foreignCurrency?: string | null; foreignAmount?: string | null; openForeign?: string | null }
