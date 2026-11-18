# Nghị định 254/2026/NĐ-CP và Thông tư 91/2026/TT-BTC — hóa đơn điện tử, chứng từ điện tử

Đối chiếu ngày 18/11/2026 với các phần liên quan tới HAK. Đây là bản đọc của phần mềm, **chưa phải ý kiến tư vấn thuế**. Kế toán trưởng xác nhận trước khi áp dụng.

## Nguồn

Cả hai văn bản tải về ngày 18/11/2026 từ Cổng Thông tin điện tử Chính phủ (bản PDF ký số). Đây là bản quét, phần mềm đọc bằng nhận dạng chữ (OCR) nên có thể sai chính tả; khi trích dẫn, đối chiếu lại với bản PDF.

| Văn bản | Ban hành | Hiệu lực | Tệp | SHA-256 |
|---|---|---|---|---|
| Nghị định 254/2026/NĐ-CP — quy định chi tiết Luật Quản lý thuế 108/2025/QH15 về hóa đơn điện tử, chứng từ điện tử | 30/06/2026 | 01/07/2026 | `254-ndcp.signed.pdf`, 55 trang (chinhphu.vn, docid 218689) | `d8df03ef58aa26f2f3bf7014c830fe598c7939e2fc32643e436eca24f1fe85ae` |
| Thông tư 91/2026/TT-BTC — hướng dẫn Luật Quản lý thuế và NĐ 254/2026 về hóa đơn điện tử, chứng từ điện tử | 30/06/2026 | 01/07/2026 | `91-btc.signed.pdf`, 97 trang (vanban.chinhphu.vn, docid 219006) | `3986d7970bd3658ce66523d8f8fcfca22c34abffb807e733b459e4729e06aab6` |

**Văn bản hết hiệu lực từ 01/07/2026:**
- NĐ 254 Điều 43: thay NĐ 123/2020, Điều 1 NĐ 41/2022 và NĐ 70/2025.
- TT 91 Điều 25: thay TT 32/2025.
- Phụ lục I–IV của TT 91 là bắt buộc, Phụ lục V để tham khảo.

**Hai văn bản này không thay thế TT 99/2025.** TT 99 là chế độ kế toán; NĐ 254 / TT 91 là hóa đơn, chứng từ theo pháp luật thuế. Theo TT 99 Điều 9 khoản 3, chứng từ thuộc pháp luật khác (như hóa đơn) thì theo pháp luật đó.

## Điểm liên quan tới HAK và việc đã làm

| Quy định | Nội dung | HAK | Trạng thái |
|---|---|---|---|
| TT 91 Điều 3; NĐ 254 Điều 4 | Hóa đơn điện tử là tệp XML gồm phần dữ liệu nghiệp vụ (do Cục Thuế công bố) và phần chữ ký số | Nhập hóa đơn từ tệp `invoice.xml` hoặc `.zip` tải từ cổng thuế, đọc theo cấu trúc DLHDon / TTChung / NDHDon | **Đã kiểm với hóa đơn thật lập 17/11/2026** (PBan 2.1.0, có TTKhac, thuế suất KKKNT, khối chữ ký người bán + Cục Thuế): đọc đúng ký hiệu, số, ngày, tiền, mã cơ quan thuế. Test einvoicexml.test.ts dựng cùng cấu trúc với dữ liệu giả |
| TT 91 Điều 4 và Phụ lục I | Ký hiệu mẫu là 1 chữ số **1–9**: 1 GTGT; 2 bán hàng; 3 tài sản công; 4 dự trữ quốc gia; 5 tem, vé, thẻ…; **6 phiếu xuất kho kiêm vận chuyển nội bộ / gửi bán đại lý**; **7 hóa đơn thương mại**; **8, 9 hóa đơn tích hợp biên lai thu thuế, phí**. Ký hiệu 6 ký tự: C/K + 2 số năm + loại (T, D, L, M, N, B, G, H, X, F) + 2 ký tự tự đặt | Đọc mọi ký hiệu mẫu (không giới hạn 1–6). **Mẫu 6 bị từ chối khi nhập vào bảng kê thuế GTGT**, kèm lý do, vì không phải hóa đơn mua bán | **Đã sửa 18/11/2026** (`web/src/einvoicexml.ts`, test `einvoicexml.test.ts`) |
| NĐ 254 Điều 10 khoản 1 điểm d | Người mua ghi mã số thuế, **hoặc** mã đơn vị quan hệ ngân sách, **hoặc** số định danh cá nhân | MST người mua không bắt buộc khi đọc XML | Phù hợp |
| TT 91 Điều 10 khoản 1 | Hóa đơn sai: sai nội dung không ảnh hưởng tiền / thuế → thông báo (Mẫu 04/SS-HĐĐT), không lập lại; sai MST, tiền, thuế suất, thuế, hàng hóa → **điều chỉnh** ("Điều chỉnh cho hóa đơn Mẫu số… ký hiệu… số… ngày…") hoặc **thay thế** ("Thay thế cho hóa đơn…"). Được lập 1 hóa đơn điều chỉnh / thay thế cho nhiều hóa đơn sai của cùng người mua trong tháng, kèm bảng kê 01/BK-ĐCTT | Hóa đơn điều chỉnh / thay thế ghi theo hóa đơn gốc; đọc thông tin hóa đơn liên quan trong XML (KHMSHDCLQuan + KHHDCLQuan, số) | Phù hợp với trường hợp **một hóa đơn gốc**. Trường hợp một hóa đơn điều chỉnh cho nhiều hóa đơn kèm bảng kê: phải ghi tay từng hóa đơn gốc |
| TT 91 Điều 10 khoản 5 điểm b | Chiết khấu thương mại theo số lượng / doanh số: trừ trên hóa đơn lần mua cuối hoặc kỳ sau, hoặc lập hóa đơn điều chỉnh kèm bảng kê | HAK chưa có phân hệ chiết khấu thương mại riêng; ghi bằng hóa đơn giảm giá / điều chỉnh | Chưa có phân hệ |
| TT 91 Điều 10 khoản 5 điểm c | Trả lại hàng: **người bán lập hóa đơn điều chỉnh**, trừ khi hai bên thỏa thuận người mua lập hóa đơn trả lại | Hàng mua trả lại / hàng bán bị trả lại ghi theo hóa đơn điều chỉnh của người bán | Phù hợp |
| NĐ 254 Điều 5 | Hóa đơn, chứng từ điện tử lưu **toàn vẹn, không bị thay đổi**, đủ thời hạn theo luật kế toán, in hoặc tra cứu được khi cần | HAK lưu số liệu đọc từ XML, **không lưu tệp XML gốc** | **Khuyến nghị:** đính kèm tệp XML gốc vào chứng từ ghi sổ hóa đơn (Tệp đính kèm nhận `.xml`, lưu SHA-256, không xóa được) và giữ bản gốc ở cổng thuế / nhà cung cấp hóa đơn |
| NĐ 254 Chương III; TT 91 Điều 17–20 | Chứng từ khấu trừ thuế TNCN, biên lai điện tử | HAK không lập chứng từ khấu trừ, chỉ nhập lương | Ngoài phạm vi |
| TT 91 Điều 24 | Hóa đơn lập theo NĐ 123/2020, NĐ 70/2025, TT 32/2025 mà sai thì điều chỉnh / thay thế theo quy định mới | Không ảnh hưởng dữ liệu đã ghi | — |

HAK không còn dẫn chiếu NĐ 123/2020, TT 78/2021 hay TT 32/2025 ở đâu (đã kiểm toàn bộ mã nguồn, 18/11/2026).
