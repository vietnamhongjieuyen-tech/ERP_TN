import { useEffect, useState, type ReactNode } from 'react'
import { get, session } from './api'

// Printed accounting documents follow the reference forms of TT 99/2025/TT-BTC Phụ lục I (Điều 9: the forms are for reference;
// a company that designs its own must issue an internal accounting regulation). Layouts were copied from Công báo 1563+1564.

export const TT99_FORM_NOTE = '(Kèm theo Thông tư số 99/2025/TT-BTC ngày 27 tháng 10 năm 2025 của Bộ trưởng Bộ Tài chính)'

/** Top of a Phụ lục I form: unit and address / department on the left, form number on the right. */
export function FormHeader({ company, second, formNo }: { company: string; second: ReactNode; formNo: string }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', gap: 16 }}>
      <div>Đơn vị: <b>{company || '...................'}</b><br />{second}</div>
      <div className="center" style={{ maxWidth: 300 }}><b>Mẫu số {formNo}</b><br /><i style={{ fontSize: '9pt' }}>{TT99_FORM_NOTE}</i></div>
    </div>
  )
}

/** "Ngày 05 tháng 03 năm 2026" from an ISO date. */
export function dateWords(iso: string) {
  const [y, m, d] = iso.split('-')
  return `Ngày ${d} tháng ${m} năm ${y}`
}

export const FORM_FOOT = 'Mẫu theo Phụ lục I Thông tư 99/2025/TT-BTC (mẫu tham khảo, Điều 9). In từ HAK Accounting.'

/** Name and address of the selected company, for the heading of printed forms and books. */
export function useCompanyInfo() {
  const [info, setInfo] = useState({ name: '', address: '' })
  useEffect(() => {
    get<{ id: string; name: string; address?: string | null }[]>('/me/companies')
      .then(cs => { const c = cs.find(x => x.id === session.companyId); if (c) setInfo({ name: c.name, address: c.address ?? '' }) })
      .catch(() => undefined)
  }, [])
  return info
}

/** Foot of a Phụ lục III book: page count, opening date and the three signatures of the form. */
export function BookFoot({ date }: { date: string }) {
  return <>
    <p>- Sổ này có .... trang, đánh số từ trang số 01 đến trang ....<br />- Ngày mở sổ: ....................</p>
    <div className="right"><i>{dateWords(date)}</i></div>
    <div className="signs" style={{ gridTemplateColumns: 'repeat(3, 1fr)' }}>
      <div><b>Người ghi sổ</b><br /><i>(Ký, họ tên)</i></div><div><b>Kế toán trưởng</b><br /><i>(Ký, họ tên)</i></div>
      <div><b>Người đại diện theo pháp luật</b><br /><i>(Ký, họ tên, đóng dấu)</i></div>
    </div>
    <div className="foot">Mẫu theo Phụ lục III Thông tư 99/2025/TT-BTC (mẫu tham khảo, Điều 12). In từ HAK Accounting lúc {new Date().toLocaleString('vi-VN')}.</div>
  </>
}

export const dmy = (iso: string | null | undefined) => (iso ? iso.split('-').reverse().join('/') : '')