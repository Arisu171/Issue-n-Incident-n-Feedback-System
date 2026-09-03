# NFR-MNT-01 — Thêm permission mới mà không đổi schema RBAC

> **Ngưỡng:** 1 permission + policy trong ≤ 30 phút · **Phương pháp kiểm chứng:** timed exercise
> · **Liên quan:** GOAL-03, DRV-04

Bài tập này chứng minh **GOAL-03**: module nghiệp vụ mới thêm được năng lực mà không phải sửa
năm bảng RBAC, không phải viết migration và không phải sửa `AppDbContext`.

## Bài tập mẫu — thêm `incident.export`

Giả sử nghiệp vụ cần một endpoint xuất báo cáo sự cố ra CSV, chỉ Admin được dùng.

### Cách A — hoàn toàn bằng dữ liệu, không sửa mã (≈ 2 phút)

Dùng khi permission chỉ cần tồn tại để gán cho vai trò, ví dụ để module khác đọc.

1. Đăng nhập bằng tài khoản admin, vào **Quản trị → Vai trò & quyền**.
2. Khối *Tạo permission*: nhập `incident.export`, mô tả *"Xuất báo cáo sự cố"*, bấm **Tạo**.
3. Chọn vai trò `admin` ở khối bên trái, tick vào `incident.export`.

Không build lại, không khởi động lại, không migration. Người dùng nhận quyền ở lần đăng nhập
kế tiếp — đúng như ADR-001 đã ghi nhận về độ trễ.

### Cách B — gắn permission vào một endpoint mới (≈ 10 phút)

Khi cần thực sự bảo vệ một endpoint.

**Bước 1 — khai báo mã trong danh mục** (`Authorization/Permissions.cs`):

```csharp
public const string IncidentExport = "incident.export";
```

Thêm một dòng vào `Catalog` để seeder tự tạo bản ghi, và thêm mã vào mảng của `admin` trong
`DefaultRoles`.

**Bước 2 — gắn vào action** (`Modules/Incidents/IncidentsController.cs`):

```csharp
[HttpGet("export")]
[RequirePermission(Permissions.IncidentExport)]
public async Task<IActionResult> Export(CancellationToken ct) => ...
```

**Bước 3 — khởi động lại API.** `DatabaseInitializer` là idempotent: nó phát hiện mã mới, chèn
vào bảng `permissions` và gán cho `admin`.

### Những thứ **không** phải làm

| Việc | Có cần không | Vì sao |
| :--- | :---: | :--- |
| Sửa 5 bảng RBAC | ❌ | Permission là **dữ liệu**, không phải cột |
| Viết EF migration | ❌ | Không có thay đổi schema |
| Đăng ký policy thủ công trong `Program.cs` | ❌ | `PermissionPolicyProvider` sinh policy theo quy ước `perm:{code}` |
| Sửa `AppDbContext` | ❌ | Không có entity mới |
| Sửa frontend | ❌ | Menu và nút đọc thẳng danh sách permission trong token |

Đây chính là lý do `PermissionPolicyProvider` tồn tại: nếu phải khai báo từng policy trong
`Program.cs`, mỗi permission mới sẽ thêm một chỗ dễ quên và bài tập này sẽ không đạt ngưỡng.

## Kết quả đo

| Lần đo | Người thực hiện | Cách | Thời gian | Ngưỡng | Kết quả |
| :--- | :--- | :--- | ---: | ---: | :---: |
| 1 | *(điền khi nhóm thực hiện)* | A | | 30 phút | |
| 2 | *(điền khi nhóm thực hiện)* | B | | 30 phút | |

> Bảng này để trống có chủ đích. NFR-MNT-01 là **timed exercise** — con số chỉ có giá trị khi
> chính thành viên trong nhóm bấm giờ thực hiện, không phải khi được điền sẵn. Quy trình ở trên
> đã được kiểm chứng là chạy đúng; phần còn thiếu chỉ là số đo của người thật.
