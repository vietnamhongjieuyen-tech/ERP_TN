# Bản đồ nhập liệu và luồng dữ liệu

Nguyên tắc: **mỗi loại dữ liệu chỉ nhập ở một nơi**; các màn khác chỉ đọc hoặc chuyển tới nơi đó. Mọi số liệu vào sổ cái đều đi qua một chứng từ (`vouchers`) và chỉ được tính khi chứng từ **đã ghi sổ**.

## 1. Nhập ở đâu

| Dữ liệu | Nhập tại màn | Quyền cần | Sinh ra | Ghi chú |
|---|---|---|---|---|
| Hệ thống tài khoản, vai trò TK, kho, vật tư, năm tài chính | Thiết lập | config.manage / catalog.manage | danh mục | Nhập hàng loạt: Thiết lập → Nhập từ Excel/CSV |
| Đối tượng (KH, NCC, NV) | Danh mục đối tượng (thêm nhanh ngay trong ô chọn đối tượng) | catalog.manage | danh mục | |
| Số dư đầu kỳ TK / công nợ | Thiết lập → Nhập từ Excel/CSV → Số dư đầu kỳ | voucher.create | **một** chứng từ OPB nháp → duyệt → ghi sổ | Nhập lại đúng tệp đã nhập bị **chặn** (cùng TK, đối tượng, số tiền với một OPB chưa hủy / chưa đảo); tệp khác chỉ cảnh báo |
| Tồn kho đầu kỳ | Nhập từ Excel/CSV → Tồn kho đầu kỳ | catalog.manage + stock.document | phiếu nhập kho nháp mỗi kho (Nợ kho / Có TK trung gian) | OPB ghi Nợ TK trung gian thay TK hàng tồn → không tính trùng |
| TSCĐ / CCDC đầu kỳ | Nhập từ Excel/CSV → TSCĐ/CCDC | asset.manage | thẻ tài sản, nguyên giá lấy từ dòng OPB đã ghi sổ | |
| Thu / chi tiền mặt, ngân hàng | Thu / Chi tiền | cash.document / bank.document | chứng từ CR / CP / BR / BP theo **quy tắc định khoản** | Không nhập lại ở màn Chứng từ (màn đó không cho sửa CR/CP/BR/BP) |
| Chứng từ tổng hợp, kết chuyển, điều chỉnh | Chứng từ | voucher.create | GJ / CLS / ADJ | |
| Lệnh chuyển tiền (UNC) | Ngân hàng | bank.document; duyệt: payment_order.approve | bút toán chỉ khi ngân hàng đã thực hiện | |
| Sao kê ngân hàng | Ngân hàng (tệp Excel / CSV / PDF / ảnh) | bank.reconcile | dòng sao kê → khớp với bút toán có sẵn hoặc tạo chứng từ từ dòng | Chống trùng tệp và mã giao dịch |
| Đơn mua / bán | Đơn hàng | invoice.document | đơn (không vào sổ) | |
| Phiếu nhập / xuất / điều chuyển kho | Kho | stock.document | sổ kho + chứng từ IR / II khi ghi sổ | Đo độ khô ở màn Kho |
| Hóa đơn mua / bán, điều chỉnh, trả hàng | Mua / Bán (có nhập XML hóa đơn điện tử) | invoice.document | chứng từ PI / SI | Gắn dòng phiếu kho; chống trùng số hóa đơn |
| Tài sản, khấu hao | Tài sản / CCDC | asset.manage; chạy khấu hao: voucher.post | thẻ + chứng từ DEP / FAD khi chạy | |
| Khế ước vay, trích lãi | Vay | loan.manage; trích lãi: voucher.post | chứng từ LIA khi chạy | |
| Tỷ giá, đánh giá lại | Ngoại tệ | voucher.post | chứng từ FXR | |
| Bảng lương | Lương (tệp JSON từ hệ thống lương) | data.import + voucher.create | chứng từ lương | |
| Giá thành | Giá thành | catalog.manage (đối tượng); chạy: voucher.post | chứng từ giá thành | |

Các màn Ngoại tệ, Vay, Giá thành, Tài sản, Lương, Thuế GTGT, Công nợ và các báo cáo cần quyền **report.view** để xem; người không có quyền này không thấy các màn đó trong menu (máy chủ vẫn kiểm từng yêu cầu).

## 2. Dữ liệu chảy đi đâu

```
Danh mục ─┬─> Chứng từ (nháp → chờ duyệt → đã duyệt → ĐÃ GHI SỔ) ──> Bút toán (journal_entries, bất biến)
          │        ▲                                                   │
          │        │ phân hệ sinh chứng từ                              ├─> BCĐPS, Sổ cái, Nhật ký chung, sổ chi tiết, sổ quỹ
          │  Thu/Chi, Hóa đơn, Kho, Tài sản, Vay,                      ├─> Công nợ (phân bổ thanh toán, tuổi nợ, biên bản đối chiếu)
          │  Ngoại tệ, Lương, Giá thành                                ├─> Báo cáo theo mẫu (B01, B02, B03, B09 …)
          │                                                            └─> Thuế GTGT (bảng kê, đối chiếu sổ)
          └─> Sổ kho (stock_movements, bất biến) ──> Tính giá xuất kho ──> chứng từ IC
```

- Chứng từ **nháp / chờ duyệt / đã duyệt / đã hủy** không vào báo cáo nào.
- Đảo chứng từ sinh chứng từ REV mới; bút toán gốc không bị sửa.
- "Ghi sổ ngay" (chỉ ở công ty bật chế độ một người làm tất cả): gửi duyệt + duyệt + ghi sổ trong **một giao dịch** ở máy chủ; lỗi ở bước nào thì chứng từ vẫn là nháp.

## 3. Kiểm tra luồng dữ liệu

- Kỳ kế toán → **Kiểm soát toàn vẹn** (GET /api/integrity): bút toán lệch, tổng Nợ ≠ Có, chứng từ đã ghi sổ không có / có hai bút toán, bút toán không có chứng từ, TK công ty khác, phân bổ vượt, tồn kho âm, định giá trùng / chưa định giá. Chỉ đọc.
- Kỳ kế toán → **Danh sách kiểm tra khóa sổ**: chứng từ và phiếu kho chưa ghi sổ, sao kê chưa khớp, tính giá, giá thành, khấu hao, lãi vay, **bảng lương của kỳ**, **tạm ứng còn dư** (khi đã gán vai trò EMPLOYEE_ADVANCE), đánh giá ngoại tệ, kết chuyển, công nợ khớp sổ cái, BCĐPS cân, toàn vẹn, khóa kỳ.
