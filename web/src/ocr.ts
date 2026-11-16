// OCR for statements that are pictures (a photo, a scan, a PDF without a text layer). Uses tesseract.js (Apache-2.0,
// github.com/naptha/tesseract.js) running entirely in the browser: the engine and the Vietnamese / English language data are
// served by the application itself (public/ocr, copied by scripts/copy-ocr-assets.mjs) — nothing is sent anywhere.
//
// The result is the same positioned text pdf.js gives for a real PDF, so pdftable.statementRowsFromPdf rebuilds the table.
// OCR misreads characters now and then: callers must show the result for review, and the statement's own balances
// (opening + movements = closing) are the check that the amounts were read correctly.

import type { TextItem } from './pdftable'

/** Width, in the units of pdftable (PDF points), every recognised page is scaled to. */
const PAGE_WIDTH = 842

export interface OcrWord { text: string; x0: number; y0: number; x1: number; y1: number }
export interface OcrLine { y1: number; height: number; words: OcrWord[] }

/**
 * Words of one recognised line joined into the phrases a table cell holds: neighbours closer than 0.7 of the line height
 * belong together ("Ngày giao dịch"), a wider gap is a column gap. Coordinates are scaled by `scale`.
 */
export function phrases(lines: OcrLine[], scale: number): TextItem[] {
  const items: TextItem[] = []
  for (const line of lines) {
    const words = line.words.filter(w => w.text.trim() !== '').sort((a, b) => a.x0 - b.x0)
    let cur: { x0: number; x1: number; s: string } | null = null
    const flush = () => { if (cur) items.push({ x: cur.x0 * scale, y: line.y1 * scale, w: (cur.x1 - cur.x0) * scale, s: cur.s }); cur = null }
    for (const w of words) {
      if (cur && w.x0 - cur.x1 <= line.height * 0.7) cur = { x0: cur.x0, x1: w.x1, s: cur.s + ' ' + w.text.trim() }
      else { flush(); cur = { x0: w.x0, x1: w.x1, s: w.text.trim() } }
    }
    flush()
  }
  return items
}

type Source = Blob | HTMLCanvasElement

async function toCanvas(src: Source): Promise<HTMLCanvasElement> {
  if (src instanceof HTMLCanvasElement) return src
  const bitmap = await createImageBitmap(src)
  // Small pictures are enlarged: the engine reads text best when letters are about 30 pixels high.
  const k = bitmap.width < 1600 ? 1600 / bitmap.width : 1
  const canvas = document.createElement('canvas')
  canvas.width = Math.round(bitmap.width * k)
  canvas.height = Math.round(bitmap.height * k)
  const g = canvas.getContext('2d')!
  g.fillStyle = '#fff'
  g.fillRect(0, 0, canvas.width, canvas.height)
  g.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
  bitmap.close()
  return canvas
}

/** Recognises the pages one after another with a single engine instance; onProgress gets (page index, 0…1). */
export async function ocrPages(pages: Source[], onProgress?: (page: number, done: number) => void): Promise<TextItem[][]> {
  const { createWorker } = await import('tesseract.js')
  const base = import.meta.env.BASE_URL + 'ocr'
  let current = 0
  const worker = await createWorker(['vie', 'eng'], 1, {
    workerPath: `${base}/worker.min.js`, corePath: base, langPath: `${base}/lang`, workerBlobURL: false,
    logger: m => { if (m.status === 'recognizing text') onProgress?.(current, m.progress) },
  })
  try {
    const out: TextItem[][] = []
    for (const [i, src] of pages.entries()) {
      current = i
      const canvas = await toCanvas(src)
      const { data } = await worker.recognize(canvas, {}, { blocks: true })
      const lines: OcrLine[] = []
      for (const b of data.blocks ?? []) for (const p of b.paragraphs) for (const l of p.lines)
        lines.push({ y1: l.bbox.y1, height: l.bbox.y1 - l.bbox.y0, words: l.words.map(w => ({ text: w.text, x0: w.bbox.x0, y0: w.bbox.y0, x1: w.bbox.x1, y1: w.bbox.y1 })) })
      out.push(phrases(lines, PAGE_WIDTH / canvas.width))
    }
    return out
  } finally { await worker.terminate() }
}
