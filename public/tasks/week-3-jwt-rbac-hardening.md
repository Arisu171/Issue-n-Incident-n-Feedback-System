# Tuần 3 — Xác thực, phân quyền theo project, hardening

| | |
|---|---|
| Chủ đề | JWT · Authorization policy · Phân quyền theo project · Rate limit · Sanitize |
| Thời lượng | 120 phút tại lớp + hoàn thiện ngoài giờ |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Mốc bàn giao | Tag `week-3` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) mục 2, 4.6 |

> Tuần nặng nhất. Đây là tuần quyết định hệ thống có thật sự cách ly dữ liệu hay không.

---

## 1. Mục tiêu

Kết thúc tuần, nhóm phải giải thích và chứng minh được:

- Token mang **danh tính**, không mang quyền — quyền đọc lại từ cơ sở dữ liệu mỗi request.
- Vì sao **hình dạng đường dẫn** quyết định phạm vi kiểm quyền.
- Vì sao nhánh "quyền ở bất kỳ project nào" là một đường leo thang, và chặn nó bằng cách nào.
- Quyền của khách hàng là **giao** của hai vế.

---

## 2. Ba lớp phải dựng

### 2.1 Xác thực

JWT mang `sub`, `iat`. Không mang danh sách quyền: quyền có thể bị thu hồi giữa chừng, và một
token mang sẵn quyền thì thu hồi không có tác dụng cho tới khi hết hạn.

Đổi mật khẩu **giết mọi phiên khác**: token cấp trước mốc đổi bị từ chối, phiên đang thao tác được
giữ lại (BR-SEC-09).

### 2.2 Nạp quyền theo request

Middleware đọc vai trò và permission từ cơ sở dữ liệu, tính **quyền hiệu lực cho request này**, rồi
phát thành ba loại claim:

| Claim | Nội dung | Dùng để |
|---|---|---|
| `perm` | Quyền hiệu lực cho request | Kiểm quyền |
| `gperm` | Chỉ quyền từ vai trò toàn cục | Endpoint quản trị hệ thống |
| `pperm` | `"{slug}:{code}"` | Lọc dữ liệu trên màn hình xuyên project |

### 2.3 Phạm vi theo đường dẫn

| Đường dẫn | Phạm vi kiểm quyền |
|---|---|
| `api/projects/{project}/…` | Quyền trong project đó |
| Không có `{project}` | Toàn cục ∪ hợp mọi project |

Nhờ vậy hơn 130 chỗ gắn `[RequirePermission]` tự thành project-aware mà không sửa dòng nào.

**Cái giá:** endpoint thuộc project mà quên `{project}` sẽ âm thầm rơi về kiểm toàn cục. Bắt buộc
có **test canh gác** liệt kê mọi controller và bắt mỗi cái tự khai.

---

## 3. Hai đường cấp quyền

| Bảng | Trả lời | Dùng cho |
|---|---|---|
| `project_members` | Người này làm ở project nào | Nhân viên |
| `project_role_access` | Project này mở cho vai trò nào | Khách hàng |
| `project_subscriptions` | Khách này có dùng dịch vụ của project không | Vế thứ hai của khách hàng |

```
quyền khách hàng = (project mở cho vai trò) ∧ (khách đã tự nhận)
```

---

## 4. Hardening

| Việc | Quy tắc |
|---|---|
| Rate limit | Theo user/IP; `429` kèm `Retry-After` |
| Sanitize | Markdown lưu thô; render qua Markdig + `HtmlSanitizer`; client lọc lại bằng DOMPurify |
| Idempotency | `Idempotency-Key` bắt buộc với `POST` tạo ticket/bình luận |
| Optimistic concurrency | `ETag` / `If-Match`; sai phiên bản → `412` |
| Thang cấp vai trò | Không tác động lên người ngang hoặc cao cấp hơn |

---

## 5. Phân công

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Chốt ma trận quyền, hợp đồng mã lỗi | Owner · BA | Nguyễn Bảo Long | Mục 2.7 của `Architecture.md` |
| Phát JWT, đăng nhập, đăng ký, đổi mật khẩu | Developer | Lê Sơn Trường | `Modules/Identity/` |
| Middleware nạp quyền, ba loại claim | Developer | Nguyễn Huy Kiên | `PrincipalEnrichmentMiddleware` |
| Policy `[RequirePermission]`, `[RequireGlobalPermission]` | Developer | Nguyễn Huy Kiên | `Authorization/PermissionPolicy.cs` |
| Bảng cấp quyền project, API thành viên | Developer | Lê Sơn Trường | `ProjectMemberModule` |
| Danh mục project cho khách tự nhận | Developer | Lê Sơn Trường | `ProjectCatalogModule` |
| Rate limit, sanitize, idempotency | Developer | Nguyễn Huy Kiên | Middleware |
| Test cách ly + test canh gác endpoint | Tester | Nguyễn Huy Kiên | `ProjectIsolationTests` |
| Test danh mục và thang cấp | Tester | Lê Sơn Trường | `ProjectCatalogTests`, `RoleHierarchyTests` |
| Duyệt mô hình quyền | Tech Lead · QA | Nguyễn Bảo Long | Ghi chú duyệt trong PR |

---

## 6. Tiêu chí chấp nhận

| # | Điều kiện | Cách kiểm |
|---|---|---|
| 1 | Nhân viên không chạm được project mình không thuộc về, **cả đọc lẫn ghi** | `ProjectIsolationTests` |
| 2 | Gỡ khỏi project → mất quyền ngay, không chờ token hết hạn | Test tích hợp |
| 3 | Manager có `user.read` ở một project **không** đọc được danh bạ toàn hệ thống | Test tích hợp |
| 4 | Khách chưa tự nhận project → `403` dù project đã mở cho vai trò | `ProjectCatalogTests` |
| 5 | Khách tự nhận project chưa mở → `404` | `ProjectCatalogTests` |
| 6 | Tự nhận xong vào được ngay, **cùng token cũ** | Test tích hợp |
| 7 | Đổi mật khẩu → phiên khác chết, phiên đang đổi sống | `ProfileAndPasswordTests` |
| 8 | Mọi endpoint tự khai phạm vi | Test canh gác |
| 9 | Nội dung có `<script>` không bao giờ thành HTML thực thi | Test sanitize |

---

## 7. Chỗ dễ sai

| Sai lầm | Hậu quả | Cách phát hiện |
|---|---|---|
| Bật cách ly mà **quên** `is_global` | Mọi phép kiểm rơi vào nhánh dự phòng; test vẫn xanh | Test xanh 100% ngay sau khi bật cách ly là **dấu hiệu hỏng**, không phải tin tốt |
| Endpoint quản trị không dùng `[RequireGlobalPermission]` | Manager đọc được toàn bộ danh bạ | Test canh gác + test riêng cho manager |
| Cấp `role-access` rồi tưởng khách vào được ngay | Thiếu vế thứ hai, khách vẫn `403` | Test khoá cả hai chiều |
| Áp luật giao-của-hai-vế cho vai trò nhân viên | Cả nhóm đứng ngoài cho tới khi từng người tự tick | Chỉ áp cho vai trò tự phục vụ |
| So mốc đổi mật khẩu bằng `<` | Phiên đăng nhập **cùng giây** lọt qua | Test lặp lại 3 lần liên tiếp |

---

## 8. Bàn giao

- [ ] Không endpoint nào thiếu khai báo phạm vi
- [ ] Test cách ly xanh và **đã kiểm bằng cách hoàn tác**
- [ ] Rate limit trả `429` đúng định dạng
- [ ] Test tuần 1–3 xanh
