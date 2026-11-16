// Text folding for look-ups typed by users: case and Vietnamese accents are ignored ("hoa don" finds "Hóa đơn").

const FOLD: Record<string, string> = {}
for (const [chars, plain] of [
  ['àáạảãâầấậẩẫăằắặẳẵ', 'a'], ['èéẹẻẽêềếệểễ', 'e'], ['ìíịỉĩ', 'i'], ['òóọỏõôồốộổỗơờớợởỡ', 'o'],
  ['ùúụủũưừứựửữ', 'u'], ['ỳýỵỷỹ', 'y'], ['đ', 'd'],
] as const) for (const c of chars) FOLD[c] = plain

export function foldText(s: string): string {
  let out = ''
  for (const c of s.trim().toLowerCase()) out += FOLD[c] ?? c
  return out
}

export interface Option { id: string; code: string; name: string }

/**
 * Options matching what was typed, best first: code starts with it, then a word of the name starts with it, then it appears
 * anywhere. Every typed word must match. An empty query returns the first options unchanged.
 */
export function searchOptions<T extends Option>(options: readonly T[], typed: string, limit = 50): T[] {
  const words = foldText(typed).split(/\s+/).filter(Boolean)
  if (words.length === 0) return options.slice(0, limit)
  const ranked: { o: T; rank: number }[] = []
  for (const o of options) {
    const code = foldText(o.code), name = foldText(o.name), all = code + ' ' + name
    if (!words.every(w => all.includes(w))) continue
    const first = words[0]
    const rank = code === first ? 0 : code.startsWith(first) ? 1 : name.split(/\s+/).some(p => p.startsWith(first)) ? 2 : 3
    ranked.push({ o, rank })
  }
  return ranked.sort((a, b) => a.rank - b.rank).slice(0, limit).map(r => r.o)   // stable: equal ranks keep the list order
}
