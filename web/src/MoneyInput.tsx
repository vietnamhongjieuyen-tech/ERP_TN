import { useState, type InputHTMLAttributes } from 'react'
import { formatMoney, parseMoney, parseRate } from './money'

type Props = Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'value' | 'onChange'> & {
  value: string
  /** Same event shape as a native input; the value stays the text the user sees (callers parse it on save, as before). */
  onChange: (e: { target: { value: string } }) => void
  /** money: up to 4 decimals (default) · rate: exchange rate / unit price, up to 6 decimals · or a parser of the caller's own. */
  kind?: 'money' | 'rate' | ((text: string) => string | null)
  /** Allow an empty field (default true); an empty required field is flagged only by the caller. */
  allowEmpty?: boolean
}

/**
 * Amount field used on every screen: right-aligned, numeric keyboard, accepts "1.234.567,5", "1234567,5" or "1234567.5",
 * groups the digits when leaving the field ("1234567" → "1.234.567") and marks text that is not an amount. The value is never
 * turned into a JavaScript number (money stays a decimal string end-to-end).
 */
export function MoneyInput({ value, onChange, kind = 'money', allowEmpty = true, className, ...rest }: Props) {
  const [touched, setTouched] = useState(false)
  const parse = typeof kind === 'function' ? kind : kind === 'rate' ? parseRate : parseMoney
  const empty = value.trim() === ''
  const invalid = touched && (empty ? !allowEmpty : parse(value) == null)
  return (
    <input {...rest} type="text" inputMode="decimal" autoComplete="off" value={value}
      className={`num${className ? ' ' + className : ''}`}
      aria-invalid={invalid || rest['aria-invalid'] || undefined}
      title={invalid ? 'Số tiền không hợp lệ (ví dụ 1.234.567 hoặc 1234567,5)' : rest.title}
      onChange={e => { setTouched(false); onChange({ target: { value: e.target.value } }) }}
      onBlur={e => {
        setTouched(true)
        const p = parse(e.target.value)
        if (p != null) {
          const shown = formatMoney(p)
          if (shown !== e.target.value) onChange({ target: { value: shown } })
        }
        rest.onBlur?.(e)
      }} />
  )
}
