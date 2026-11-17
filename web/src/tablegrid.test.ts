import assert from 'node:assert/strict'
import { test } from 'node:test'
import { removeRules } from './tablegrid.ts'

test('removeRules erases solid and dotted ruling lines, keeps text strokes, reports the rule positions', () => {
  const w = 400, h = 200, g = new Uint8Array(w * h).fill(255)
  const ink = (x: number, y: number) => { g[y * w + x] = 0 }
  for (let x = 10; x < 390; x++) ink(x, 60)                         // solid horizontal rule
  for (let x = 10; x < 390; x++) if (x % 4 !== 3) ink(x, 140)       // dotted horizontal rule
  for (let y = 20; y < 180; y++) ink(200, y)                        // vertical rule
  for (let y = 90; y < 110; y++) for (let x = 50; x < 54; x++) ink(x, y)   // a letter stroke
  const r = removeRules(g, w, h)
  assert.deepEqual(r.rowRules.map(Math.round), [60, 140])
  assert.deepEqual(r.columnRules.map(Math.round), [200])
  assert.equal(r.gray[60 * w + 120], 255)
  assert.equal(r.gray[140 * w + 120], 255)
  assert.equal(r.gray[100 * w + 200], 255)
  assert.equal(r.gray[100 * w + 51], 0)                              // the stroke is untouched
})