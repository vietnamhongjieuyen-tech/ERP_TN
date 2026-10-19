# HAK Accounting Offline ERP — hướng dẫn cho phiên làm việc

Người dùng nói tiếng Việt, **xưng "chị"** (16/10/2026 tự xưng "chị"; trước đó ghi nhầm là "anh"), gọi trợ lý là "em"; trả lời bằng tiếng Việt.

## Cách làm việc người dùng yêu cầu
- **Làm tiếp liên tục** theo danh sách "Bước tiếp theo" bên dưới; không chờ được giao lại việc.
- Câu hỏi chưa được trả lời → ghi vào mục "Câu hỏi đang chờ", **chuyển sang phần khác**, quay lại khi người dùng trả lời.
- Sau khi bị giới hạn chat / mở phiên mới: đọc file này + `STATUS.md`, làm tiếp từ "Đang làm dở".
- Mỗi đợt xong: chạy toàn bộ test, cập nhật `STATUS.md` và file này.

## Lệnh thường dùng (PowerShell)
- .NET SDK 10 ở `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (chưa có trong PATH).
- Test: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test` · Frontend: `npm --prefix web test; npm --prefix web run build`
- Migration: `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH="$env:DOTNET_ROOT;$env:PATH"; dotnet ef migrations add <Tên> --project src/Hak.Data --output-dir Migrations` — trigger bất biến viết tay bằng `migrationBuilder.Sql` cuối hàm Up.
- API dev: `dotnet run --project src/Hak.Api --urls http://127.0.0.1:5080 --Hak:Environment=Development --Hak:DatabasePath=<file db dev mới>`; web: `npm --prefix web run dev`; dữ liệu thử: `node web/scripts/dev-seed.mjs` (mật khẩu ghi vào `web/.dev-credentials.json`, không commit).
- Tiến trình `Hak.Api.exe` đang chạy sẽ khóa DLL → dừng nó trước khi build.
- Git: `& "$env:LOCALAPPDATA\Programs\PortableGit\cmd\git.exe"` (repo đã khởi tạo, nhánh main; commit sau mỗi đợt). Rust: `%USERPROFILE%\.cargo\bin`. PostgreSQL portable 18.6 (bản dùng chính): `%LOCALAPPDATA%\HAK\postgres18\pgsql\bin`, dữ liệu `...\postgres18\data`, cổng 5433, user `hak_admin`, mật khẩu trong `...\postgres18\superuser.pw` (không commit); khởi động: `pg_ctl -D <data> -o "-p 5433 -c listen_addresses=127.0.0.1" -l <base>\server.log -w start`. (Có thêm cụm cũ `%LOCALAPPDATA%\HAK\postgres` cùng cổng 5433 — đã dừng, không khởi động song song.)
- Test trên PostgreSQL: `$env:HAK_TEST_PG="Host=127.0.0.1;Port=5433;Username=hak_admin;Password=<postgres18\superuser.pw>"` rồi `dotnet test tests\Hak.Integration.Tests` (~12 phút; mỗi test tạo/xóa DB `hak_test_<guid>`).

## Nguyên tắc không thương lượng
Ghi sổ ACID; sổ cái/sổ kho/phân bổ/kết quả đo bất biến (interceptor + trigger); decimal, không float (UI dùng chuỗi/BigInt); quyền kiểm tra ở backend; không seed tài khoản/biểu mẫu pháp lý chưa xác minh (mã thử: CASH, AR, INV…); test không chạm DB thật; không đánh dấu DONE khi chưa đối chiếu dữ liệu thực.

## Quyết định nghiệp vụ đã được người dùng xác nhận
- Chênh lệch giá HĐ mua so với phiếu nhập đã ghi sổ: phần còn tồn → tồn kho, phần đã xuất → giá vốn (vai trò TK COGS), có cảnh báo + xác nhận (đã làm 2026-10-15).
- Giá xuất kho theo từng kho: FIFO / bình quân tức thời / bình quân cuối tháng.
- Dăm gỗ: BDMT = MT × độ khô % ÷ 100; đo khi nhập và đo ở tàu khi xuất (chứng từ là chứng thư); làm tròn độ khô 2, BDMT 3 chữ số; giá vốn theo MT, **giá mua/bán theo BDMT**; sổ kho giữ MT phiếu, MT chứng thư chỉ để tính BDMT.

## Câu hỏi đang chờ người dùng (không chặn việc khác)
1. Dữ liệu thực: ĐÃ CÓ thư mục dữ liệu FAST (đường dẫn ghi trong bộ nhớ riêng, không ghi ở đây; FAST, VFP, TCVN3; chỉ đọc, KHÔNG đọc SYS\dmuser). Công cụ tools/migrate-fast; 2026 khớp BCĐPS 7/7 tháng và khớp BCĐPS in từ FAST T6/2026. Chị QUYẾT ĐỊNH ÁP DỤNG TT99 từ 01/01/2026 (16/10; chuyển cả 2025 làm số so sánh, đã chạy thử khớp 19/19 tháng + B01/B02/B03 2025); bảng ánh xạ CHỜ chị duyệt (dòng 338811, 4111 cần xác nhận; lưu ý mẫu FAST là S04-DNN/TT133 → TT99 là tự chọn với DNNVV).
2. Chị (KTT) **xác minh** chế độ TT99 và các mẫu BCTC trong phần mềm (mẫu do người khác soạn/nhập).

## Đã trả lời 16/10/2026 (hỏi gộp một lần)
- Sao kê không mã giao dịch, trùng tiền + ngày với đúng một bút toán: **chỉ gợi ý, bắt xác nhận tay** (không tự khớp).
- Khấu hao/phân bổ tháng đầu–cuối: mặc định **tròn tháng** (từng thẻ vẫn đổi được).
- Lãi vay: **365 ngày**, tính từ ngày giải ngân đến ngày trước ngày trả nợ.
- Trả hàng mua: chênh lệch giá HĐ – giá vốn xuất **vào giá vốn** (giữ như đang làm); BDMT hàng dăm trả lại **theo tỷ lệ dòng gốc** (không đo lại).
- Chi tiền từ TK ngoại tệ: tỷ giá ghi sổ **bình quân gia quyền di động**.
- Hàng về chưa có hóa đơn (GRNI): **TK chi tiết của 331 "Hàng về chưa có hóa đơn"** theo dõi theo NCC; có HĐ thì chuyển sang 331 chính.
- Người xác minh chế độ và mẫu BCTC: **chị (kế toán trưởng)**.
## Đã trả lời 2026-10-15
- Chênh lệch giá hóa đơn mua so với phiếu nhập đã ghi sổ: **phần còn trong kho điều chỉnh giá trị tồn kho, phần đã xuất điều chỉnh giá vốn, có cảnh báo** (người ghi sổ phải xác nhận).
- Đồng ý cài Rust / Git / PostgreSQL (cài mức người dùng; PostgreSQL portable 18.6 ở %LOCALAPPDATA%\HAK\postgres, cổng 5433, chỉ 127.0.0.1; Rust MSVC cần thêm bộ C++ của VS Build Tools — cần quyền admin).
## Đang làm dở
- GĐ5 bước 1 — PostgreSQL: XONG 2026-10-15 (105/105 test tích hợp đạt trên PostgreSQL 18.6).
- GĐ4 TSCĐ & CCDC: XONG 2026-10-15 (src/Hak.Application/Assets, màn hình "Tài sản / CCDC", migration Phase4Assets cho cả SQLite và PostgreSQL — trigger PG của tài sản nằm trong `ImmutabilityTriggers.AssetStatements()`, KHÔNG thêm bảng mới vào `Statements()` vì migration đầu gọi hàm đó). 173/173 test SQLite.
- GĐ4 Ngoại tệ (bước 1): XONG 2026-10-15 (src/Hak.Application/Fx, màn hình "Ngoại tệ", migration Phase4Fx; dòng FXR chỉ mang mã loại tiền, không nguyên tệ). 180/180 test SQLite.
- GĐ4 Công nợ ngoại tệ: XONG 2026-10-15 (SettlementAllocation.ForeignAmount, FxPaymentService "Thu/chi ngoại tệ theo hóa đơn", đánh giá lại theo khoản mở trong FxService, InvoiceService.Fx.cs dòng công nợ nguyên tệ; vai trò FX_REALIZED_GAIN/LOSS). 190/190 test SQLite, 123/123 tích hợp trên PostgreSQL.
- GĐ4 Vay: XONG 2026-10-15 (src/Hak.Application/Loans, màn hình "Vay", migration Phase4Loans; trigger PG trong ImmutabilityTriggers.LoanStatements()). 198/198 test SQLite.
- GĐ4 Hàng mua trả lại / giảm giá hàng mua: XONG 2026-10-15 (InvoiceService.Adjustments.cs: CreatePurchaseReturnIssueAsync, PurchaseAdjustmentLinesAsync; StockService.ReverseValueAdjustmentsAsync). 200/200 test.
- GĐ4 Lương (import JSON) + quản trị khóa trao đổi: XONG 2026-10-15 (src/Hak.Application/Payroll, Exchange/ExchangeKeyService.cs, schemas/payroll.schema.json, màn hình "Lương"). 205/205 test.
- GĐ5 bước 2 — API chế độ PostgreSQL (Hak:Provider=PostgreSQL, ConnectionStrings:Hak) + ghi tuần tự bằng pg_advisory_xact_lock: XONG 2026-10-15.
- GĐ4 Giá thành sản xuất: XONG 2026-10-15 (src/Hak.Application/Production, màn hình "Giá thành"). 209/209 test.
- Sao lưu/khôi phục mã hóa (src/Hak.Application/Backup), Audit Viewer, quản trị người dùng + khóa đăng nhập sai + đồng bộ quyền mặc định, màn hình "Quản trị": XONG 2026-10-15. 215/215 test.
- Báo cáo: BCĐPS cộng dồn TK cha, Sổ Nhật ký chung, xuất Excel .xlsx (web/src/xlsx.ts, nút ExportButton trong common.tsx): XONG 2026-10-15. 216/216 test.
- Màn hình Thiết lập (TK, vai trò TK, kho/vật tư, năm TC) + tạo công ty trong Quản trị + xuất Excel các sổ: XONG 2026-10-15. 217/217 test.
- Nhập danh mục TK / đối tượng / số dư đầu kỳ từ Excel/CSV: XONG 2026-10-15 (src/Hak.Application/Setup/CatalogImportService.cs, web/src/sheetread.ts + ImportData.tsx). 232 test.
- Lần chạy đầu (tạo quản trị đầu tiên trên màn hình đăng nhập, /api/setup/status), khai báo + xác minh chế độ kế toán trên giao diện, lệnh khôi phục mật khẩu quản trị tại máy (`Hak.Api.exe --reset-admin-password <user>`), docs/08 hướng dẫn vận hành: XONG 2026-10-15. 235 test.
- Đo hiệu năng (docs/09, tools/Hak.Bench; ghi log vào %TEMP%\hak-bench vì scratchpad có thể không ghi được) + bật pooling SQLite + sửa sổ chi tiết O(n²): XONG 2026-10-15. 235 test SQLite, 163/163 tích hợp PostgreSQL.
- Report Engine (src/Hak.Domain/Reports, Application/Reports/ReportService.cs, màn hình "Báo cáo theo mẫu", migration Phase6Reports — trigger PG trong ImmutabilityTriggers.ReportStatements()): XONG 2026-10-15. 248 test. Lưu ý EF: thêm entity con có khóa GUID gán sẵn qua `db.AddRange`, không qua navigation của cha đang tracked (bị hiểu là UPDATE → lỗi xung đột).
- Đợt 15/10 (sau Report Engine): phân trang chứng từ, in (CSS + phiếu kế toán có số tiền bằng chữ), nhập vật tư / tồn kho đầu kỳ / TSCĐ-CCDC đầu kỳ, đổi TK cấp trên, tạo chứng từ từ dòng sao kê (gợi ý phí/lãi). 252 test; 170/170 PG. Lưu ý: server chạy InvariantGlobalization → `string.Normalize` không tách dấu; bỏ dấu tiếng Việt bằng bảng (StatementVoucherService.Plain).
- 16/10: TT99 bản gốc → `legal/TT99-2025` (Phụ lục II-A 184 TK, tệp nhập đề xuất, đề xuất vai trò). Đã nhận đủ Công báo (1579+1580 ngày 16/10). Đã dựng mẫu B01-DN, B02-DN (legal/TT99-2025/mau-*.json; nhãn TK DH/TDT/HCSD/HTM/BCC/SXDD/NO/BDSDT/DGL/LV/TLTS; NET_D/NET_C bỏ chứng từ CLS; MAX0). CHỜ chị: người xác minh chế độ và mẫu, quyết GRNI.
- Mã luồng tiền + B03-DN (trực tiếp): XONG 16/10 (Domain/Reports/CashFlow.cs, cột cash_flow_code ở voucher_lines/journal_lines, hàm CF(), cột Mã LCTT trong form chứng từ).
- B03 gián tiếp + B09 (8 mẫu số liệu + khung văn bản) + mẫu nhiều cột (ReportDefinition.Columns, migration Phase6ReportColumns) + hàm PS_D/PS_C, FXR, VT_D/VT_C: XONG 16/10. 260 test SQLite. Sinh mẫu: `python legal/TT99-2025/tools/templates.py %TEMP%\hak-tt99\tables legal/TT99-2025` và `python legal/TT99-2025/tools/b09.py legal/TT99-2025`. Còn: thử tay mẫu nhiều cột trên giao diện; phần văn bản B09 nhập trong phần mềm (hiện là tệp .md).
- Cây TK cha–con dạng modal (Setup.tsx Accounts + Modal trong common.tsx; SetupService.AccountTree.cs: AddChildAccountAsync, UpdateAccountAttributesAsync; API /accounts/{id}/children, /attributes) + áp dụng các quyết định 16/10 (sao kê không mã → chờ xác nhận; chi ngoại tệ theo bình quân di động; GRNI theo NCC): XONG 16/10. 262 test.
- 18/10: đưa mã nguồn lên GitHub công khai (nhánh cục bộ `public` 1 commit, đã lược tên DN/ngân hàng/số liệu thật → origin/main; KHÔNG đẩy nhánh `main` cục bộ vì lịch sử có số liệu thật; trước mỗi lần đẩy rà lại bằng grep). CI GitHub Actions đạt. Màn Công nợ: khớp tay + lịch sử/hủy phân bổ (GET /settlement/allocations). 86 + 178 test.
- 19/10: Biên bản đối chiếu công nợ (web/src/Confirmation.tsx, dùng /reports/account-ledger theo đối tượng; balanceText trong money.ts; /me/companies trả thêm taxCode). 86 + 178 test, 14 test web.
- Tiếp theo: nhập mẫu BCTC khi có TT99 Phụ lục IV đã xác minh (chỉ là dữ liệu mẫu, công cụ đã sẵn) → bộ cài Tauri/MSI (GĐ8; gói chạy độc lập tools/package/package.ps1 đã XONG 2026-10-15; Tauri cần VS C++ — cần quyền admin; tệp .ps1 phải lưu UTF-8 CÓ BOM cho PowerShell 5.1) → GĐ7 đối chiếu dữ liệu thật (chờ thư mục).
- Lưu ý khi thêm entity có decimal: phải thêm `HasConversion(money)` trong `ApplySqliteMoneyStorage` (SQLite), PostgreSQL tự áp numeric(19,4).

## Bước tiếp theo
1. ~~Đơn hàng~~ (xong 2026-10-15, 156 test).
2. GĐ4: ~~Thuế GTGT~~ (xong 2026-10-15, 160 test); ~~TSCĐ & khấu hao; CCDC phân bổ~~ (xong 2026-10-15, 173 test); ~~ngoại tệ: đánh giá lại tiền/vay~~ (xong 2026-10-15, 180 test); ~~công nợ ngoại tệ + chênh lệch tỷ giá đã thực hiện~~ (xong 2026-10-15, 190 test); ~~vay~~ (xong 2026-10-15, 198 test); ~~hàng mua trả lại / giảm giá hàng mua~~ (xong 2026-10-15, 200 test); ~~lương (import JSON)~~ (xong 2026-10-15, 205 test); ~~giá thành~~ (xong 2026-10-15, 209 test).
3. GĐ5 đa công ty/LAN (PostgreSQL), GĐ6 Report Engine + BCTC (cần TT99 đã xác minh), GĐ7 đối chiếu dữ liệu thực, GĐ8 bộ cài.
