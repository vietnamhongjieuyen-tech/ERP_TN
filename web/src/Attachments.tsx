import { useCallback, useEffect, useState } from 'react'
import { downloadBlob, get, post, uploadFile } from './api'
import { ErrorBox, useAction } from './common'
import { askText } from './dialogs'

interface Attachment { id: string; fileName: string; size: number; uploadedAt: string; isRemoved: boolean; removeReason: string | null }

const ACCEPT = '.pdf,.jpg,.jpeg,.png,.xlsx,.xls,.docx,.doc,.xml,.csv,.txt,.zip'
const sizeText = (n: number) => (n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`)

/** Source documents of one voucher: add (also after posting), open, save, remove with a reason. Removed files stay listed. */
export function Attachments({ voucherId }: { voucherId: string }) {
  const [list, setList] = useState<Attachment[]>([])
  const { busy, error, run } = useAction()
  const load = useCallback(() => run(async () => setList(await get<Attachment[]>(`/vouchers/${voucherId}/attachments`))), [run, voucherId])
  useEffect(() => { load() }, [load])

  const add = (picked: FileList | null) => { const files = [...(picked ?? [])]; return run(async () => {
    // One refused file (wrong type, too large) must not hide the ones already stored: the list is reloaded either way.
    try { for (const f of files) await uploadFile(`/vouchers/${voucherId}/attachments?fileName=${encodeURIComponent(f.name)}`, f) }
    finally { setList(await get<Attachment[]>(`/vouchers/${voucherId}/attachments`)) }
  }) }
  // View opens PDFs and pictures in a new tab; everything else (and "Tải về") is saved as a file.
  const open = (a: Attachment, save: boolean) => run(async () => {
    const url = URL.createObjectURL(await downloadBlob(`/attachments/${a.id}`))
    if (!save && /\.(pdf|jpe?g|png)$/i.test(a.fileName)) window.open(url, '_blank', 'noopener')
    else { const link = document.createElement('a'); link.href = url; link.download = a.fileName; link.click() }
    setTimeout(() => URL.revokeObjectURL(url), 60_000)
  })
  const active = list.filter(a => !a.isRemoved)

  return (
    <div style={{ marginTop: 10 }}>
      <ErrorBox error={error} />
      <div className="toolbar" style={{ marginBottom: 4 }}>
        <strong>Tệp đính kèm ({active.length})</strong>
        <label className="check" title="PDF, ảnh, Excel, Word, XML… tối đa 10 MB mỗi tệp">
          <input type="file" multiple accept={ACCEPT} disabled={busy} onChange={e => { add(e.target.files); e.target.value = '' }} /></label>
      </div>
      {list.length > 0 && <table className="grid">
        <tbody>{list.map(a => (
          <tr key={a.id} className={a.isRemoved ? 'muted' : undefined}>
            <td>{a.fileName}</td><td className="num">{sizeText(a.size)}</td><td>{new Date(a.uploadedAt).toLocaleString('vi-VN')}</td>
            <td>{a.isRemoved ? `Đã gỡ: ${a.removeReason ?? ''}` : ''}</td>
            <td style={{ display: 'flex', gap: 4 }}>
              <button disabled={busy} onClick={() => open(a, false)}>Xem</button>
              <button disabled={busy} onClick={() => open(a, true)}>Tải về</button>
              {!a.isRemoved && <button className="danger" disabled={busy} onClick={async () => {
                const reason = await askText(`Lý do gỡ tệp ${a.fileName} (tệp vẫn được lưu trong nhật ký):`)
                if (reason) run(async () => { await post(`/attachments/${a.id}/remove`, { reason }); await load() })
              }}>Gỡ</button>}
            </td>
          </tr>))}
        </tbody>
      </table>}
    </div>
  )
}
