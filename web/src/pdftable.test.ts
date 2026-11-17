import assert from 'node:assert/strict'
import { test } from 'node:test'
import { parseStatementRows } from './bankfile.ts'
import { adviceDetails, columnRole, statementRowsFromPdf, textLines, type Page, type TextItem } from './pdftable.ts'
import { phrases } from './ocr.ts'

test('phrases joins OCR words into cells: close words together, a column gap apart, coordinates scaled', () => {
  const w = (text: string, x0: number, x1: number) => ({ text, x0, y0: 100, x1, y1: 130 })
  const items = phrases([{ y1: 130, height: 30, words: [w('giao', 110, 170), w('Ngày', 40, 100), w('dịch', 180, 240), w('5,100,000.00', 600, 800), w(' ', 300, 310)] }], 0.5)
  assert.deepEqual(items, [{ x: 20, y: 65, w: 100, s: 'Ngày giao dịch' }, { x: 300, y: 65, w: 100, s: '5,100,000.00' }])
})

// Items as pdf.js gives them (left, top, width, text). All names and figures are made up.
const row = (y: number, ...cells: [number, string, number?][]): TextItem[] => cells.map(([x, s, w]) => ({ x, y, w: w ?? s.length * 5, s }))

test('textLines groups items by vertical position and orders them left to right', () => {
  const lines = textLines([...row(100, [200, 'b'], [10, 'a']), ...row(102, [300, 'c']), ...row(120, [10, 'd'])])
  assert.deepEqual(lines.map(l => l.map(i => i.s).join('')), ['abc', 'd'])
})

// Layout 1: wrapped text starts on the transaction line and continues below it; debit shown as a negative number, no balances.
const topAligned: TextItem[] = [
  ...row(60, [140, 'SAO KÊ TÀI KHOẢN']),
  ...row(80, [125, '(Từ ngày/'], [165, 'From Date:'], [233, '01/09/2026'], [294, 'đến ngày/'], [333, 'to date:'], [430, '30/09/2026)']),
  ...row(200, [22, 'Ngày bút toán'], [106, 'Ngày giao dịch'], [203, 'Mã giao dịch'], [320, 'Nội dung'], [432, 'Ghi nợ'], [525, 'Ghi có']),
  ...row(212, [22, 'Date processed'], [102, 'Transaction Date'], [199, 'Transaction No'], [325, 'Details'], [435, 'Debit'], [526, 'Credit']),
  ...row(230, [29, '04/09/2026'], [114, '04/09/2026'], [183, 'FT0001'], [282, 'Khach A tra tien hang'], [482, '0'], [543, '6,800,000']),
  ...row(239, [282, 'theo hoa don 12']),
  ...row(257, [29, '07/09/2026'], [114, '05/09/2026'], [183, 'FT0002'], [282, 'Tt tien cong. Nguoi chuyen:'], [447, '-1,800,000'], [575, '0']),
  ...row(266, [282, 'CONG TY TNHH THU']),
  ...row(278, [282, 'NGHIEM']),
  ...row(296, [29, '30/09/2026'], [114, '30/09/2026'], [183, '900011112222-'], [282, 'Tra lai tien gui'], [482, '0'], [554, '96,000']),
  ...row(308, [183, '20260930']),
  ...row(370, [61, 'KIỂM SOÁT'], [130, '/ AUTHORISER']),
  ...row(450, [319, 'Ký ngày: 30/10/2026 14:50:13']),
  ...row(540, [30, 'Created date/Ngày tạo'], [116, '30/10/2026 02:50'], [505, 'Trang 1 / 1']),
]

test('statementRowsFromPdf: text wrapped below the transaction line, negative debits, footer dates ignored', () => {
  const st = parseStatementRows(statementRowsFromPdf([topAligned]))
  assert.deepEqual([st.fromDate, st.toDate, st.opening, st.closing], ['2026-09-01', '2026-09-30', null, null])
  assert.deepEqual(st.lines, [
    { transactionDate: '2026-09-04', amount: '6800000', bankReference: 'FT0001', description: 'Khach A tra tien hang theo hoa don 12' },
    { transactionDate: '2026-09-05', amount: '-1800000', bankReference: 'FT0002', description: 'Tt tien cong. Nguoi chuyen: CONG TY TNHH THU NGHIEM' },
    { transactionDate: '2026-09-30', amount: '96000', bankReference: '900011112222- 20260930', description: 'Tra lai tien gui' },
  ])
  assert.deepEqual(st.warnings, ['Tệp không ghi số dư đầu / cuối kỳ — nhập tay theo sao kê.'])
})

// Layout 2: wrapped text is centred on the transaction line (lines above and below it); balances and a totals line are printed.
const centred: TextItem[] = [
  ...row(40, [298, 'SỔ PHỤ KIÊM PHIẾU BÁO NỢ/CÓ']),
  ...row(76, [292, '(Từ ngày/From 01/09/2026 đến ngày/To 30/09/2026)']),
  ...row(235, [17, 'Ngày giao dịch'], [106, 'Đối tác'], [184, 'NH đối tác'], [288, 'Diễn giải'], [389, 'Số bút toán'], [492, 'Nợ'], [574, 'Có'], [641, 'Phí/Lãi'], [703, 'Thuế'], [774, 'Số dư']),
  ...row(247, [14, 'Transaction date'], [103, 'Remitter'], [177, 'Remitter bank'], [292, 'Details'], [381, 'Transaction No.'], [488, 'Debit'], [567, 'Credit'], [632, 'Fee/Interest'], [706, 'Tax'], [771, 'Balance']),
  ...row(265, [134, 'Số dư đầu kỳ/ Open balance'], [568, '10,000,000.00', 62]),
  ...row(280, [164, 'NGOAI THUONG VN']),
  ...row(286, [25, '05/09/2026'], [78, 'NGUYEN VAN A'], [249, 'Tra luong thang 8'], [379, 'FT1111'], [495, '5,100,000.00', 50], [774, '4,900,000.00', 55]),
  ...row(289, [164, '(VCB)']),
  ...row(334, [78, 'BAO HIEM XA HOI'], [164, 'CONG THUONG VN'], [249, 'Nop tien BHXH,BHYT']),
  ...row(340, [25, '18/09/2026'], [379, 'FT2222'], [495, '900,000.00', 44], [774, '4,000,000.00', 55]),
  ...row(343, [78, 'QUAN X'], [164, '(VIETINBANK)'], [249, 'ma don vi 123']),
  ...row(355, [249, 'Tra lai so du tren tai khoan -']),
  ...row(361, [25, '30/09/2026'], [383, '998877-20260930'], [588, '32,000.00', 40], [774, '4,032,000.00', 55]),
  ...row(364, [249, 'thang 09/2026']),
  ...row(376, [491, '6,000,000.00', 55], [588, '32,000.00', 40]),
  ...row(379, [134, 'Cộng doanh số / Total Volume']),
  ...row(391, [568, '4,032,000.00', 62]),
  ...row(394, [134, 'Số dư cuối kì / Ending balance']),
  ...row(565, [248, 'Ngày giờ in/Printed Time:01/10/2026']),
]

test('statementRowsFromPdf: text centred on the transaction line, balances reconcile', () => {
  const st = parseStatementRows(statementRowsFromPdf([centred]))
  assert.deepEqual([st.fromDate, st.toDate, st.opening, st.closing], ['2026-09-01', '2026-09-30', '10000000', '4032000'])
  assert.deepEqual(st.lines, [
    { transactionDate: '2026-09-05', amount: '-5100000', bankReference: 'FT1111', description: 'Tra luong thang 8' },
    { transactionDate: '2026-09-18', amount: '-900000', bankReference: 'FT2222', description: 'Nop tien BHXH,BHYT ma don vi 123' },
    { transactionDate: '2026-09-30', amount: '32000', bankReference: '998877-20260930', description: 'Tra lai so du tren tai khoan - thang 09/2026' },
  ])
  assert.deepEqual(st.warnings, [])
})

test('statementRowsFromPdf: a table continued on the next page keeps its columns; a page without a table is only text', () => {
  const page2 = row(60, [25, '30/09/2026'], [383, 'FT3333'], [588, '8,000.00', 40], [774, '4,040,000.00', 55])
  const st = parseStatementRows(statementRowsFromPdf([centred.filter(i => i.y < 370), [...page2, ...row(90, [134, 'Số dư cuối kì / Ending balance'], [568, '4,040,000.00', 62])]]))
  assert.equal(st.lines.length, 4)
  assert.deepEqual([st.closing, st.warnings], ['4040000', []])
  assert.match(parseStatementRows(statementRowsFromPdf([row(50, [10, 'Thư ngỏ của ngân hàng'])])).warnings.join(' '), /Không nhận ra dòng tiêu đề/)
})

// Layout 3: a daily "detailed account book" without a description column, followed by one advice page per transaction.
test('statementRowsFromPdf + adviceDetails: account book with advices on the following pages', () => {
  const book: TextItem[] = [
    ...row(40, [417, 'SỔ HẠCH TOÁN CHI TIẾT']),
    ...row(230, [24, 'Số dư đầu ngày'], [87, '/ Opening Balance'], [156, ':'], [516, '1.000.000,00']),
    ...row(263, [41, 'Ngày GD/'], [110, 'CN.PH/'], [168, 'Người lập/SCT/'], [277, 'Số Reference/'], [390, 'Doanh số nợ/'], [494, 'Doanh số có/']),
    ...row(275, [51, 'Date'], [111, 'Branch'], [170, 'Maker/Doc No.'], [291, 'Ref No.'], [390, 'Debit Amount'], [492, 'Credit Amount']),
    ...row(296, [39, '03/10/2026'], [179, '1234.5678'], [265, 'VND0000000001'], [424, '55.000,00']),
    ...row(310, [39, '03/10/2026'], [179, '1234.5679'], [265, 'VND0000000002'], [530, '5.000,50']),
    ...row(340, [24, 'Số món'], [56, '/ Number of Transaction'], [142, ': 2'], [198, 'Doanh số ngày'], [343, ':'], [424, '55.000,00'], [547, '5.000,50']),
    ...row(386, [24, 'Số dư cuối ngày'], [89, '/ Closing Balance'], [153, ':'], [516, '950.000,50']),
  ]
  const advice = (text: string): TextItem[] => [
    ...row(40, [386, 'GIẤY BÁO NỢ - DEBIT ADVICE']), ...row(83, [480, 'Ngày'], [501, '/ Date'], [525, ': 03/10/2026']),
    ...row(330, [32, 'Số tiền nợ'], [71, '/ Debit Amount'], [132, ': 55.000,00']),
    ...row(449, [24, 'Nội dung'], [60, '/ Details'], [132, ': ' + text]), ...row(509, [24, 'Ngày in'], [54, '/ Print Date'], [132, ': 06/11/2026']),
  ]
  const rows = statementRowsFromPdf([book, advice('THU PHI DICH VU'), advice('LAI TIEN GUI')])
  const st = parseStatementRows(rows)
  assert.deepEqual(st.lines.map(l => [l.transactionDate, l.amount, l.bankReference, l.description]),
    [['2026-10-03', '-55000', 'VND0000000001', ''], ['2026-10-03', '5000.5', 'VND0000000002', '']])
  assert.deepEqual([st.opening, st.closing, st.warnings], ['1000000', '950000.5', []])
  assert.deepEqual(adviceDetails(rows), ['THU PHI DICH VU', 'LAI TIEN GUI'])
})
// Layout 4 (a photographed statement read by OCR): the header captions are spread over three lines and the date caption is
// lost, the date sits on its own line above the amounts, the document number under it in the same cell, and the description
// wraps around the amounts. Horizontal rules separate the rows.
test('statementRowsFromPdf: OCR layout with dates above the amounts and ruled rows', () => {
  const page: Page = Object.assign([
    ...row(80, [35, 'Số dư đầu kỳ / Opening Balance'], [229, '1.000.000 VND']),
    ...row(118, [97, 'Ngày'], [135, 'giao dịch/']),
    ...row(135, [50, 'STT'], [106, '8 kêu Nên'], [210, 'Sốtiềnghinợ'], [319, 'Số tiền ghi có'], [456, 'số dư'], [627, 'Nội dung chi tiết']),
    ...row(151, [53, 'No'], [104, 'Số CT/ Doc No'], [235, 'Debit'], [342, 'Credit'], [452, 'Balance'], [622, 'Transaction In Detail']),
    ...row(189, [114, '03/10/2026'], [541, 'THU PHI DICH VU']),
    ...row(199, [59, '1'], [258, '55.000'], [474, '945.000']),
    ...row(205, [114, '9915-2949'], [542, 'THANG 09/2026']),
    ...row(240, [114, '25/10/2026']),
    ...row(257, [58, '2'], [97, '9701 - 0578019'], [380, '971'], [474, '945.971'], [542, 'INTEREST PAYMENT']),
    ...row(300, [35, 'Tổng số / Total'], [239, '55.000'], [380, '971']),
    ...row(325, [35, 'Số dư cuối kỳ / Closing Balance'], [228, '945.971 VND']),
  ], { rowRules: [160, 225, 280] })
  const rows = statementRowsFromPdf([page])
  assert.deepEqual(rows.find(r => r.length > 2), ['STT', 'Ngày giao dịch', 'Ghi nợ', 'Ghi có', 'Số dư', 'Nội dung'])
  const st = parseStatementRows(rows)
  assert.deepEqual(st.lines, [
    { transactionDate: '2026-10-03', amount: '-55000', bankReference: '9915-2949', description: 'THU PHI DICH VU THANG 09/2026' },
    { transactionDate: '2026-10-25', amount: '971', bankReference: '9701 - 0578019', description: 'INTEREST PAYMENT' },
  ])
  assert.deepEqual([st.opening, st.closing, st.warnings], ['1000000', '945971', []])
})

test('columnRole reads Vietnamese and English captions, OCR-joined words, and keeps titles out', () => {
  const cases: [string, string | null][] = [['ngay giao dich txn date', 'txdate'], ['so but toan transaction no', 'ref'], ['no debit', 'debit'], ['co credit', 'credit'],
    ['sotienghino debit', 'debit'], ['so du balance', 'balance'], ['noi dung chi tiet transaction in detail', 'desc'], ['stt no', 'stt'], ['phi lai fee interest', null]]
  for (const [k, role] of cases) assert.equal(columnRole(k), role, k)
})