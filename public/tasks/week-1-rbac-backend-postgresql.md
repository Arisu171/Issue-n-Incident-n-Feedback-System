# Tuần 1 — Nền tảng dữ liệu và RBAC

| | |
|---|---|
| Chủ đề | ASP.NET Core 8 · EF Core 8 · PostgreSQL 16 · RBAC · Swagger |
| Thời lượng | 120 phút tại lớp + hoàn thiện ngoài giờ |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Đề tài | Unified Ticketing & Incident Tracker |
| Mốc bàn giao | Tag `week-1` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) mục 2, 6 |

---

## 1. Mục tiêu

Dựng nền dữ liệu và mô hình phân quyền mà **năm tuần sau đều đứng trên đó**. Sai ở tuần này thì
mọi thứ phía sau phải sửa theo.

Kết thúc tuần, nhóm phải giải thích và chứng minh được:

- Vì sao cần **hai** bảng nối (`user_roles`, `role_permissions`) chứ không phải một.
- Vì sao vai trò có **cấp** (`rank`) và cấp giải quyết vấn đề gì.
- Vì sao vai trò có **phạm vi** (`is_global`) và điều gì hỏng nếu bỏ nó đi.
- Tạo schema từ model C# bằng migration, không gõ SQL tay.
- Không một response nào trả về `password_hash`.

**Ngoài phạm vi tuần này:** đăng nhập, JWT, `[Authorize]`. Tuần 3 mới làm.

---

## 2. Mô hình dữ liệu phải dựng

| Bảng | Vai trò |
|---|---|
| `users` | Tài khoản; `login` sinh từ email, duy nhất |
| `roles` | Vai trò, có `rank` (1..1000) và `is_global` |
| `permissions` | Mã quyền dạng `resource.action` |
| `user_roles` | Nối người ↔ vai trò, khoá chính kép |
| `role_permissions` | Nối vai trò ↔ quyền, khoá chính kép |

### 2.1 Sáu vai trò dựng sẵn

| Vai trò | `rank` | `is_global` |
|---|---:|:---:|
| `system` | 120 | ✔ |
| `admin` | 100 | ✔ |
| `manager` | 80 | ✘ |
| `responder` | 60 | ✘ |
| `support` | 40 | ✘ |
| `customer` | 10 | ✘ |

**`rank` để làm gì.** Không ai tác động lên người ngang hoặc cao cấp hơn mình, và không ai tự gỡ
vai trò của chính mình. Thiếu nó thì hai admin gỡ vai trò của nhau, hoặc một người tự khoá mình
ra ngoài (BR-SEC-08).

**`is_global` để làm gì.** Chỉ vai trò toàn cục mới cấp quyền toàn hệ thống. Không có cờ này thì
mọi phép kiểm theo project ở tuần 3 sẽ rơi vào nhánh dự phòng và **không có hiệu lực** — nhìn thì
như đã làm, chạy thì như chưa.

---

## 3. Phân công

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Chốt danh mục permission, sáu vai trò, thang cấp | Owner · BA | Nguyễn Bảo Long | Mục 2 và 11.1 của `Architecture.md` |
| Entity, `AppDbContext`, khoá chính kép, unique index | Tech Lead · Dev lõi | Nguyễn Bảo Long | `Domain/Entities.cs`, `Persistence/AppDbContext.cs` |
| Migration đầu tiên và seeder dữ liệu tham chiếu | Dev lõi | Nguyễn Bảo Long | `Persistence/Migrations/`, `DatabaseInitializer.cs` |
| Cấu hình khởi động API, Swagger, tệp dự án | Dev lõi | Nguyễn Bảo Long | `Program.cs`, `appsettings.json` |
| Controller CRUD user / role / permission | Developer | Nguyễn Huy Kiên | `Modules/Rbac/` |
| Đăng nhập, đăng ký, hồ sơ người dùng | Developer | Lê Sơn Trường | `Modules/Identity/` |
| Viết test ràng buộc và thang cấp | QA | Nguyễn Bảo Long | `RbacAndAuthTests`, `RoleHierarchyTests` |
| Chạy bộ test, đối chiếu bằng thao tác tay | Tester | Kiên và Trường | Ghi chú trong PR |
| Duyệt thiết kế và tiêu chí chấp nhận | Tech Lead · QA | Nguyễn Bảo Long | Ghi chú duyệt trong PR |

**Ranh giới sở hữu.** Lõi dự án, cấu hình, script deploy, script test và toàn bộ tài liệu thuộc về Long; Kiên và Trường phát triển tính năng trên nền đó. Vai tester của hai người là **chạy** bộ test ấy trên máy thật rồi ghi lại kết quả — viết script test là việc của vai QA.

---

## 4. Tiêu chí chấp nhận

| # | Điều kiện | Cách kiểm |
|---|---|---|
| 1 | `dotnet ef database update` dựng đủ 5 bảng trên PostgreSQL trắng | Chạy lại từ database rỗng |
| 2 | Sáu vai trò được seed đúng `rank` và `is_global` | Truy vấn bảng `roles` |
| 3 | Tài khoản bootstrap mang **cả** `admin` và `system` | Truy vấn `user_roles` |
| 4 | Không response nào chứa `password_hash` | Test quét response |
| 5 | Tên vai trò trùng → `409` | Test tích hợp |
| 6 | `rank` ngoài 1..1000 → cơ sở dữ liệu từ chối | Check constraint |
| 7 | Admin không gỡ được vai trò của admin khác | `RoleHierarchyTests` |
| 8 | Không ai tự gỡ vai trò của chính mình | `RoleHierarchyTests` |

---

## 5. Chỗ dễ sai

| Sai lầm | Hậu quả | Cách tránh |
|---|---|---|
| Bỏ `is_global`, để mọi vai trò cấp quyền toàn cục | Tuần 3 phân quyền theo project **không có hiệu lực** mà test vẫn xanh | Có cờ ngay từ tuần 1 |
| Gán `rank` bằng nhau cho hai vai trò | Không phân biệt được ai trên ai | Khoảng cách 20–40 điểm, chừa chỗ chèn |
| Xoá vai trò bằng cách bỏ khỏi mã | Seeder chỉ thêm, không bao giờ gỡ — vai trò vẫn nằm trong cơ sở dữ liệu | Dùng danh sách vai trò đã bỏ, một nguồn cho cả bước gỡ lẫn bước chặn tạo lại |
| Tài khoản bootstrap chỉ có `admin` | Mất quyền cấu hình nền là không còn đường lấy lại | Bootstrap mang cả hai vai trò và được khôi phục mỗi lần khởi động |

---

## 6. Bàn giao

- [ ] Migration chạy được trên cơ sở dữ liệu trắng
- [ ] Seeder không ghi đè dữ liệu đã có
- [ ] Swagger liệt kê đủ endpoint RBAC
- [ ] Test tuần 1 xanh
- [ ] PR có ghi chú duyệt của Tech Lead
