// Jumping between screens with parameters (drill-down): a report row opens the account book, a book row opens the voucher.
// The target screen takes its parameters once, when it mounts.

export type Params = Record<string, string>

let pending: { screen: string; params: Params } | null = null

export function goTo(screen: string, params: Params = {}) {
  pending = { screen, params }
  window.dispatchEvent(new CustomEvent('hak:goto', { detail: screen }))
}

/** Parameters handed to this screen by goTo (once), or undefined when it was opened from the menu. */
export function takeParams(screen: string): Params | undefined {
  if (pending?.screen !== screen) return undefined
  const p = pending.params
  pending = null
  return p
}

/** Tells every open catalogue (accounts, partners) to reload — after a partner is added from inside a form. */
export const catalogChanged = () => window.dispatchEvent(new Event('hak:catalog'))
