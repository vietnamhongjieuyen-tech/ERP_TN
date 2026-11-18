import { useEffect, useRef, useState, type InputHTMLAttributes } from 'react'
import { formatDmy, parseDmy } from './dates'

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'value' | 'onChange' | 'min' | 'max'> & {
  value: string
  /** Called with an ISO yyyy-MM-dd value ('' when cleared); the event shape matches a native input so callers stay the same. */
  onChange: (e: { target: { value: string } }) => void
  min?: string
  max?: string
}

/**
 * Date field shown as dd/MM/yyyy on every computer (a native date input follows the browser's language, e.g. MM/dd/yyyy on an
 * English Windows). Type the date or pick it on the calendar; the value is passed on only once it is a real date within min/max.
 */
export function DateInput({ value, onChange, min, max, className, style, ...rest }: Props) {
  const [text, setText] = useState(formatDmy(value))
  const [editing, setEditing] = useState(false)
  const picker = useRef<HTMLInputElement>(null)
  useEffect(() => { if (!editing) setText(formatDmy(value)) }, [value, editing])
  const inRange = (iso: string) => (!min || iso >= min) && (!max || iso <= max)
  const emit = (iso: string) => { if (iso !== value) onChange({ target: { value: iso } }) }
  const typed = parseDmy(text, true)
  const invalid = editing && text.trim() !== '' && (!typed || !inRange(typed))
  return (
    <span className={`date-input${className ? ' ' + className : ''}`} style={style}>
      <input {...rest} type="text" inputMode="numeric" autoComplete="off" placeholder="dd/mm/yyyy" size={10} value={text} aria-invalid={invalid || undefined}
        title={invalid ? `Ngày không hợp lệ${min || max ? ` (trong khoảng ${formatDmy(min) || '…'} – ${formatDmy(max) || '…'})` : ''}` : rest.title}
        onFocus={e => { setEditing(true); rest.onFocus?.(e) }}
        onChange={e => {
          setText(e.target.value)
          const iso = parseDmy(e.target.value)
          if (iso && inRange(iso)) emit(iso)
        }}
        onBlur={e => {
          setEditing(false)
          const t = e.target.value.trim()
          if (t === '') emit('')
          else { const iso = parseDmy(t, true); if (iso && inRange(iso)) emit(iso) }
          rest.onBlur?.(e)
        }} />
      <button type="button" className="date-pick" tabIndex={-1} aria-label="Chọn ngày trên lịch" disabled={rest.disabled}
        onClick={() => { const p = picker.current; if (!p) return; try { p.showPicker() } catch { p.focus() } }}>▾</button>
      <input ref={picker} type="date" className="date-native" tabIndex={-1} aria-hidden="true" value={value} min={min} max={max}
        onChange={e => e.target.value && emit(e.target.value)} />
    </span>
  )
}
