# HAK Accounting Offline ERP

Phần mềm kế toán chạy offline trên Windows cho doanh nghiệp Việt Nam: một máy (SQLite) hoặc mạng nội bộ (PostgreSQL). Backend .NET 10, giao diện React chạy trong trình duyệt tại `127.0.0.1`; dữ liệu chỉ nằm trên máy / máy chủ nội bộ.

**Hiện trạng:** đã có code, giao diện và test tự động cho các phân hệ bên dưới; **chưa phân hệ nào được đánh dấu hoàn thành** vì còn chờ đối chiếu trên dữ liệu thực và chờ kế toán trưởng xác minh chế độ kế toán, mẫu báo cáo. Chi tiết từng chức năng: [STATUS.md](STATUS.md).

## Phân hệ

- **Lõi kế toán:** chứng từ lập → duyệt → ghi sổ (tách người lập / người duyệt), sổ cái bất biến (sửa bằng đảo / điều chỉnh), khóa sổ kỳ, kết chuyển, nhật ký kiểm toán chuỗi băm, phân quyền theo công ty.
- **Tiền và ngân hàng:** phiếu thu / chi, báo Nợ / Có, lệnh thanh toán (UNC), nhập sao kê và đối chiếu.
- **Công nợ:** phân bổ thanh toán theo hóa đơn (tự động FIFO hoặc khớp tay), tuổi nợ, biên bản đối chiếu công nợ.
- **Kho, mua / bán:** đơn hàng, phiếu kho, hóa đơn; giá xuất kho FIFO / bình quân tức thời / bình quân cuối tháng theo từng kho; hàng dăm gỗ theo MT và BDMT.
- **Thuế GTGT, TSCĐ / CCDC, ngoại tệ, vay, lương (nhập gói JSON có chữ ký), giá thành sản xuất.**
- **Báo cáo:** bảng cân đối số phát sinh, sổ chi tiết, sổ nhật ký chung, xuất Excel, in; Report Engine với bộ mẫu báo cáo tài chính dựng theo Thông tư 99/2025/TT-BTC (`legal/TT99-2025`, **chưa xác minh** — người dùng tự khai báo và xác minh trước khi dùng thật).
- **Vận hành:** sao lưu / khôi phục mã hóa, nhập danh mục và số dư đầu kỳ từ Excel/CSV, công cụ chuyển dữ liệu từ FAST Accounting (`tools/migrate-fast`), gói chạy độc lập (`tools/package`).

## Cấu trúc

```
docs/        SRS & BRM, kiến trúc & ERD, đặc tả engine, JSON, báo cáo, kiểm thử, sổ pháp lý, vận hành, hiệu năng
legal/       Văn bản pháp lý và mẫu báo cáo dựng theo đó (chưa xác minh)
schemas/     JSON Schema của các gói trao đổi dữ liệu + ví dụ
src/
  Hak.Domain          thực thể, Money, validator, công thức báo cáo (không I/O)
  Hak.Data            EF Core, migration SQLite, interceptor + trigger bất biến
  Hak.Data.Postgres   migration và trigger cho PostgreSQL
  Hak.Application     các dịch vụ nghiệp vụ (ghi sổ, công nợ, kho, thuế, tài sản, báo cáo…)
  Hak.Api             ASP.NET Core API cục bộ, phục vụ luôn giao diện đã build
web/         React + TypeScript (Vite)
tests/       unit test (Hak.Domain.Tests) và integration test trên SQLite / PostgreSQL thật (Hak.Integration.Tests)
tools/       đóng gói, đo hiệu năng, chuyển dữ liệu từ FAST
```

## Build & test

Yêu cầu .NET SDK 10 và Node.js 24.

```powershell
dotnet test
```

```powershell
npm --prefix web ci; npm --prefix web test; npm --prefix web run build
```

Chạy integration test trên PostgreSQL: đặt biến môi trường `HAK_TEST_PG` (chuỗi kết nối tới một máy chủ PostgreSQL thử nghiệm; mỗi test tự tạo và xóa cơ sở dữ liệu `hak_test_<guid>`) rồi `dotnet test tests/Hak.Integration.Tests`.

## Chạy thử trên máy phát triển

1. API (môi trường Development, cơ sở dữ liệu riêng tại `%LOCALAPPDATA%\HAK\Development\hak.db`):

```powershell
dotnet run --project src/Hak.Api --urls http://127.0.0.1:5080 --Hak:Environment=Development
```

2. Giao diện (Vite proxy `/api` sang cổng 5080), mở http://127.0.0.1:5173:

```powershell
npm --prefix web run dev
```

3. Dữ liệu thử (chỉ chạy khi API báo Development; mật khẩu sinh ngẫu nhiên, ghi vào `web/.dev-credentials.json`, không commit):

```powershell
npm --prefix web run seed:dev
```

Với cơ sở dữ liệu trống, màn hình đăng nhập cho tạo quản trị hệ thống đầu tiên. Cài đặt, chế độ LAN, sao lưu, nâng cấp: [docs/08-VAN-HANH.md](docs/08-VAN-HANH.md).

## Nguyên tắc không thương lượng

Ghi sổ ACID; sổ cái bất biến (sửa bằng đảo / điều chỉnh); decimal, không float (giao diện xử lý tiền bằng chuỗi / BigInt); JSON không phải cơ sở dữ liệu kế toán; không cài sẵn hệ thống tài khoản hay biểu mẫu pháp lý chưa xác minh; test không bao giờ chạm cơ sở dữ liệu thật; quyền kiểm tra ở backend.
