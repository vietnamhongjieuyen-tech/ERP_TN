import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { test } from 'node:test'
import { formatDmy, parseDmy } from './dates.ts'

test('dd/MM/yyyy in, ISO out; real calendar dates only', () => {
  assert.equal(parseDmy('05/03/2026'), '2026-03-05')
  assert.equal(parseDmy('5/3/2026'), '2026-03-05')
  assert.equal(parseDmy('05-03-2026'), '2026-03-05')
  assert.equal(parseDmy('05.03.2026'), '2026-03-05')
  assert.equal(parseDmy('05032026'), '2026-03-05')
  assert.equal(parseDmy('29/02/2028'), '2028-02-29')
  assert.equal(parseDmy('29/02/2026'), null)          // not a leap year
  assert.equal(parseDmy('31/04/2026'), null)
  assert.equal(parseDmy('03/13/2026'), null)          // month 13 (an MM/dd habit) is refused, not swapped
  assert.equal(parseDmy('5/3/26'), null)              // a year still being typed is not taken
  assert.equal(parseDmy('5/3/26', true), '2026-03-05') // on leaving the field, 26 → 2026
  assert.equal(parseDmy(''), null)
})

test('ISO → dd/MM/yyyy', () => {
  assert.equal(formatDmy('2026-03-05'), '05/03/2026')
  assert.equal(formatDmy(''), '')
  assert.equal(formatDmy(null), '')
})

test('no screen uses the browser-language date input directly (use DateInput)', () => {
  const dir = new URL('.', import.meta.url)
  const offenders = readdirSync(dir).filter(f => f.endsWith('.tsx') && f !== 'DateInput.tsx' && !f.includes('.test.'))
    .filter(f => /<input[^>]*type="date"/.test(readFileSync(new URL(f, dir), 'utf8')))
  assert.deepEqual(offenders, [])
})
