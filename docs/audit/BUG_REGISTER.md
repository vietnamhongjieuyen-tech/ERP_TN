# Sổ lỗi (bug register)

Mỗi lỗi ghi: mã, cách tái hiện, nguyên nhân gốc, cách sửa, test chứng minh. Chỉ ghi lỗi đã tái hiện được.

## Đợt 18/11/2026 — phân quyền, nhập liệu, toàn vẹn sổ

| Mã | Lỗi | Tái hiện | Nguyên nhân gốc | Sửa | Test |
|---|---|---|---|---|---|
| B-2026-11-01 | Ô ngày hiển thị theo ngôn ngữ trình duyệt (MM/dd/yyyy trên Windows tiếng Anh), dễ nhập nhầm ngày / tháng | Mở bất kỳ màn có ô ngày trên trình duyệt tiếng Anh | 67 ô `<input type="date">` trong 20 màn | Ô `DateInput` dùng chung: luôn hiện và nhận dd/MM/yyyy (gõ 05/03/2026, 5/3/2026, 05032026; năm 2 chữ số khi rời ô), nút mở lịch, giá trị trao đổi `yyyy-MM-dd`, kiểm min/max, báo ô sai bằng viền đỏ | `dates.test.ts` (phân tích, ngày không có thật, tháng 13 không đảo; **chặn mọi `type="date"` mới**), `guard.ui.test.tsx` |
| B-2026-11-02 | Thông báo lỗi của máy chủ in ngày dạng MM/dd/yyyy ("Không có kỳ kế toán chứa ngày 03/05/2026" cho ngày 5 tháng 3) | Ghi sổ chứng từ ngoài mọi kỳ | Máy chủ chạy InvariantGlobalization; 22 chỗ chèn ngày không định dạng | Định dạng `dd/MM/yyyy` ở cả 22 chỗ (14 tệp) | — (rà bằng tìm kiếm mã) |
| B-2026-11-03 | 25 lệnh ghi dữ liệu trả **lỗi hệ thống 500** khi yêu cầu thiếu trường (tạo chứng từ, phiếu thu/chi, quy tắc, tài khoản, đối tượng, kho, năm tài chính, phiếu kho, hóa đơn, đơn hàng, sao kê, tờ khai…) | Gửi `{}` tới các endpoint đó với người đủ quyền | Danh sách dòng / chuỗi null đi vào dịch vụ; năm tài chính và kho không kiểm mã / tên trống nên lỗi ở ràng buộc cơ sở dữ liệu | Bộ đọc JSON từ chối `null` ở trường không được null (`RespectNullableAnnotations`); danh sách dòng thiếu = rỗng; thiếu giá trị bắt buộc → 400 kèm thông báo tiếng Việt (không ghi gì: mọi lệnh ghi chạy trong giao dịch); thêm kiểm tra mã / tên / ngày cho năm tài chính và kho | `ApiTests.EveryWriteEndpoint_RefusesReadOnlyAndOtherCompanyUsers` (không lệnh nào trả 500) |
| B-2026-11-04 | Người **không có quyền** nhận thông báo "thiếu lý do" / "dữ liệu không hợp lệ" thay vì "không có quyền" ở 6 lệnh (mở lại kỳ, trả lại, hủy chứng từ, thay kết quả đo, tạo chứng từ, tạo phiếu thu/chi) | Người chỉ xem gửi yêu cầu không có lý do | Kiểm tra nội dung đứng trước kiểm tra quyền | Kiểm quyền trước | Cùng test: người chỉ xem không nhận 422 ở lệnh nào |
| B-2026-11-05 | Tự khớp sao kê với mã sao kê không tồn tại trả "thành công" | `POST /api/bank-statements/{id ngẫu nhiên}/auto-match` | Không kiểm sao kê tồn tại | Trả 404 "Sao kê không tồn tại" | Cùng test |

## Kiểm chứng phân quyền bằng HTTP (B-2026-11-03, -04)

`ApiTests.EveryWriteEndpoint_RefusesReadOnlyAndOtherCompanyUsers` lấy **mọi** lệnh POST / PUT / DELETE từ bảng định tuyến (137 lệnh, trừ đăng nhập, lần chạy đầu, đổi mật khẩu của chính mình). Mỗi lệnh được gọi bằng 3 người:

| Người gọi | Kết quả yêu cầu |
|---|---|
| Chỉ có vai trò Kiểm toán nội bộ (chỉ xem) | Không lệnh nào thành công; không lệnh nào trả "thiếu lý do / dữ liệu sai" trước khi báo thiếu quyền |
| Có toàn quyền nhưng ở **công ty khác** | Mọi lệnh 403 (hoặc 400 khi dữ liệu không đọc được nên không tới được mã xử lý) |
| Toàn quyền ở chính công ty, gửi dữ liệu rỗng | Không lệnh nào trả 500 |
