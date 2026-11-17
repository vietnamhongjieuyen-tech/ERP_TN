# 08 — Hướng dẫn vận hành (cài đặt, LAN, sao lưu, nâng cấp) — v0.1

> Bộ cài MSI/EXE bằng Tauri (GĐ8) chưa có — cần bộ C++ của Visual Studio Build Tools, phải cài bằng quyền quản trị. Trong lúc chờ, dùng **gói chạy độc lập** (mục 1a).
> Nội dung pháp lý (hệ thống TK, biểu mẫu TT 99/2025) **[CẦN XÁC MINH]** — xem docs/07.

## 1. Mô hình triển khai

| Mô hình | Lưu trữ | Khi nào dùng |
|---|---|---|
| Một máy | SQLite (một tệp `.db`) | 1 người dùng / 1 máy |
| Mạng nội bộ (LAN) | PostgreSQL trên máy chủ; mỗi máy trạm chạy API riêng ở `127.0.0.1` kết nối tới máy chủ CSDL | Nhiều người cùng làm |

Ở chế độ LAN, các lệnh ghi được xếp lượt bằng khóa `pg_advisory_xact_lock` (đọc vẫn song song); xung đột deadlock/serialization tự chạy lại tối đa 5 lần.

## 1a. Gói chạy độc lập (không cần cài .NET / Node)

Tạo gói (máy phát triển): `.\tools\package\package.ps1` → thư mục `artifacts\HAK` gồm:
- `HAK.exe` — API kèm .NET, một tệp, khoảng 120 MB;
- `wwwroot` — giao diện; API phục vụ cùng địa chỉ, có header bảo mật CSP, nosniff, chống nhúng khung;
- `Chay HAK.cmd` — môi trường Production, cổng 5080;
- `Chay HAK - thu nghiem.cmd` — môi trường Test, cổng 5081, CSDL riêng;
- `Khoi phuc mat khau quan tri.cmd`;
- `DOC-TRUOC.txt`;
- `SHA256SUMS.txt`.

Chép cả thư mục sang máy dùng rồi chạy tệp `.cmd`. Các tệp `.cmd` phải là ASCII (chữ không dấu) và xuống dòng CRLF — cmd.exe đọc lệch tệp UTF-8 / LF (lỗi gặp thật 17/10/2026; `.gitattributes` khóa `*.cmd eol=crlf`).

Dữ liệu nằm ở `%LOCALAPPDATA%\HAK\<môi trường>\`, không nằm trong thư mục chương trình. Vì vậy thay bản mới chỉ cần thay thư mục; nhớ sao lưu trước, xem mục 6.

Đã thử: gói chạy ở môi trường Test với CSDL tạm. Màn hình lần chạy đầu hiển thị đúng. Chuyển trang và gọi API đúng (đường `/api` không tồn tại trả 404), trình duyệt không báo lỗi CSP.

Gói **chưa ký số** — Windows SmartScreen có thể cảnh báo. Ký mã bằng chứng thư của doanh nghiệp thuộc GĐ8.

## 2. Cấu hình

Đặt trong `appsettings.json` cạnh `Hak.Api.exe`, hoặc bằng biến môi trường (dấu `:` thay bằng `__`, ví dụ `Hak__Provider`).

| Khóa | Giá trị | Mặc định |
|---|---|---|
| `Hak:Environment` | `Development` / `Test` / `Production` | `Development` |
| `Hak:Provider` | `Sqlite` / `PostgreSQL` | `Sqlite` |
| `Hak:DatabasePath` | Đường dẫn tệp SQLite | `%LOCALAPPDATA%\HAK\<môi trường>\hak.db` |
| `ConnectionStrings:Hak` | Chuỗi kết nối PostgreSQL — **chỉ đặt bằng biến môi trường `ConnectionStrings__Hak`**, không ghi vào tệp trong kho mã | — |
| `Hak:BackupPath` | Thư mục bản sao lưu (SQLite) | `%LOCALAPPDATA%\HAK\<môi trường>\backups` |
| `--urls` | Địa chỉ API | Nên giữ `http://127.0.0.1:<cổng>` (chỉ máy này truy cập) |

Production + PostgreSQL: chương trình **từ chối khởi động** nếu `SslMode=Disable`.

## 3. Lần chạy đầu (trình tự bắt buộc)

1. Khởi động API. CSDL được tạo/nâng cấp tự động (migration) và nạp dữ liệu tham chiếu (vai trò, quyền, loại tiền).
2. Mở giao diện: khi CSDL chưa có người dùng, màn hình đăng nhập chuyển sang **Lần chạy đầu** → tạo quản trị hệ thống (mật khẩu ≥ 10 ký tự). Chỉ làm được một lần.
3. **Quản trị → Chế độ kế toán**: khai báo phiên bản (mã, hiệu lực, ít nhất một căn cứ pháp lý). Phiên bản mới luôn **CHƯA xác minh**; chỉ bấm *Xác minh* sau khi đã đối chiếu văn bản gốc và ghi rõ người ký.
4. **Quản trị → Công ty**: tạo công ty (đồng tiền hạch toán, chế độ kế toán).
5. **Quản trị → Người dùng**: tạo người dùng, cấp vai trò theo công ty. Nguyên tắc: người lập chứng từ khác người duyệt/ghi sổ.
6. Đăng nhập vào công ty → **Thiết lập**:
   - Năm tài chính (tạo kỳ tháng);
   - Hệ thống tài khoản — nhập hàng loạt ở tab *Nhập từ Excel/CSV* (tải tệp mẫu, *Kiểm tra*, rồi *Nhập*); sau đó **mở TK chi tiết trước khi hạch toán** ở tab *Hệ thống tài khoản* (dạng cây): nút **+ TK con** trên TK cần chi tiết (vd 331 → 3311 phải trả người bán, 3318 hàng về chưa có hóa đơn; 112 → 1121 theo từng ngân hàng). TK cha tự chuyển thành TK tổng hợp; khi TK cha đã có bút toán hoặc đã gắn vai trò thì không chuyển được — phần mềm báo lý do;
   - Vai trò tài khoản (ánh xạ mọi vai trò bắt buộc; thiếu thì phân hệ từ chối ghi sổ `RULE_ROLE_UNMAPPED`);
   - Đối tượng, kho, vật tư, phương pháp tính giá theo kho.
7. Số dư đầu kỳ: tab *Nhập từ Excel/CSV* → *Số dư đầu kỳ* → tạo chứng từ OPB (Nháp) → **Chứng từ**: gửi duyệt → duyệt → ghi sổ. Công nợ nhập chi tiết từng hóa đơn còn nợ (có hạn thanh toán) để theo dõi thanh toán sau này.
   Tồn kho đầu kỳ: nhập danh mục vật tư, rồi chọn loại *Tồn kho đầu kỳ* với một **TK trung gian**. Mỗi kho được tạo một phiếu nhập NHÁP, ghi Nợ TK kho / Có TK trung gian. Trong chứng từ OPB, ghi **Nợ TK trung gian** thay cho các TK hàng tồn kho, để TK trung gian về 0. Kho phải đã có phương pháp tính giá trước khi ghi sổ phiếu. Sau khi ghi sổ, dùng đối chiếu kho – sổ cái để kiểm tra.
   TSCĐ/CCDC đầu kỳ: làm sau khi chứng từ OPB đã ghi sổ. Chọn loại *TSCĐ / CCDC đầu kỳ* và nhập số chứng từ OPB.
   - Nguyên giá từng thẻ được lấy từ dòng Nợ TK tài sản trên OPB.
   - Hao mòn lũy kế và số tháng còn lại khai trên tệp; khấu hao tiếp từ ngày số dư, không trích bù kỳ cũ.
   - Với CCDC, nhập giá trị còn lại vào cột Nguyên giá.
   - Thẻ ở trạng thái nháp; người thứ hai ghi tăng.
8. Đối chiếu: Bảng cân đối số phát sinh tại ngày số dư phải khớp số liệu phần mềm cũ trước khi bắt đầu ghi nghiệp vụ mới.

## 4. Chế độ LAN với PostgreSQL

Máy chủ CSDL (đã thử với PostgreSQL 18.6):

```sql
CREATE ROLE hak_app LOGIN PASSWORD '<mật khẩu mạnh>';
CREATE DATABASE hak OWNER hak_app ENCODING 'UTF8';
```

- `postgresql.conf`: `listen_addresses = '<IP LAN của máy chủ>'`, `ssl = on` (chứng chỉ máy chủ).
- `pg_hba.conf`: chỉ cho dải mạng nội bộ, bắt buộc SSL và scram:
  `hostssl  hak  hak_app  192.168.1.0/24  scram-sha-256`
- Tường lửa Windows: mở cổng PostgreSQL **chỉ** cho dải LAN.

Mỗi máy trạm:

```
setx ConnectionStrings__Hak "Host=<máy chủ>;Port=5432;Database=hak;Username=hak_app;Password=<...>;SslMode=VerifyFull"
setx Hak__Provider PostgreSQL
setx Hak__Environment Production
```

API trên máy trạm chỉ nghe `127.0.0.1`. Không mở cổng API ra mạng.

## 5. Sao lưu và khôi phục

### SQLite (một máy)
- **Quản trị → Sao lưu**: tạo bản `.hakbk` (ảnh chụp trực tuyến, không cần dừng phần mềm), mã hóa AES-256-GCM bằng mật khẩu sao lưu ≥ 12 ký tự. **Mất mật khẩu sao lưu là mất bản sao lưu** — lưu mật khẩu tách biệt (két / quản lý mật khẩu).
- Nguyên tắc 3-2-1: 3 bản, 2 loại thiết bị, 1 bản ngoài văn phòng. Tối thiểu sao lưu cuối mỗi ngày làm việc và trước khi khóa sổ / nâng cấp.
- **Lịch sao lưu tự động** (Quản trị → Sao lưu → Lịch sao lưu tự động): bật, chọn giờ, số bản tự động giữ lại, và (nên có) một thư mục chép thêm trên ổ USB / máy khác. Mỗi ngày, lần đầu phần mềm đang chạy từ giờ đã chọn trở đi, sẽ tạo bản `hak-auto-<thời điểm>.hakbk`, chép sang thư mục thêm, xóa bản tự động cũ quá số giữ lại (bản sao lưu tay không bị xóa). Phần mềm không chạy thì không sao lưu: bản của ngày đó được tạo khi mở lại trong ngày. Mật khẩu sao lưu của lịch lưu trong `schedule.json` ở thư mục sao lưu, mã hóa bằng Windows DPAPI theo máy — chuyển sang máy khác phải nhập lại; vẫn phải cất mật khẩu riêng để khôi phục khi hỏng máy. Kết quả lần chạy gần nhất (và lỗi, ví dụ USB không cắm) hiện ngay trên dòng tiêu đề của lịch. `Chạy ngay` để thử.
- **Kiểm tra** định kỳ (hằng tháng): giải mã, kiểm SHA-256, `integrity_check`, chuỗi nhật ký kiểm toán của mọi công ty.
- **Khôi phục**: tạo tệp `restored-*.db` bên cạnh bản sao lưu (không ghi đè CSDL đang chạy). Sau đó: dừng phần mềm → đặt `Hak:DatabasePath` tới tệp khôi phục → khởi động → kiểm tra số liệu → giữ tệp cũ ít nhất đến khi xác nhận.
- **Chuyển CSDL sang máy khác** (khôi phục trên máy mới, đổi máy chủ): khóa trao đổi dữ liệu (lương, chứng từ JSON) được bảo vệ bằng Windows DPAPI của máy cũ nên máy mới không đọc được. Vào **Lương → Khóa trao đổi**: ngừng dùng khóa cũ, cấp khóa mới và cấu hình lại ở hệ thống gửi. Số liệu kế toán không bị ảnh hưởng.
- Khi khởi động, phần mềm tự xóa bản chụp tạm chưa mã hóa (`.snapshot-*.db`, `.verify-*.db`) còn sót trong thư mục sao lưu do lần sao lưu trước bị ngắt giữa chừng (mất điện, tắt máy).

### PostgreSQL (LAN)
Chức năng sao lưu trong phần mềm trả `NOT_SUPPORTED`; dùng công cụ của PostgreSQL trên máy chủ (Task Scheduler):

```
pg_dump -h 127.0.0.1 -U hak_app -Fc -f D:\hak-backup\hak-%DATE%.dump hak
```

- Mã hóa tệp dump trước khi đưa ra ngoài (BitLocker / 7-Zip AES-256).
- Thử khôi phục định kỳ vào **CSDL mới**: `createdb hak_restore_test` rồi `pg_restore -d hak_restore_test <tệp>`; không khôi phục đè lên `hak` đang dùng.

## 1b. Bộ cài đặt (một tệp .exe, không cần quyền quản trị)

Tạo (máy phát triển): `.\tools\package\build-setup.ps1 [-Package artifacts\HAK-…] [-Version 2026.11.17]` → `artifacts\HAK-Setup-<phiên bản>.exe` (khoảng 57 MB) và tệp `.sha256.txt` kèm theo. Bộ cài dựng bằng IExpress có sẵn trong Windows, không tải thêm công cụ.

Trên máy dùng: chạy `HAK-Setup-….exe` (Windows SmartScreen có thể cảnh báo vì tệp chưa ký số → "More info / Thông tin thêm" → "Run anyway / Vẫn chạy"). Bộ cài:
- cài cho người dùng hiện tại vào `%LOCALAPPDATA%\Programs\HAK`, không cần quyền quản trị;
- kiểm mã băm từng tệp, giải nén vào thư mục mới rồi mới thay bản cũ (lỗi giữa chừng không làm hỏng bản đang dùng);
- tạo biểu tượng "HAK Accounting" trên màn hình nền và menu Start (kèm "HAK - thử nghiệm", "Khôi phục mật khẩu quản trị", "Gỡ cài đặt HAK"), và mục "HAK Accounting" trong Settings → Apps để gỡ;
- **nâng cấp**: chạy bộ cài bản mới. HAK đang chạy thì bộ cài hỏi rồi mới tắt; trước khi thay, chép CSDL vào `%LOCALAPPDATA%\HAK\<Production|Test>\backups\truoc-nang-cap-<bản cũ>-<thời điểm>.db` (bản chép KHÔNG mã hóa, cùng mức bảo vệ với CSDL; xóa khi đã kiểm tra bản mới ổn);
- **gỡ**: chỉ xóa chương trình, **không xóa dữ liệu** `%LOCALAPPDATA%\HAK`.

Chưa có: chữ ký số cho bộ cài (cần chứng thư ký mã), bản MSI / Tauri (cần Visual Studio C++ — quyền quản trị), cài cho mọi người dùng trên máy.

## 6. Nâng cấp phiên bản

Máy đơn dùng bộ cài: chạy bộ cài bản mới (mục 1b), bộ cài tự chép dữ liệu trước khi thay. Vẫn nên sao lưu mã hóa trước (mục 5).

1. Sao lưu (mục 5) và kiểm tra bản sao lưu.
2. LAN: **dừng phần mềm trên tất cả máy trạm** (máy cũ chạy trên lược đồ mới có thể lỗi).
3. Thay chương trình, khởi động một máy trước: migration chạy tự động, quyền mặc định mới được cấp bổ sung cho vai trò có sẵn (ghi nhật ký `role.sync_defaults`).
4. Kiểm tra đăng nhập, bảng cân đối số phát sinh, rồi mới cập nhật các máy còn lại.

## 7. Bảo mật vận hành

- Sai mật khẩu 5 lần liên tiếp → khóa 15 phút (quản trị có thể đặt lại mật khẩu). Phiên đăng nhập tối đa 10 giờ. Nhập sai **mật khẩu hiện tại** khi đổi mật khẩu cũng tính như đăng nhập sai; đủ 5 lần thì tài khoản tạm khóa và mọi phiên đang mở kết thúc.
- Nhật ký không lưu nội dung gõ vào ô tên đăng nhập khi tên đó không tồn tại (phòng trường hợp gõ nhầm mật khẩu vào ô tên).
- Khóa bí mật của hệ thống gửi dữ liệu (khóa trao đổi) được mã hóa trong CSDL bằng Windows DPAPI; chỉ hiện một lần lúc cấp.
- Ngừng dùng người dùng → mọi phiên của người đó bị đóng ngay.
- Sổ cái, sổ kho, phân bổ, khấu hao… **không sửa/xóa được** (trigger CSDL). Sai sót xử lý bằng chứng từ đảo/điều chỉnh. Tuyệt đối không sửa trực tiếp trong CSDL.
- Tệp đính kèm chứng từ lưu trong cơ sở dữ liệu (tối đa 10 MB mỗi tệp): dung lượng CSDL và bản sao lưu tăng theo; chỉ nhận PDF, ảnh, tệp văn phòng, XML, không nhận tệp chạy được hay trang web.
- Nhận dạng chữ (OCR) sổ phụ chạy trong trình duyệt bằng tesseract.js (Apache-2.0) với dữ liệu tiếng Việt / tiếng Anh nằm sẵn trong thư mục `wwwroot/ocr` của gói cài: không cần mạng, không gửi ảnh ra ngoài.
- Nhật ký kiểm toán là chuỗi băm; xem tại **Quản trị → Nhật ký**; bản sao lưu kiểm tra lại toàn chuỗi.

## 8. Sự cố thường gặp

| Hiện tượng | Nguyên nhân / cách xử lý |
|---|---|
| API không khởi động: cổng đang dùng | Phiên bản khác đang chạy — tắt `Hak.Api.exe` cũ hoặc đổi `--urls` |
| `Hak:Provider=PostgreSQL cần chuỗi kết nối…` | Chưa đặt biến `ConnectionStrings__Hak` cho đúng tài khoản Windows |
| `CONCURRENCY_CONFLICT` | Người khác vừa sửa cùng dữ liệu — tải lại rồi làm lại |
| `LEDGER_IMMUTABLE` | Cố sửa dữ liệu đã ghi sổ — dùng đảo/điều chỉnh |
| `RULE_ROLE_UNMAPPED` | Thiếu ánh xạ vai trò TK — Thiết lập → Vai trò tài khoản |
| Tài khoản bị khóa | Chờ 15 phút hoặc quản trị đặt lại mật khẩu |
| Quên mật khẩu quản trị duy nhất | Trên máy chạy API (cùng cấu hình CSDL), mở cửa sổ dòng lệnh: `Hak.Api.exe --reset-admin-password <tên đăng nhập>` → nhập mật khẩu mới 2 lần (không hiện ký tự). Chỉ áp dụng cho quản trị hệ thống đang hoạt động; tài khoản được mở khóa, mọi phiên bị đóng, ghi nhật ký `user.password_recover_local` (tên máy, tài khoản Windows). Vẫn nên duy trì ít nhất 2 quản trị hệ thống |
