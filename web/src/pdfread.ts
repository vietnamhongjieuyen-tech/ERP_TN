// pdf.js glue: text of every page as positioned items in upright coordinates (the page rotation is applied), loaded only when
// a PDF is actually opened so the library does not weigh on the rest of the application. A page without a text layer (a scan)
// is rendered to a picture and read by OCR instead.

import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url'
import type { TextItem } from './pdftable'

/** Fewer text items than this on a page means it is a scanned picture, not text. */
const SCAN_THRESHOLD = 8

export interface PdfText { pages: TextItem[][]; ocrPages: number }

export async function pdfTextItems(bytes: Uint8Array, onProgress?: (message: string) => void): Promise<PdfText> {
  const pdfjs = await import('pdfjs-dist')
  pdfjs.GlobalWorkerOptions.workerSrc = workerUrl
  const task = pdfjs.getDocument({ data: bytes })
  const doc = await task.promise
  try {
    const pages: TextItem[][] = []
    const scans: { index: number; canvas: HTMLCanvasElement }[] = []
    for (let p = 1; p <= doc.numPages; p++) {
      const page = await doc.getPage(p)
      const vp = page.getViewport({ scale: 1 })
      const content = await page.getTextContent()
      const items: TextItem[] = []
      for (const it of content.items) {
        if (!('str' in it) || it.str.trim() === '') continue
        const m = pdfjs.Util.transform(vp.transform, it.transform)   // text matrix in viewport space: m[4], m[5] = left, baseline from the top
        items.push({ x: m[4], y: m[5], w: it.width, s: it.str })
      }
      pages.push(items)
      if (items.length < SCAN_THRESHOLD) {
        // About 250 dpi: enough for the OCR engine, small enough to stay fast.
        const big = page.getViewport({ scale: Math.min(3.5, 2100 / vp.width) })
        const canvas = document.createElement('canvas')
        canvas.width = Math.round(big.width)
        canvas.height = Math.round(big.height)
        await page.render({ canvas, canvasContext: canvas.getContext('2d')!, viewport: big }).promise
        scans.push({ index: p - 1, canvas })
      }
    }
    if (scans.length > 0) {
      const { ocrPages } = await import('./ocr')
      const read = await ocrPages(scans.map(s => s.canvas), (i, done) => onProgress?.(`Đang nhận dạng chữ (OCR) trang ${scans[i].index + 1}/${doc.numPages}: ${Math.round(done * 100)}%`))
      scans.forEach((s, i) => { pages[s.index] = read[i] })
    }
    return { pages, ocrPages: scans.length }
  } finally { await task.destroy() }
}
