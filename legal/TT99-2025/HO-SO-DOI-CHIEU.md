# Hồ sơ đối chiếu pháp lý TT 99/2025/TT-BTC trong HAK

Lập ngày 18/11/2026. Hồ sơ này ghi những gì đã được đối chiếu, bằng cách nào, kết quả ra sao, và những gì còn chờ người có thẩm quyền ký xác minh.

Phần mềm không tự chứng nhận tuân thủ pháp luật: kết quả tự kiểm tra và test tự động chỉ chứng minh mẫu nhất quán với danh mục tài khoản và số liệu tính đúng theo công thức đã khai báo. Việc xác minh vẫn do kế toán trưởng thực hiện. Cách làm: đối chiếu với văn bản gốc, ghi kết quả vào bảng kiểm, rồi bấm **Xác minh** cho chế độ kế toán và từng mẫu báo cáo trong phần mềm.

## 1. Văn bản căn cứ

| Văn bản | Hiệu lực | Nguồn đã dùng |
|---|---|---|
| Thông tư 99/2025/TT-BTC ngày 27/10/2025 — Chế độ kế toán doanh nghiệp | Năm tài chính bắt đầu từ 01/01/2026 (Điều 31). Thay thế TT 200/2014, 75/2015, 53/2016, 195/2012 | Bản PDF ký số 778 trang và các số Công báo 1563–1582; mã SHA-256 từng tệp ghi trong `nguon-sha256.txt` và `README.md` |

Doanh nghiệp đã quyết định áp dụng TT99 từ 01/01/2026, chuyển cả năm 2025 làm số so sánh (16/10/2026). Đối với doanh nghiệp nhỏ và vừa, TT99 là lựa chọn được phép thay cho chế độ dành cho doanh nghiệp nhỏ và vừa.

**Nghị định 254/2026/NĐ-CP và Thông tư 91/2026/TT-BTC** (hiệu lực ghi nhận 01/07/2026) được tài liệu yêu cầu nâng cấp nhắc tới, nhưng **chưa được đối chiếu**. HAK chưa có văn bản gốc của hai văn bản này. Cần đọc bản gốc trước khi lập BCTC kỳ có hiệu lực của chúng.

## 2. Phạm vi và kết quả

| Hạng mục | Cách đối chiếu | Kết quả | Trạng thái |
|---|---|---|---|
| Danh mục tài khoản (Phụ lục II-A, 184 TK) | Trích tự động từ Công báo 1565+1566; so thủ công với trang 93–99 bản PDF ký số (16/10/2026) | Khớp toàn bộ số hiệu và tên | Chờ KTT ký |
| Mẫu B01-DN (126 dòng), B02-DN (21), B03-DN trực tiếp (30) và gián tiếp (41) | Sinh từ bảng mẫu Công báo 1577+1578; công thức theo hướng dẫn lập Công báo 1579–1582; chương trình sinh mẫu tự kiểm mọi Mã số trong bảng gốc đều có công thức | Đủ Mã số | Chờ KTT ký |
| B09-DN: 8 mẫu số liệu và khung văn bản | Sinh từ mẫu B09 Công báo 1577+1578 | Đủ khoản mục trong phạm vi đã dựng | Chờ KTT ký |
| Tự kiểm tra mẫu với danh mục TK (`tools/audit.py`, 18/11/2026) | (a) mọi TK trong công thức có trong Phụ lục II-A; (b) mọi TK chi tiết loại 1–4 vào B01; (c) mọi TK chi tiết loại 5–8 vào B02 (trừ 621–627, 154 qua giá vốn); (d) Mã số không trùng, LINE() đúng | **Không có lỗi** ở cả 12 mẫu. Đã chèn lỗi giả để kiểm, công cụ bắt đủ | Đạt |
| Tính đúng số liệu (test tự động) | `ImportTests.Tt99ChartOfAccounts_ImportsCleanly_WithItsHierarchy`, `ReportEngineTests.Tt99Statements_B01AndB02_ComputeFromTheChart_AndBalance`, `ReportEngineTests.Tt99CashFlow_IndirectMethod_ReconcilesWithDirectMethod`, `Tt99NotesTests.B09Templates_Validate_Run_AndTieOutToTheLedger`, `Tt99NotesTests.MultiColumnTemplates_RefuseWrongSlotCounts_AndRoundTrip` | 5/5 đạt (18/11/2026). B01: 280 = 440; B03: gián tiếp = trực tiếp; B09 khớp sổ | Đạt |
| Chuyển dữ liệu từ chế độ cũ sang TT99 (`tools/migrate-fast/map-tt200-tt99.csv`) | Mỗi TK đích có trong Phụ lục II-A và là TK chi tiết (hoặc có TK chi tiết doanh nghiệp mở theo Điều 11 khoản 2); chạy lại chuyển đổi 2025–2026 | 39 dòng ánh xạ hợp lệ; số dư đầu năm cân; hai dòng chờ đã quyết (mục 3) | Đạt, chờ KTT ký |
| Chứng từ kế toán (Phụ lục I), sổ kế toán (Phụ lục III) | Phiếu thu, phiếu chi, phiếu kho, ủy nhiệm chi, biên bản trong HAK là **mẫu nội bộ**, chưa dựng theo Phụ lục I/III | — | **Chưa đối chiếu** |
| In PDF bản ký, phân trang, chữ ký | Có in từ trình duyệt; chưa kiểm khổ A4/A3, lặp tiêu đề, đánh số trang theo mẫu | — | **Chưa đối chiếu** |

## 3. Quyết định ánh xạ đã chốt khi đối chiếu (18/11/2026)

| TK chế độ cũ | TK TT99 | Lý do |
|---|---|---|
| 338811 "Phải trả, phải nộp khác: HĐ SXKD" | **3411 "Các khoản đi vay"**, theo dõi theo đối tượng | Nội dung phát sinh là tiền mượn của cá nhân: nhận tiền khi mượn, chi tiền khi trả. Đây là khoản đi vay. TT99 không có TK 3388 cấp 3. Trình bày: B01 Mã 321 (phần thời hạn trên 12 tháng gắn nhãn `DH` → Mã 339); B03 tiền nhận vào Mã 33, tiền trả vào Mã 34 (hoạt động tài chính), không còn ở Mã 06/07 (hoạt động kinh doanh). Nếu thực chất là cổ tức / lợi nhuận phải trả thì dùng 332; phải trả khác thì dùng 3388 |
| 4111 "Vốn đầu tư của chủ sở hữu" | **4111 "Vốn góp của chủ sở hữu"**, hạch toán trực tiếp | Công ty TNHH ghi vốn thực góp vào 4111. TK 41111 / 41112 dành cho công ty cổ phần |

Số liệu ảnh hưởng của từng quyết định ghi trong tệp riêng, không công bố: `out/tt99-doi-chieu-du-lieu-cong-ty.md`.

## 4. Việc kế toán trưởng cần làm để xác minh

1. Mở `ho-so-doi-chieu-chi-tieu.csv` (465 dòng, mở bằng Excel). Đối chiếu từng chỉ tiêu với bản gốc và ghi Đ/S vào hai cột: tên chỉ tiêu (với bảng mẫu Phụ lục IV), công thức (với hướng dẫn lập). Ký tên ở cột "Người đối chiếu".
2. Xem bảng ánh xạ tài khoản (mục 3 và `tools/migrate-fast/map-tt200-tt99.csv`), đồng ý hoặc sửa.
3. Trong phần mềm:
   - Quản trị → Chế độ kế toán → **Xác minh**, ghi họ tên và "đã đối chiếu theo hồ sơ ngày …".
   - Báo cáo theo mẫu → từng mẫu → **Xác minh**. Người xác minh phải khác người nhập mẫu, trừ khi công ty chọn chế độ "Một người làm tất cả".
4. Hằng năm, hoặc khi Bộ Tài chính sửa đổi văn bản:
   - chạy lại `python -I tools/audit.py .` trong thư mục này;
   - nếu có văn bản mới thì sinh lại mẫu từ bản gốc, tạo phiên bản mẫu mới có ngày hiệu lực, và xác minh lại phần thay đổi.

## 5. Còn lại

- Chứng từ (Phụ lục I) và sổ kế toán (Phụ lục III) theo mẫu của Thông tư.
- Kiểm tra bản in BCTC trên giấy.
- Đối chiếu NĐ 254/2026 và TT 91/2026 khi có văn bản gốc.
- Bộ chứng từ, sổ, BCTC của năm chuyển đổi 2025 nhập vào HAK chính thức, đối chiếu với BCTC đã nộp.
