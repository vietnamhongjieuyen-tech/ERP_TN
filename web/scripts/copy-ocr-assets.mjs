// Copies the OCR engine (tesseract.js worker + WebAssembly core) and the Vietnamese / English language data from node_modules
// into public/ocr, so the application serves them itself and OCR works offline. Run before "dev" and "build".
import { copyFileSync, existsSync, mkdirSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const files = [
  ['node_modules/tesseract.js/dist/worker.min.js', 'public/ocr/worker.min.js'],
  ['node_modules/tesseract.js-core/tesseract-core-lstm.wasm.js', 'public/ocr/tesseract-core-lstm.wasm.js'],
  ['node_modules/tesseract.js-core/tesseract-core-simd-lstm.wasm.js', 'public/ocr/tesseract-core-simd-lstm.wasm.js'],
  ['node_modules/tesseract.js-core/tesseract-core-relaxedsimd-lstm.wasm.js', 'public/ocr/tesseract-core-relaxedsimd-lstm.wasm.js'],
  ['node_modules/@tesseract.js-data/vie/4.0.0_best_int/vie.traineddata.gz', 'public/ocr/lang/vie.traineddata.gz'],
  ['node_modules/@tesseract.js-data/eng/4.0.0_best_int/eng.traineddata.gz', 'public/ocr/lang/eng.traineddata.gz'],
]
let copied = 0
for (const [from, to] of files) {
  const src = join(root, from), dst = join(root, to)
  if (!existsSync(src)) { console.error(`Thiếu ${from} — chạy "npm ci" trước.`); process.exit(1) }
  if (existsSync(dst) && statSync(dst).size === statSync(src).size) continue
  mkdirSync(dirname(dst), { recursive: true })
  copyFileSync(src, dst)
  copied++
}
if (copied) console.log(`OCR: đã chép ${copied} tệp vào public/ocr.`)