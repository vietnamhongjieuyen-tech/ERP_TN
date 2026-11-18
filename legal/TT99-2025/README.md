# Thông tư 99/2025/TT-BTC — Chế độ kế toán doanh nghiệp

**Hồ sơ đối chiếu (18/11/2026):** xem `HO-SO-DOI-CHIEU.md` — tự kiểm tra 12 mẫu với danh mục TK (`tools/audit.py`, không lỗi), 5 test TT99 đạt, bảng kiểm 465 chỉ tiêu `ho-so-doi-chieu-chi-tieu.csv` để KTT ghi kết quả.

**Trạng thái trong HAK:** đã trích Phụ lục II-A từ văn bản gốc và đối chiếu từng trang với bản PDF ký số. **CHƯA được xác minh** bởi người có thẩm quyền ký. Trong phần mềm, phiên bản chế độ luôn ở trạng thái "CHƯA xác minh" cho tới khi kế toán trưởng bấm *Xác minh* và ghi rõ người ký.

## Nguồn (người dùng cung cấp ngày 16/10/2026)

- **Văn bản:** Thông tư số 99/2025/TT-BTC ngày 27/10/2025 của Bộ Tài chính, hướng dẫn Chế độ kế toán doanh nghiệp.
- **Hiệu lực (Điều 31):** áp dụng cho năm tài chính bắt đầu từ hoặc sau ngày **01/01/2026**.
- **Văn bản thay thế:** Thông tư 200/2014/TT-BTC, 75/2015/TT-BTC, 53/2016/TT-BTC và 195/2012/TT-BTC. Riêng các nội dung về cổ phần hóa nêu tại Điều 31 khoản 2 tiếp tục áp dụng.

| Tệp | SHA-256 |
|---|---|
| Thông tư số 99-TT-BTC ngày 27-10-2025 về hướng dẫn_251030_111328.pdf (bản scan có chữ ký số, 778 trang) | `bfe83829c588fc2d614b30d94f27824dbc056e5552d8ff4596acadccf40cef19` |
| 2025_1563 + 1564_99-2025-TT-BTC.doc (Công báo, phần thân + Phụ lục I) | `c7b69fa50ba3fe9d41c5fde331bd9197fed88d468fed818185cf80a61f17d941` |
| 2025_1565 + 1566_99-2025-TT-BTC.doc (**Phụ lục II-A danh mục TK**, đầu II-B) | `a39b1af9a69e54100dadada996ac5a21faa7bc24cd511f801e7736b6095d1b11` |
| 2025_1567 + 1568 … 1573 + 1574 (Phụ lục II-B nội dung, phương pháp kế toán) | xem `nguon-sha256.txt` |
| 2025_1575 + 1576 (Phụ lục III sổ kế toán) | xem `nguon-sha256.txt` |
| 2025_1577 + 1578 (**Phụ lục IV mẫu BCTC** B01/B02/B03/B09) | `fa65e2e715d46f5887c852402f15ef824e4771d0ad84cc65df966056b533e60e` |
| 2025_1579 + 1580_99-2025-TT-BTC.doc (**hướng dẫn lập B01-DN, B02-DN** đến Mã số 60; nhận 16/10/2026) | `08bdea543135b930bcbda81b63deeff68c59d097b6600aa8cff71ec84b83b5b0` |
| 2025_1581 + 1582 (hướng dẫn lập B02 từ Mã số 70, B03-DN, …) | `9fd2867122d9f443d91fd301520ecd3e3914252c5790785fe992fd18cbbd2888` |

## Các tệp trong thư mục này

- **`phu-luc-II-A-danh-muc-tai-khoan.csv` / `.json`** — Phụ lục II-A, 184 tài khoản: 71 TK cấp 1 (STT 01–71 liên tục), các TK cấp 2 và cấp con (đến 215121); 154 TK chi tiết.
  - Trích tự động từng ô của bảng trong bản Word (Công báo 1565 + 1566).
  - Đã đối chiếu thủ công với trang 93–99 của bản PDF ký số: khớp hoàn toàn số hiệu và tên.
  - Cột *Loại tài khoản* lấy từ các dòng tiêu đề "LOẠI TÀI KHOẢN …" của bảng.
- **`nhap-he-thong-tk-de-xuat.csv`** — tệp nhập sẵn cho màn hình Thiết lập → Nhập từ Excel/CSV → Hệ thống tài khoản.
  - Số hiệu, tên và quan hệ cấp trên lấy đúng theo Phụ lục II-A.
  - Các cột *Theo dõi đối tượng / Công nợ / Loại tiền / Số dư âm / Trình bày số dư* là **đề xuất cấu hình quản lý của HAK**, không phải quy định của Thông tư. Kế toán trưởng sửa trước khi nhập. Đề xuất hiện tại:
    - 111: tiền mặt, chặn số dư âm.
    - 112: tiền gửi, cảnh báo số dư âm.
    - 113: tiền đang chuyển.
    - 131: theo dõi đối tượng, công nợ phải thu, trình bày lưỡng tính.
    - 331: theo dõi đối tượng, công nợ phải trả, trình bày lưỡng tính.
    - 141: theo dõi đối tượng, công nợ phải thu.
    - 334: theo dõi đối tượng.
  - Doanh nghiệp được mở thêm TK chi tiết (Điều 11 khoản 2), ví dụ 1121 theo từng ngân hàng hoặc 1311 theo nhóm khách. Khi mở chi tiết dưới 111/112/131/331…, TK cấp trên phải chuyển thành tổng hợp và thuộc tính chuyển xuống TK chi tiết. Doanh nghiệp tự ban hành Quy chế hạch toán cho các nội dung sửa đổi, bổ sung đó.

## Đề xuất ánh xạ vai trò tài khoản (cần kế toán trưởng duyệt — không tự áp dụng)

| Vai trò HAK | TK đề xuất | Ghi chú |
|---|---|---|
| CASH_DEFAULT | 111 | |
| AR_TRADE | 131 | |
| AP_TRADE | 3311 (chi tiết của 331 "Phải trả người bán") | Mở trước khi hạch toán: Thiết lập → Hệ thống TK → 331 → **+ TK con** (331 tự chuyển thành TK tổng hợp; TK con kế thừa theo dõi đối tượng, công nợ) |
| VAT_INPUT | 1331 | TSCĐ: 1332 hạch toán trên chứng từ |
| VAT_OUTPUT | 33311 | |
| SALES_REVENUE | 511 | |
| SALES_RETURNS, SALES_DISCOUNT | 521 | TT99 không chia TK cấp 2 cho 521; nếu cần, doanh nghiệp tự mở chi tiết |
| COGS | 632 | |
| GRNI (hàng về chưa có hóa đơn) | 3318 "Hàng về chưa có hóa đơn" (chi tiết của 331) | **Doanh nghiệp đã chọn 16/10/2026.** Thông tư không có TK riêng. Khi thêm TK con: bỏ "Kế thừa", chọn **Bắt buộc đối tượng = có, Công nợ theo chứng từ = Không** (phiếu nhập ghi Có 3318 theo nhà cung cấp; hóa đơn ghi Nợ 3318 cùng nhà cung cấp, Có 3311). Ghi vào quy chế hạch toán của doanh nghiệp |
| FX_REVALUATION | 413 | Cuối kỳ kết chuyển 413 sang 515 / 635 |
| FX_REALIZED_GAIN / LOSS | 515 / 635 | |
| PAYROLL_EXPENSE_GENERAL / ADMIN | 6421 | |
| PAYROLL_EXPENSE_SELLING | 6411 | |
| PAYROLL_EXPENSE_PRODUCTION | 622 (hoặc 6271 cho nhân viên phân xưởng) | |
| PAYROLL_PAYABLE | 334 | |
| PAYROLL_SI / HI / UI / UNION_PAYABLE | 3383 / 3384 / 3386 / 3382 | |
| PAYROLL_PIT_PAYABLE | 3335 | |
| PAYROLL_OTHER_DEDUCTION | 1388 hoặc 141 | Tùy nội dung khấu trừ |
| BANK_FEE | 6427 | 635 nếu là chi phí đi vay |
| BANK_INTEREST_INCOME | 515 | |

## Khai báo chế độ trong phần mềm (Quản trị → Chế độ kế toán)

- **Mã chế độ** `TT99`; **tên** "Chế độ kế toán doanh nghiệp (Thông tư 99/2025/TT-BTC)"; **mã phiên bản** `2025`; **hiệu lực từ** `2026-01-01`.
- **Căn cứ pháp lý** (nhập 4 dòng; mỗi dòng ghi Số văn bản `99/2025/TT-BTC`, Ngày ban hành `2025-10-27`):

  | Nhóm quy định | Điều / phụ lục |
  |---|---|
  | Hệ thống tài khoản | Điều 11, Phụ lục II |
  | Báo cáo tài chính | Điều 14–27, Phụ lục IV |
  | Chứng từ kế toán | Điều 8–10, Phụ lục I |
  | Sổ kế toán | Điều 12–13, Phụ lục III |

- **Xác minh:** chỉ bấm *Xác minh* sau khi người chịu trách nhiệm (kế toán trưởng) đã đối chiếu, và ghi họ tên vào nội dung xác minh.

## Mẫu Báo cáo tài chính (Phụ lục IV)

| Tệp | Mẫu | Nguồn |
|---|---|---|
| `mau-B01-DN.json` | Báo cáo tình hình tài chính, 126 dòng (đủ mọi Mã số của mẫu) | Bảng mẫu: Công báo 1577+1578. Công thức: Phần B mục 1.4, Công báo 1579+1580 |
| `mau-B02-DN.json` | Báo cáo kết quả hoạt động kinh doanh, 21 dòng | Bảng mẫu: 1577+1578. Công thức: Phần C mục 3, Công báo 1579+1580 và 1581+1582 |
| `mau-B03-DN.json` | Báo cáo lưu chuyển tiền tệ — **phương pháp trực tiếp**, 30 dòng | Bảng mẫu: 1577+1578 (bảng 16). Hướng dẫn: Phần D, Công báo 1581+1582 |
| `mau-B03-DN-gian-tiep.json` | Báo cáo lưu chuyển tiền tệ — **phương pháp gián tiếp**, 41 dòng | Bảng mẫu: 1577+1578 (bảng 19). Hướng dẫn: Phần D mục 4.1.2, Công báo 1581+1582 |

Các tệp được sinh bằng `tools/templates.py` từ các bảng xuất ra của bản Word (B01, B02, B03 trực tiếp và gián tiếp); các mẫu B09 sinh bằng `tools/b09.py` (xem mục Thuyết minh BCTC). `tools/chart.py` và `tools/suggest.py` sinh danh mục tài khoản. Cả ba tệp dùng để kiểm tra lại khi Bộ Tài chính sửa đổi văn bản.

**Nhập vào phần mềm:** Báo cáo theo mẫu → Mẫu báo cáo → Nhập mẫu từ tệp JSON → Lưu nháp. Kế toán trưởng đối chiếu từng dòng với hướng dẫn lập rồi bấm Xác minh.

**Cách lấy số liệu:**
- **Tên chỉ tiêu** lấy nguyên văn từ bảng mẫu. Chương trình sinh mẫu tự kiểm tra: mọi Mã số trong bảng gốc đều có công thức và ngược lại.
- **Mã số có chữ** được lưu dạng 411A, 411B, 420A, 420B.
- **Chỉ tiêu số dư (B01):** dùng số dư cuối kỳ của tài khoản chi tiết.
  - Tài khoản có theo dõi đối tượng (131, 331, 333, 334, 338…) lấy số dư **theo từng đối tượng**, đúng yêu cầu "số dư Nợ / Có chi tiết": dư Nợ 131 → Mã 131, dư Có 131 → Mã 312.
- **Chỉ tiêu phát sinh (B02):** dùng phát sinh thuần trong kỳ, **không tính chứng từ kết chuyển (CLS)**. Kết quả này tương đương "phát sinh Có 632 đối ứng Nợ 911"… như hướng dẫn mô tả.
  - Vì vậy kết chuyển cuối kỳ phải lập bằng chức năng **Kết chuyển**, tức chứng từ loại CLS. Nếu kết chuyển bằng chứng từ thường thì B02 sẽ bị bù trừ về 0.
- **Cột "Số đầu năm" (B01) / "Năm trước" (B02):** chạy báo cáo với kỳ so sánh.
- **Quy tắc kiểm tra B01:** Mã 280 = Mã 440. Nếu lệch, thường là chưa kết chuyển kết quả, hoặc còn tài khoản có số dư mà chưa chỉ tiêu nào lấy.
- **Chưa tự tính:**
  - Mã 70, 71 (lãi trên cổ phiếu, chỉ áp dụng cho công ty cổ phần).
  - Chỉ tiêu 420a/420b trên **BCTC quý**: theo hướng dẫn, mẫu này chỉ đúng cho BCTC năm.

### Nhãn tài khoản (cột "Nhãn BC")

Hướng dẫn lập tách nhiều chỉ tiêu theo **kỳ hạn còn lại** hoặc theo **nội dung chi tiết**, mà số hiệu tài khoản cấp 1/cấp 2 không thể hiện được. Doanh nghiệp mở tài khoản chi tiết cho các phần cần tách và gắn nhãn (Thiết lập → Hệ thống tài khoản → cột Nhãn BC). Không gắn nhãn thì toàn bộ số dư nằm ở chỉ tiêu ngắn hạn hoặc chỉ tiêu chính.

| Nhãn | Ý nghĩa | Chỉ tiêu bị ảnh hưởng |
|---|---|---|
| `DH` | Phần **dài hạn** (kỳ hạn còn lại > 12 tháng hoặc hơn một chu kỳ kinh doanh) | 131/211, 132/212, 133/214, 135/215, 136/216, 141 → 251/273, 151–153/236–238, 161/271, 123/265, 311/331, 312/332, 314/333, 316/334, 317/336, 319/337, 320/338, 321/339, 322/343 |
| `TDT` | Khoản **tương đương tiền** (1281, 1288 kỳ hạn gốc ≤ 3 tháng) | 112 (không ở 123) |
| `HCSD` | Tiền, tương đương tiền **bị hạn chế sử dụng** | Chuyển từ 111/112 sang 165 (ngắn hạn) hoặc 274 (dài hạn, kèm `DH`) |
| `HTM` | Chi tiết 2292 là dự phòng đầu tư **nắm giữ đến ngày đáo hạn** | 124 / 266 |
| `BCC` | Chi tiết 2281 / 2292 là hợp đồng **BCC** không đồng kiểm soát | 125 / 126 (ngắn hạn), phần dài hạn vào 263 / 264 |
| `SXDD` | Chi tiết 2294 dự phòng cho **chi phí SXKD dở dang dài hạn** | 251 (còn lại 2294 dài hạn → 273) |
| `NO` | Chi tiết 41112 là **cổ phiếu ưu đãi phân loại nợ phải trả** | 341 (không ở 411/411b) |
| `BDSDT` | Chi tiết 511 / 632 của **bán, thanh lý bất động sản đầu tư** | 21 (không ở 01 / 11) |
| `DGL` | Chi tiết 515 / 635 của **chênh lệch tỷ giá đánh giá lại cuối kỳ** | 22 / 23 trình bày thuần |
| `LV` | Chi tiết 635 là **chi phí đi vay**; chi tiết 335 là **lãi vay phải trả** | B02: 24. B03: 06, 14 (trả lãi → mã 04); 335 nhãn LV không ở 11 |
| `TLTS` | Chi tiết 711 / 811 của **thanh lý, nhượng bán TSCĐ** | B02: 31 / 32 trình bày thuần. B03: 05; tiền thu/chi đối ứng → mã 22 |
| `DT` | Khoản liên quan **hoạt động đầu tư, tài chính**: chi tiết 515 / 635 lãi tiền gửi, lãi cho vay, cổ tức, lãi lỗ bán khoản đầu tư; chi tiết 131/136/138/244 phải thu đầu tư (thanh lý TSCĐ, lãi cho vay…); 331/336/338/344 phải trả đầu tư, tài chính (nhà thầu XDCB, mua TSCĐ, cổ tức…); 242 tiền thuê đất trả một lần | B03 gián tiếp: 05, 09, 11, 12, 02. Tiền thu đối ứng 515 nhãn DT → mã 27. Phải thu/phải trả nhãn DT nên gắn thêm `CFxx` (vd `DT,CF21`) |
| `KH` | TK chi phí nhận **khấu hao** ngoài 6234, 6274, 6414, 6424 (vd chi tiết 632 khấu hao BĐSĐT) | B03 gián tiếp: 02 |
| `CFxx` | Mã luồng tiền B03 cố định cho TK đối ứng (vd `CF21`) | B03: mọi dòng tiền đối ứng TK này |

**Kiểm thử:** `ReportEngineTests.Tt99Statements_B01AndB02_ComputeFromTheChart_AndBalance` nhập hệ thống TK TT99, ghi 8 nghiệp vụ mẫu (góp vốn, bán chịu, khách trả trước, mua hàng, giá vốn, chi phí, tiền gửi ≤ 3 tháng, kết chuyển) và đối chiếu từng chỉ tiêu với số tính tay.

### Báo cáo lưu chuyển tiền tệ (B03-DN) — mã luồng tiền

Hướng dẫn (Phần D mục 3) yêu cầu sổ chi tiết tiền theo dõi từng khoản thu, chi theo 3 hoạt động. HAK xác định mã B03-DN cho mỗi dòng tiền (TK 111/112/113 có khai báo Loại tiền và khoản tương đương tiền nhãn `TDT`; không gồm nhãn `HCSD`) theo thứ tự ưu tiên:
1. **Mã chọn trên dòng chứng từ** (cột *Mã LCTT* trong form chứng từ, chỉ hiện với TK tiền).
2. **Chứng từ đánh giá lại tỷ giá (FXR):** dòng tiền luôn là mã 61.
3. **Nhãn `CFxx` của TK đối ứng.** Ví dụ mở 3318 "Phải trả nhà thầu XDCB", gắn nhãn `DT,CF21` → mọi khoản trả cho TK này vào Mã 21.
4. **Nhãn chung:** đối ứng TK nhãn `LV` khi chi → 04; nhãn `TLTS` / `BDSDT` → 22; 515 nhãn `DT` khi thu → 27.
5. **Mặc định theo TK đối ứng** (tiền tố dài nhất): thu 131/511/3331/121/337 → 01; chi 331/15x/62x/632/641/642/133/242/335/635 → 02; 334 → 03; 3334 → 05; 21x/241/1332 → 21; 128/171 → 23/24; 221/222/228 → 25/26; 411 → 31/32; 3411/343 → 33/34; 3412 → 35; 332/421 → 36; 413 → 61; còn lại → 06 (thu) / 07 (chi).

Chuyển tiền giữa các TK tiền (kể cả gửi tiền ≤ 3 tháng) không là luồng tiền. Chứng từ có dòng tiền mà có nhiều TK Nợ **và** nhiều TK Có thì không ghép được đối ứng: báo cáo báo lỗi và liệt kê số chứng từ — chọn Mã LCTT trên dòng tiền của chứng từ đó.

| Tệp | Phương pháp | Kiểm tra |
|---|---|---|
| `mau-B03-DN.json` | Trực tiếp: mỗi Mã 01–07, 21–27, 31–36, 61 = `CF(mã)` | 70 = số dư tiền và tương đương tiền cuối kỳ (Mã 110) |
| `mau-B03-DN-gian-tiep.json` | Gián tiếp (Phần D mục 4.1.2): Mã 01–17 điều chỉnh từ lợi nhuận; Mã 21–70 như phương pháp trực tiếp | 70 = Mã 110; **20 gián tiếp = 20 tính trực tiếp từ sổ tiền** |

**Cách tính phương pháp gián tiếp:**
- 01 = Mã 50 B02-DN (phát sinh thuần 511, 515, 711 trừ 521, 632, 635, 641, 642, 811, không tính kết chuyển).
- 02 = phát sinh Nợ TK chi phí khấu hao (6234, 6274, 6414, 6424, nhãn `KH`) + phân bổ 242 nhãn `DT`, **trừ bút toán đảo** (hàm `PS_D`). Khấu hao vào XDCB hoặc ghi giảm quỹ không qua các TK này nên tự loại trừ.
- 03 = chênh lệch số dư 229, 352. 04 = lỗ − lãi đánh giá lại (515/635 nhãn `DGL`). 05 = lỗ − lãi thanh lý TSCĐ (`TLTS`), đầu tư (`DT`), BĐSĐT (`BDSDT`). 06 = 635 nhãn `LV`. 07 = trích lập − hoàn nhập quỹ 356, 357.
- 09–13 = số dư đầu kỳ − cuối kỳ (cùng một công thức cho tài sản và nợ phải trả: tài sản giảm / nợ tăng ghi dương) của phải thu (131, 133, 136, 138, 141, 244), hàng tồn kho (15x), phải trả (331, 333 trừ 3334, 334, 335 trừ `LV`, 336, 337, 338, 344), 242, 121 — bỏ TK chi tiết nhãn `DT`, và **cộng lại phần đánh giá lại tỷ giá** trên các TK đó (hàm `FXR`, chứng từ FXR) vì đã loại ở 04.
- Tài khoản lưỡng tính lấy số dư thuần: 131 dư Có (người mua trả trước) nằm ở 09 thay vì 11, 331 dư Nợ nằm ở 11 thay vì 09 — tổng 20 không đổi.
- 14 = `CF(04)`, 15 = `CF(05)`, 16 / 17 = tiền thu / chi (hoặc chuyển 334, 338) của quỹ 353, 356, 357.
- Quy tắc 20 gián tiếp = trực tiếp phát hiện phân loại sai: khoản phải thu/phải trả đầu tư chưa gắn `DT`, dòng tiền chọn mã đầu tư nhưng đối ứng TK công nợ kinh doanh, khấu hao ghi vào TK chưa gắn `KH`, ghi tăng TSCĐ không qua 241/331 nhãn `DT`…

**Kiểm thử:** `ReportEngineTests.Tt99CashFlow_IndirectMethod_ReconcilesWithDirectMethod` — 25 nghiệp vụ (khấu hao có bút toán đảo, dự phòng, đánh giá lại tỷ giá, lãi vay dồn tích và trả, đi vay, XDCB trả chậm, lãi tiền gửi, thanh lý TSCĐ, nộp thuế, trích và chi quỹ): Mã 20 gián tiếp = trực tiếp = 420, Mã 70 = số dư tiền = 2.902.

### Thuyết minh BCTC (B09-DN)

B09 gồm phần văn bản và nhiều bảng số liệu có số cột khác nhau. HAK tách thành:
- `mau-B09-DN-thuyet-minh.md` — khung đề mục I–X của mẫu, ghi chú phần nào phần mềm tính sẵn, gợi ý lấy từ thiết lập (kỳ kế toán, phương pháp giá xuất kho theo kho, khấu hao…).
- 8 mẫu báo cáo số liệu (sinh bằng `tools/b09.py`; tên khoản mục chép từ mẫu B09 — Công báo 1577+1578):

| Tệp | Nội dung | Cột |
|---|---|---|
| `mau-B09-DN-V-so-du.json` | V.1–5, 7, 8, 14, 17, 18, 20–23, 26, 27b | Cuối năm \| Đầu năm |
| `mau-B09-DN-V-bien-dong.json` | V.13 BĐSĐT, V.16 vay và nợ thuê tài chính, V.25 dự phòng phải trả | Đầu năm \| Tăng \| Giảm \| Cuối năm |
| `mau-B09-DN-V19-thue.json` | V.19 thuế (thuần từng loại; âm = phải thu) | Đầu năm \| Số phải nộp \| Số đã thực nộp \| Cuối năm |
| `mau-B09-DN-V9-tscd-huu-hinh.json`, `-V10-tscd-vo-hinh`, `-V11-tscd-thue-tai-chinh` | Tăng, giảm TSCĐ | theo nhóm tài sản + Tổng cộng |
| `mau-B09-DN-V27-von-chu-so-huu.json` | V.27a biến động vốn chủ sở hữu (chạy cho năm nay, rồi năm trước) | theo khoản mục vốn + Cộng |
| `mau-B09-DN-VII-VIII.json` | VII.1–11, VIII.3–4 | một cột; chạy kèm kỳ so sánh = Năm trước |

**Mẫu nhiều cột** (Report Engine): mẫu khai báo `columns`; mỗi công thức có một phần cho mỗi cột, ngăn bởi `|` (bỏ trống = ô trống); `LINE()` lấy cùng cột; quy tắc kiểm tra áp dụng cho từng cột.

**Cách tính các bảng tăng, giảm TSCĐ:**
- Mua trong năm = phát sinh Nợ TK nguyên giá đối ứng 331/111/112/113/141/3388/341; XDCB hoàn thành = đối ứng 241.
- Thanh lý, nhượng bán = phát sinh Có nguyên giá / Nợ hao mòn trên **chứng từ ghi giảm TSCĐ (FAD)** của phân hệ TSCĐ (hàm `VT_C` / `VT_D`).
- Khấu hao trong năm = phát sinh Có TK hao mòn, trừ bút toán đảo (`PS_C`).
- "Tăng khác" / "Giảm khác" = phần còn lại, nên đầu năm + tăng − giảm luôn bằng cuối năm (có quy tắc kiểm tra).
- TT 99/2025 không còn TK cấp 2 theo nhóm TSCĐ: mở TK chi tiết 211/2141 (212/2142, 213/2143) theo nhóm và gắn nhãn nhóm (bảng dưới). Không gắn nhãn thì chỉ cột **Tổng cộng** có số.

**Nhãn dùng cho B09:**

| Nhãn | Gắn cho | Dòng / cột B09 |
|---|---|---|
| `NHA`, `MAY`, `PTVT`, `CLN`, `KHAC` | TK chi tiết 211/2141, 212/2142 theo nhóm: nhà cửa, máy móc, phương tiện vận tải, cây lâu năm, khác | V.9, V.11 (cột) |
| `QSDD`, `QTG`, `SHCN`, `PM`, `KHAC` | TK chi tiết 213/2143: quyền sử dụng đất, quyền tác giả, quyền sở hữu công nghiệp, phần mềm, khác | V.10 (cột) |
| `DV`, `XD`, `TCTG` | Chi tiết 511 (và 632 với `DV`): dịch vụ, dịch vụ xây dựng, trợ cấp trợ giá | VII.1, VII.3 |
| `CKTM`, `GGHB`, `HBTL` | Chi tiết 521: chiết khấu thương mại, giảm giá, hàng bán bị trả lại | VII.2 |
| `DPHTK` | Chi tiết 632: dự phòng giảm giá hàng tồn kho | VII.3 |
| `LTG`, `LBDT`, `CTLN`, `TG`, `TTC`, `CKTT` | Chi tiết 515/635: lãi tiền gửi – cho vay, lãi/lỗ bán khoản đầu tư, cổ tức – lợi nhuận được chia, chênh lệch tỷ giá đã thực hiện, lãi trả chậm – trả góp, chiết khấu thanh toán | VII.5, VII.6 (dùng cùng `DGL`, `LV`) |
| `DPDT` | Chi tiết 635: dự phòng giảm giá chứng khoán kinh doanh, tổn thất đầu tư | VII.6 |

Khoản thu nhập đầu tư (`LTG`, `LBDT`, `CTLN`) cũng phải gắn `DT` để B03 xếp vào hoạt động đầu tư (vd `DT,LTG`).

**Kiểm thử:** `Tt99NotesTests.B09Templates_Validate_Run_AndTieOutToTheLedger` — nạp cả 8 mẫu, ghi góp vốn, mua 2 TSCĐ (nhãn NHA, MAY), chạy khấu hao 12 tháng bằng phân hệ TSCĐ, thanh lý máy, bán chịu, nộp thuế, vay – trả nợ, kết chuyển; đối chiếu bảng TSCĐ theo nhóm, thuế, vay, vốn chủ sở hữu, số dư và VII với số tính tay; mọi quy tắc kiểm tra đạt.

