# 10 — Hướng dẫn sử dụng cho kế toán — v0.1

Tài liệu cho người làm kế toán hằng ngày. Cài đặt, sao lưu, phân quyền: xem [08-VAN-HANH.md](08-VAN-HANH.md).

## 1. Mở phần mềm và trang Tổng quan

Bấm đúp `Chay HAK.cmd`, chờ trình duyệt mở, đăng nhập và chọn công ty. Trang đầu tiên là **Tổng quan**:

- Các ô số: chứng từ nháp của tôi, chờ duyệt, chờ ghi sổ, phiếu kho nháp, dòng sao kê chưa khớp. Bấm vào ô để mở đúng danh sách đó.
- **Khóa sổ**: kỳ mở sớm nhất và nút mở danh sách kiểm tra khóa sổ.
- **Tiền**: số dư từng quỹ, từng tài khoản ngân hàng; bấm vào dòng để mở sổ quỹ / sổ tiền gửi.
- **Công nợ quá hạn**: tổng phải thu, phải trả quá hạn.

Menu bên trái chia nhóm: Tổng hợp, Tiền, Mua – Bán – Kho, Tài sản – Lương – Thuế, Báo cáo, Hệ thống.

## 2. Vòng đời một chứng từ

**Nháp → Chờ duyệt → Đã duyệt → Đã ghi sổ.** Người lập và người duyệt phải là hai người khác nhau. Chứng từ đã ghi sổ không sửa, không xóa được: làm sai thì **Đảo** (sinh chứng từ đảo) rồi lập lại, hoặc lập chứng từ **Điều chỉnh**.

**Đính kèm chứng từ gốc**: chọn một chứng từ trong danh sách, mục "Tệp đính kèm" bên dưới bảng định khoản cho thêm tệp (PDF, ảnh, Excel, Word, XML… tối đa 10 MB mỗi tệp, 20 tệp mỗi chứng từ), kể cả sau khi đã ghi sổ. "Xem" mở PDF / ảnh, "Tải về" lưu tệp. Tệp không bị xóa: "Gỡ" yêu cầu ghi lý do, tệp đã gỡ vẫn hiện trong danh sách và người có quyền xem nhật ký vẫn mở được. Tệp nằm trong cơ sở dữ liệu nên có mặt trong bản sao lưu.

Duyệt nhiều chứng từ: tick các dòng trong danh sách rồi bấm Gửi duyệt / Duyệt / Ghi sổ / Duyệt và ghi sổ. Phần mềm làm lần lượt theo ngày; chứng từ nào không qua kiểm tra được liệt kê, các chứng từ khác vẫn tiếp tục.

Nếu công ty đặt **hạn mức duyệt** (Thiết lập → Mẫu, mã phân tích, hạn mức duyệt), chứng từ vượt hạn mức chỉ Kế toán trưởng / Giám đốc duyệt được.

## 3. Nhập chứng từ nhanh

Màn hình **Chứng từ → Thêm chứng từ**:

| Phím | Tác dụng |
|------|----------|
| Enter | Sang ô kế tiếp. Ở ô cuối của dòng cuối: tự thêm dòng mới, điền sẵn bên Nợ/Có còn thiếu, số tiền còn lệch và diễn giải của dòng trên |
| Insert | Thêm dòng |
| Ctrl+S | Lưu nháp |
| Ctrl+Enter | Lưu và gửi duyệt |
| Esc | Đóng (nội dung đang nhập vẫn được giữ) |

- **Ô tài khoản, đối tượng, mã phân tích**: gõ mã hoặc một phần tên, không cần dấu (`phai thu`, `131`, `dai duong`), dùng ↑ ↓ rồi Enter. Xóa trắng ô để bỏ chọn.
- **Thêm đối tượng mới ngay trong form**: gõ mã / tên chưa có, chọn dòng "+ Thêm mới…", điền mã, tên, mã số thuế.
- **Định khoản mẫu**: nhập xong các dòng, bấm "Lưu làm mẫu…" và đặt tên. Lần sau chọn mẫu ở ô "Định khoản mẫu", sửa số tiền, lưu. Quản lý / xóa mẫu ở Thiết lập.
- **Sao chép**: chọn một chứng từ trong danh sách, bấm "Sao chép" để lập chứng từ mới cùng định khoản với ngày hôm nay.
- **Cảnh báo ngay khi gõ** (dưới bảng): tài khoản bắt buộc đối tượng mà chưa chọn, kỳ đã khóa sổ, số tiền sai. Sau khi lưu, phần mềm kiểm tra đầy đủ (quỹ âm, công nợ…) và báo kết quả.
- **Đang nhập dở mà đóng trình duyệt / mất điện**: mở lại "Thêm chứng từ", phần mềm khôi phục nội dung và báo giờ đã lưu tạm. Bấm "Bỏ, nhập lại từ đầu" nếu không cần.
- **Tìm chứng từ**: ô Tìm không phân biệt hoa / thường và dấu.

## 4. Thu, chi, ngân hàng

- **Thu / Chi tiền**: lập phiếu thu, phiếu chi, báo Có, báo Nợ theo nghiệp vụ; chọn hóa đơn được thanh toán ngay trên phiếu. In phiếu thu / phiếu chi bằng nút "In chứng từ" ở màn hình Chứng từ.
- **Ngân hàng → Lệnh thanh toán**: lập ủy nhiệm chi → duyệt → ghi nhận gửi ngân hàng → ngân hàng thực hiện (sinh báo Nợ nháp). Nút "In UNC" in ủy nhiệm chi để ký.
- **Ngân hàng → Sao kê & đối chiếu → Nhập sao kê**: bấm "Chọn tệp" và chọn tệp sao kê / sổ phụ của ngân hàng. Phần mềm tự nhận cột ngày, số tham chiếu, Nợ, Có, nội dung, điền kỳ sao kê và số dư đầu / cuối vào các ô bên dưới; **xem lại rồi mới bấm "Nhập & tự khớp"**. Các dạng tệp đọc được:
  - Excel `.xls` / `.xlsx`, `.csv` tải từ ngân hàng trực tuyến: chính xác nhất, nên ưu tiên.
  - PDF do ngân hàng xuất (có chữ chọn được): phần mềm dựng lại bảng theo vị trí chữ. Diễn giải dài nhiều dòng có thể ghép chưa chuẩn; số tiền được kiểm bằng số dư.
  - Ảnh chụp, bản quét `.jpg` / `.png`, PDF quét: phần mềm **nhận dạng chữ (OCR)** ngay trên máy, không gửi đi đâu, mất khoảng vài giây đến nửa phút mỗi trang. OCR có thể đọc sai ký tự (nhất là dấu tiếng Việt, đôi khi cả chữ số), nên phải so từng dòng với sổ phụ gốc. Ảnh nên chụp thẳng, đủ sáng, rõ nét.
  - Dấu hiệu đọc đúng số tiền: số dư đầu + phát sinh = số dư cuối. Nếu phần mềm báo lệch, sửa dòng sai ngay trong ô văn bản trước khi nhập.
- Dòng sao kê không có trên sổ (phí, lãi): bấm tạo chứng từ từ dòng đó, phần mềm gợi ý tài khoản.

## 5. Hóa đơn điện tử và thuế GTGT

**Thuế GTGT → Nhập hóa đơn điện tử**: chọn các tệp `invoice.xml` hoặc tệp `.zip` tải từ cổng hóa đơn điện tử của cơ quan thuế. Phần mềm đọc người bán, ký hiệu, số, ngày, tiền hàng, thuế theo từng thuế suất; hóa đơn có người bán là công ty mình được coi là đầu ra, còn lại là đầu vào. Bấm "Nhập", rồi sang tab đối chiếu để so với hóa đơn đã ghi sổ.

Khai mã số thuế công ty trước khi nhập (tab Hồ sơ), nếu không phần mềm không phân biệt được đầu vào / đầu ra.

## 6. Công nợ

- **Công nợ**: chọn tài khoản và ngày để xem tuổi nợ. Bấm một dòng đối tượng để xem chứng từ còn mở.
- **Khớp tay**: tick một dòng Nợ và một dòng Có rồi "Phân bổ…". **Tự động bù trừ (FIFO)** khớp theo thứ tự hạn thanh toán.
- **Lịch sử phân bổ**: xem và hủy phân bổ (bắt buộc ghi lý do).
- **Biên bản đối chiếu công nợ**: nút "Biên bản đối chiếu…", chọn từ ngày, in hoặc lưu PDF.

## 7. Báo cáo và truy ngược

- **Cân đối phát sinh**: bấm vào một tài khoản chi tiết để mở **Sổ chi tiết**; bấm vào một dòng sổ để mở **chứng từ**.
- **Sổ Nhật ký chung**, **Báo cáo theo mẫu** (báo cáo tài chính theo mẫu đã khai báo), **Phân tích theo mã**.
- Mọi báo cáo có nút **Xuất Excel** và **In**.

**Mã phân tích** (Thiết lập → Mẫu, mã phân tích, hạn mức duyệt): khai báo nhóm và mã (ví dụ nhóm "Chuyến tàu", mã `T2026-03`). Khi có mã, form chứng từ có thêm cột "Mã phân tích". Báo cáo "Phân tích theo mã" cộng phát sinh theo từng mã; cột "Có − Nợ" là lãi của mã đó khi cả dòng doanh thu và dòng chi phí đều gắn mã. Mã gắn được trên chứng từ nhập tay (Chứng từ khác, Điều chỉnh, Phân bổ…) và trên từng dòng phiếu thu / chi, báo Nợ / Có (mã đi vào dòng đối ứng, không vào tài khoản tiền). Chứng từ do phân hệ kho và hóa đơn sinh ra chưa mang mã phân tích.

## 8. Khóa sổ cuối tháng

**Kỳ kế toán → Kiểm tra khóa sổ** ở dòng của kỳ. Danh sách liệt kê theo thứ tự nên làm:

1. Ghi sổ hết chứng từ và phiếu kho trong kỳ.
2. Đối chiếu sao kê ngân hàng.
3. Tính giá xuất kho cuối tháng, tính giá thành.
4. Khấu hao TSCĐ / phân bổ CCDC, trích lãi vay, đánh giá lại ngoại tệ.
5. Kết chuyển doanh thu, chi phí.
6. Kiểm tra sổ chi tiết công nợ khớp sổ cái, bảng cân đối số phát sinh cân.
7. Khóa sổ.

Mỗi dòng có trạng thái: **Xong**, **Cần làm** (chặn việc khóa sổ sạch), **Nên xem** (không chặn), **Không áp dụng**, và nút mở màn hình tương ứng. Kỳ đã khóa không ghi sổ thêm được; mở lại kỳ phải ghi lý do và được ghi nhật ký.

## 9. Khi gặp lỗi

- "Không kết nối được máy chủ HAK": cửa sổ HAK đã tắt — chạy lại `Chay HAK.cmd`.
- Thông báo có mã lỗi viết hoa (ví dụ `PERIOD_LOCKED`, `FORBIDDEN`): đọc câu tiếng Việt phía sau; thường là kỳ đã khóa, thiếu quyền, hoặc người duyệt trùng người lập.
- Số liệu nghi sai: không sửa trực tiếp trong cơ sở dữ liệu. Dùng Đảo / Điều chỉnh, hoặc báo người quản trị.
