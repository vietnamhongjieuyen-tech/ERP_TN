import type { ReactNode } from 'react'

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
