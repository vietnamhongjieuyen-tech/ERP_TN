import assert from 'node:assert/strict'
import { test } from 'node:test'
import { foldText, searchOptions } from './text.ts'

const opts = [
  { id: '1', code: '111', name: 'Tiền mặt' },
  { id: '2', code: '1121', name: 'Tiền gửi ngân hàng' },
  { id: '3', code: '131', name: 'Phải thu của khách hàng' },
  { id: '4', code: '331', name: 'Phải trả cho người bán' },
  { id: '5', code: 'KH01', name: 'Công ty Đại Dương' },
]

test('foldText ignores case and Vietnamese accents', () => {
  assert.equal(foldText('  HÓA ĐƠN số 12/A '), 'hoa don so 12/a')
  assert.equal(foldText('Ưu đãi Ỷ lại'), 'uu dai y lai')
})

test('searchOptions ranks code prefix first, then name words, and needs every word', () => {
  assert.deepEqual(searchOptions(opts, '11').map(o => o.code), ['111', '1121'])
  assert.deepEqual(searchOptions(opts, '131').map(o => o.code), ['131'])
  assert.deepEqual(searchOptions(opts, 'phai').map(o => o.code), ['131', '331'])
  assert.deepEqual(searchOptions(opts, 'PHẢI tra').map(o => o.code), ['331'])
  assert.deepEqual(searchOptions(opts, 'dai duong').map(o => o.code), ['KH01'])
  assert.deepEqual(searchOptions(opts, 'ngan').map(o => o.code), ['1121'])
  assert.equal(searchOptions(opts, 'xyz').length, 0)
  assert.equal(searchOptions(opts, '', 2).length, 2)
})
