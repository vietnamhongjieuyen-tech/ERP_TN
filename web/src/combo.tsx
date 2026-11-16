import { useEffect, useRef, useState } from 'react'
import { searchOptions, type Option } from './text'

/**
 * Type-to-search picker for long lists (accounts, partners, items): type part of the code or the name (accents optional),
 * ↑/↓ to move, Enter or Tab to take the highlighted row, Esc to cancel. Clearing the text clears the value.
 * onCreate adds a last row "+ Thêm mới" that hands the typed text to the caller.
 */
export function SearchSelect({ options, value, onChange, ariaLabel, placeholder = '— chọn —', onCreate, width }: {
  options: readonly Option[]; value: string; onChange: (id: string) => void; ariaLabel?: string; placeholder?: string
  onCreate?: (typed: string) => void; width?: number
}) {
  const selected = options.find(o => o.id === value)
  const label = selected ? `${selected.code} – ${selected.name}` : ''
  const [text, setText] = useState<string | null>(null)   // null = not editing: show the selected label
  const [edited, setEdited] = useState(false)             // the user typed since the box got the focus
  const [hi, setHi] = useState(0)
  const [box, setBox] = useState<{ left: number; top: number; width: number } | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const open = text !== null && box !== null
  const matches = open ? searchOptions(options, edited ? text : '') : []
  const rows = matches.length + (onCreate ? 1 : 0)

  const place = () => {
    const r = input.current?.getBoundingClientRect()
    if (r) setBox({ left: r.left, top: r.bottom + 2, width: Math.max(r.width, 320) })
  }
  const close = () => { setText(null); setBox(null); setEdited(false) }
  const pick = (i: number) => {
    if (i < matches.length) { onChange(matches[i].id); close() }
    else if (onCreate) { const typed = edited ? text ?? '' : ''; close(); onCreate(typed.trim()) }
  }
  // The list is positioned on the viewport (it must not be clipped by scrolling tables), so it closes when the page moves.
  useEffect(() => {
    if (!open) return
    const off = (e: Event) => { if (!(e.target instanceof Node && document.getElementById('combo-list')?.contains(e.target))) close() }
    window.addEventListener('scroll', off, true)
    window.addEventListener('resize', off)
    return () => { window.removeEventListener('scroll', off, true); window.removeEventListener('resize', off) }
  }, [open])

  return (
    <span className="combo" style={width ? { width } : undefined}>
      <input ref={input} role="combobox" aria-expanded={open} aria-label={ariaLabel} aria-autocomplete="list" placeholder={placeholder} autoComplete="off"
        value={edited ? text ?? '' : label} title={label}
        onFocus={e => { setText(label); setEdited(false); setHi(0); place(); e.target.select() }}
        onChange={e => { setText(e.target.value); setEdited(true); setHi(0); place() }}
        onBlur={() => {
          // Leaving with text typed: an exact code takes that option, an emptied box clears the value, anything else is dropped.
          if (edited && text !== null) {
            const typed = text.trim().toLowerCase()
            if (typed === '') onChange('')
            else { const exact = options.find(o => o.code.toLowerCase() === typed); if (exact) onChange(exact.id) }
          }
          close()
        }}
        onKeyDown={e => {
          if (!open) { if (e.key === 'ArrowDown') { setText(label); setEdited(false); place(); e.preventDefault() } return }
          if (e.key === 'ArrowDown') { setHi(h => Math.min(h + 1, rows - 1)); e.preventDefault() }
          else if (e.key === 'ArrowUp') { setHi(h => Math.max(h - 1, 0)); e.preventDefault() }
          else if (e.key === 'Escape') { close(); e.stopPropagation() }
          else if ((e.key === 'Enter' || e.key === 'Tab') && edited && text.trim() !== '' && rows > 0) {
            // Enter also moves on to the next field (handled by the form); creating a new entry stays here.
            if (hi >= matches.length) e.preventDefault()
            pick(hi)
          }
        }} />
      {open && rows > 0 && (
        <ul id="combo-list" className="combo-list" role="listbox" style={{ left: box.left, top: box.top, width: box.width }}>
          {matches.map((o, i) => (
            <li key={o.id} role="option" aria-selected={i === hi} className={i === hi ? 'hi' : undefined}
              onMouseDown={e => { e.preventDefault(); pick(i) }} onMouseEnter={() => setHi(i)}><b>{o.code}</b> – {o.name}</li>))}
          {onCreate && <li role="option" aria-selected={hi === matches.length} className={'create' + (hi === matches.length ? ' hi' : '')}
            onMouseDown={e => { e.preventDefault(); pick(matches.length) }} onMouseEnter={() => setHi(matches.length)}>+ Thêm mới{edited && text.trim() ? ` “${text.trim()}”` : ''}…</li>}
        </ul>
      )}
    </span>
  )
}
