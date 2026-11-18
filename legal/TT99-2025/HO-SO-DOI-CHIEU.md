# Hồ sơ đối chiếu pháp lý TT 99/2025/TT-BTC trong HAK

Lập ngày 18/11/2026. Hồ sơ ghi những gì đã được đối chiếu, đối chiếu bằng cách nào, kết quả ra sao, và những gì còn chờ người có thẩm quyền ký xác minh.

Phần mềm không tự chứng nhận tuân thủ pháp luật. Kết quả tự kiểm tra và test tự động chỉ chứng minh:
- mẫu chép đúng văn bản gốc;
- mẫu nhất quán với danh mục tài khoản;
- số liệu tính đúng theo công thức đã khai báo.

Việc xác minh vẫn do kế toán trưởng thực hiện:
1. xem hồ sơ này và hai bảng kiểm;
2. bấm **Xác minh** cho chế độ kế toán và cho từng mẫu báo cáo trong phần mềm.

## 1. Văn bản căn cứ

| Văn bản | Hiệu lực | Nguồn đã dùng |
|---|---|---|
| Thông tư 99/2025/TT-BTC ngày 27/10/2025 — Chế độ kế toán doanh nghiệp | Năm tài chính bắt đầu từ 01/01/2026 (Điều 31). Thay TT 200/2014, 75/2015, 53/2016, 195/2012 | Bản PDF ký số và các tệp Công báo số 1563–1582 (mã SHA-256 trong `nguon-sha256.txt`). **Ngày 18/11/2026 tải lại 6 tệp Công báo (1563, 1565, 1575, 1577, 1579, 1581) từ congbao.chinhphu.vn: mã SHA-256 trùng khớp với bản lưu ngày 16/10/2026**, nên văn bản gốc chưa thay đổi |
| Nghị định 254/2026/NĐ-CP và Thông tư 91/2026/TT-BTC — hóa đơn điện tử, chứng từ điện tử | 01/07/2026 | Đối chiếu riêng: `../ND254-TT91-2026/README.md` |

Doanh nghiệp đã quyết định áp dụng TT99 từ 01/01/2026 và chuyển cả năm 2025 làm số so sánh (16/10/2026). Với doanh nghiệp nhỏ và vừa, đây là lựa chọn được phép thay cho chế độ dành riêng cho doanh nghiệp nhỏ và vừa.

## 2. Phạm vi và kết quả

| Hạng mục | Cách đối chiếu | Kết quả | Trạng thái |
|---|---|---|---|
| Danh mục tài khoản (Phụ lục II-A, 184 TK) | Mở bảng Công báo 1565+1566 bằng Microsoft Word (`tools/word-tables.ps1`), sinh lại bằng `tools/chart.py`, so từng byte với tệp đang dùng (18/11/2026). Đã so tay với PDF ký số trang 93–99 (16/10/2026) | **Giống hệt**: đúng số hiệu, tên, cấp, 71 TK cấp 1 có STT liên tục | Chờ KTT ký |
| Tên chỉ tiêu, Mã số các mẫu B01, B02, B03 (trực tiếp, gián tiếp), 8 mẫu B09 | Trích lại bảng mẫu Công báo 1577+1578; sinh lại bằng `tools/templates.py`, `tools/b09.py`; so từng byte với 12 tệp mẫu đang dùng | **Giống hệt cả 12 mẫu**: tên chỉ tiêu nguyên văn bảng mẫu; chương trình sinh mẫu tự kiểm mọi Mã số của bảng gốc đều có công thức | Chờ KTT ký |
| Công thức so với hướng dẫn lập (Công báo 1579–1582) | `tools/verify-formulas.py` tách từng đoạn "(Mã số X)" của hướng dẫn, lấy các TK hướng dẫn nêu, so với TK trong công thức; dòng lệch được đọc tay | 131 chỉ tiêu có tài khoản: 102 khớp ngay, 29 đọc tay đều khớp (hướng dẫn nói "đối ứng TK 911", trình bày thuần theo hướng dẫn, đoạn hướng dẫn nhắc chỉ tiêu khác). Kết luận từng dòng trong `doi-chieu-cong-thuc.csv`. **Phát hiện và đã sửa 2 điểm** (mục 3) | Chờ KTT ký |
| Tự kiểm tra mẫu với danh mục TK (`tools/audit.py`) | (a) mọi TK trong công thức có trong Phụ lục II-A; (b) mọi TK chi tiết loại 1–4 vào B01; (c) mọi TK chi tiết loại 5–8 vào B02 (trừ 621–627, 154 qua giá vốn); (d) Mã số không trùng, LINE() đúng | Không lỗi ở cả 12 mẫu. Đã chèn lỗi giả để thử, công cụ bắt đủ | Đạt |
| Tính đúng số liệu (test tự động) | `ImportTests.Tt99ChartOfAccounts_ImportsCleanly_WithItsHierarchy`, `ReportEngineTests.Tt99Statements_B01AndB02_ComputeFromTheChart_AndBalance`, `ReportEngineTests.Tt99CashFlow_IndirectMethod_ReconcilesWithDirectMethod`, `Tt99NotesTests.B09Templates_Validate_Run_AndTieOutToTheLedger`, `Tt99NotesTests.MultiColumnTemplates_RefuseWrongSlotCounts_AndRoundTrip` | 5/5 đạt. B01: 280 = 440. B03: gián tiếp = trực tiếp. B09 khớp sổ | Đạt |
| Chuyển dữ liệu chế độ cũ → TT99 (`tools/migrate-fast/map-tt200-tt99.csv`) | Mỗi TK đích có trong Phụ lục II-A, là TK chi tiết hoặc có TK chi tiết doanh nghiệp mở (Điều 11 khoản 2); chạy lại chuyển đổi 2025–2026 | 39 dòng hợp lệ; số dư đầu năm cân; hai dòng chờ đã quyết (mục 4) | Chờ KTT ký |
| Chứng từ kế toán (Phụ lục I) | Điều 9: mẫu Phụ lục I là **mẫu tham khảo**. Doanh nghiệp được tự thiết kế nhưng phải đủ nội dung theo Điều 16 Luật Kế toán và ban hành Quy chế hạch toán; nếu không tự thiết kế thì dùng mẫu Phụ lục I. Bố cục chép từ Công báo 1563+1564 | **Đã chuyển bản in sang mẫu Phụ lục I** (18/11/2026): Phiếu thu 01-TT, Phiếu chi 02-TT, Phiếu nhập kho 01-VT, Phiếu xuất kho 02-VT, Bảng kiểm kê quỹ 08a-TT. Ủy nhiệm chi in theo mẫu ngân hàng; biên bản đối chiếu công nợ là mẫu nội bộ (không có trong Phụ lục I) | Chờ KTT duyệt bản in |
| Sổ kế toán (Phụ lục III) | Điều 12: mẫu Phụ lục III là mẫu tham khảo (cùng nguyên tắc như chứng từ). Mẫu sổ trong Công báo 1575+1576 là ảnh, nên xuất PDF bằng Word, vẽ thành ảnh và xem từng trang | **Đã in theo mẫu** (18/11/2026): Sổ Nhật ký chung S03a-DN, Sổ Cái S03b-DN (theo tháng: cộng phát sinh, số dư cuối tháng, lũy kế từ đầu quý), Sổ quỹ tiền mặt S07-DN, Sổ chi tiết thanh toán với người mua / người bán S31-DN, Sổ chi tiết các tài khoản S38-DN (hai mẫu khổ ngang). Dữ liệu sổ có thêm ngày chứng từ. Các sổ khác (S08, S10–S12, S21–S23, S34–S37, S61…) chưa có bản in theo mẫu | Chờ KTT duyệt bản in |
| Bản in BCTC trên giấy (khổ A4, lặp tiêu đề, đánh số trang, chữ ký) | — | In từ trình duyệt | **Chưa đối chiếu** |

## 3. Sai lệch phát hiện khi đối chiếu và đã sửa (18/11/2026)

| Mẫu / Mã số | Hướng dẫn lập | Trước khi sửa | Sau khi sửa |
|---|---|---|---|
| B03-DN gián tiếp, Mã 09 và 11 | Phần D mục 4.1.2: Mã 09 gồm phải thu **và TK 331 trả trước cho người bán**; Mã 11 gồm phải trả **và TK 131 người mua trả tiền trước** | Lấy số thuần từng TK: tiền khách trả trước bị bù trừ vào phải thu, trả trước người bán bù vào phải trả. Tổng Mã 20 vẫn đúng, nhưng từng dòng 09 / 11 sai | 131 và 331 tách theo dư Nợ / dư Có của từng đối tượng (hàm `OPEN_GROSS_D/C`, `GROSS_D/C`). Test cập nhật: Mã 09 = −550, Mã 11 = 350, Mã 20 không đổi |
| B01-DN, Mã 420a / 420b trên **BCTC quý** | Phần B: 420a quý = dư Có 4211 **cộng dư Có 4212 lũy kế từ đầu năm đến đầu kỳ báo cáo** | 420a chỉ lấy 4211 (chỉ đúng với BCTC năm) | 420a = dư Có 4211 cuối kỳ + dư Có 4212 đầu kỳ báo cáo; 420b = phát sinh 4212 trong kỳ. BCTC năm không đổi (4212 đầu năm bằng 0 sau khi kết chuyển) |
| Bảng kiểm kê quỹ 08a-TT | "Chênh lệch (III = I – II)" — số theo sổ trừ số kiểm kê thực tế; lý do ghi "+ Thừa / + Thiếu" | Màn hình kiểm kê quỹ (17/11) tính thực tế − sổ, tiêu đề "Biên bản kiểm kê quỹ" | Theo đúng mẫu 08a-TT |

## 4. Quyết định ánh xạ đã chốt khi đối chiếu

| TK chế độ cũ | TK TT99 | Lý do |
|---|---|---|
| 338811 "Phải trả, phải nộp khác: HĐ SXKD" | **3411 "Các khoản đi vay"**, theo dõi theo đối tượng | Nội dung là tiền mượn của cá nhân: nhận tiền khi mượn, chi tiền khi trả, nên là khoản đi vay. TT99 không có 3388 cấp 3. Trình bày: B01 Mã 321 (phần trên 12 tháng gắn `DH` → Mã 339); B03 Mã 33 / 34 (hoạt động tài chính). Nếu thực chất là cổ tức / lợi nhuận phải trả thì dùng 332; phải trả khác thì dùng 3388 |
| 4111 "Vốn đầu tư của chủ sở hữu" | **4111 "Vốn góp của chủ sở hữu"**, hạch toán trực tiếp | Công ty TNHH ghi vốn thực góp vào 4111. 41111 / 41112 dành cho công ty cổ phần |

Số liệu ảnh hưởng ghi trong tệp riêng, không công bố: `out/tt99-doi-chieu-du-lieu-cong-ty.md`.

## 5. Kế toán trưởng làm gì để xác minh

1. Xem `doi-chieu-cong-thuc.csv` (131 chỉ tiêu, mở bằng Excel): kết luận của máy và của người đọc từng dòng. Ghi ý kiến nếu khác.
2. `ho-so-doi-chieu-chi-tieu.csv` (465 chỉ tiêu) là bảng ký: tên chỉ tiêu đã được chứng minh chép đúng nguyên văn, nên chủ yếu xem cột công thức / diễn giải, đánh Đ/S và ghi tên.
3. In thử phiếu thu, phiếu chi, phiếu nhập / xuất kho, bảng kiểm kê quỹ; so với mẫu Phụ lục I.
4. Trong phần mềm:
   - Quản trị → Chế độ kế toán → **Xác minh**, ghi họ tên và "đã đối chiếu theo hồ sơ ngày …";
   - Báo cáo theo mẫu → từng mẫu → **Xác minh**.
5. Hằng năm, hoặc khi có văn bản sửa đổi, chạy lại cả chuỗi:
   - `tools/word-tables.ps1` và `tools/word-text.ps1` trên tệp Công báo mới;
   - `tools/templates.py`, `tools/b09.py`, `tools/chart.py`;
   - `tools/audit.py` và `tools/verify-formulas.py`.

## 6. Còn lại

- Sổ theo Phụ lục III ngoài 5 sổ đã làm: sổ tiền gửi S08-DN, sổ / thẻ kho S10–S12, sổ TSCĐ S21–S23, sổ tiền vay S34, sổ bán hàng S35, sổ chi phí S36, thẻ giá thành S37, sổ thuế GTGT S61….
- Kiểm tra bản in BCTC trên giấy.
- Trường hợp một hóa đơn điều chỉnh cho nhiều hóa đơn kèm bảng kê (TT 91 Điều 10).
- Phân hệ chiết khấu thương mại.
- Đã kiểm tệp XML hóa đơn thật lập 17/11/2026 (cấu trúc PBan 2.1.0): đọc đúng (thêm test cùng cấu trúc với dữ liệu giả).
