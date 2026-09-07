# Báo cáo chạy test — Nguyễn Huy Kiên

| | |
|---|---|
| Vai trò | Tester |
| Phạm vi | Bộ test tích hợp backend, test web, và UAT nhóm 1–5 |
| Bộ test | Do Nguyễn Bảo Long (QA) viết và bảo trì |
| Máy chạy | Windows 11, Docker Desktop, .NET 8.0.424, Node 22 |
| Ngày chạy | 07/09/2026 |

> Báo cáo này ghi **kết quả chạy**, không phải mã test. Script test thuộc về vai QA; việc của
> tester là chạy chúng trên máy thật, đối chiếu với thao tác tay, và ghi lại những gì thấy —
> kể cả những thứ test xanh nhưng dùng thì vẫn gợn.

---

## 1. Kết quả tự động

| Bộ | Lệnh | Kết quả |
|---|---|---|
| Backend | `dotnet test` | 313/313 xanh, 2 phút 05 |
| Frontend | `npm test` | 108/108 xanh, 7,5 giây |
| Kiểu | `npm run typecheck` | Sạch |
| i18n | `npm run i18n:missing` · `i18n:stale` | 0/667 thiếu, 0 thừa |
| Ba service | `docker compose up --build -d --wait` | web, api, db đều healthy |

Chạy lại lần hai trên volume trắng (`docker compose down -v`) cho cùng kết quả — không có test
nào phụ thuộc trạng thái còn sót lại từ lần chạy trước.

---

## 2. UAT nhóm 1–5 (thao tác tay)

| Nhóm | Nội dung | Kết quả |
|---|---|---|
| 1 | Đăng ký, đăng nhập, đổi mật khẩu, hết hạn phiên | Đạt |
| 2 | RBAC: tạo quyền, tạo vai trò, gán quyền qua giao diện | Đạt |
| 3 | Thang cấp vai trò — không tự nâng mình lên admin | Đạt |
| 4 | Tổ chức dự án: project, label, milestone, board, template | Đạt |
| 5 | Quan hệ sub-issue và phụ thuộc, tìm kiếm theo Query DSL | Đạt |

**Cách kiểm thang cấp.** Đăng nhập bằng tài khoản `manager`, thử gán vai trò `admin` cho chính
mình qua giao diện và qua lời gọi API trực tiếp. Cả hai đều trả `403` — giao diện không bày ra
lựa chọn đó, và API cũng không nhận dù gửi thẳng.

---

## 3. Phần sửa nội dung (tuần 6)

Kiểm chéo phần Lê Sơn Trường làm — sửa phản hồi, câu trả lời và màn hình.

| Việc | Kết quả |
|---|---|
| Support sửa nội dung phản hồi của khách | Đạt — lịch sử ghi nguyên văn cũ, có nhãn "sửa hộ" |
| Khách hàng sửa phản hồi của chính mình | Đạt — lịch sử có chữ ký, không có nhãn "sửa hộ" |
| Kỹ thuật viên (chỉ có `feedback.read.all`) thử sửa | Đạt — `403`, đúng thông điệp |
| Sửa lời xác nhận tự động | Đạt — `409`, không sửa được kể cả bằng admin |
| Hai tab cùng mở form sửa một phản hồi | Đạt — cả hai tab hiện cảnh báo tên người kia |
| Tab cũ bấm Lưu sau khi tab kia đã lưu | Đạt — `412`, trang tự tải lại bản mới |

---

## 4. Ghi nhận

**Một chỗ gợn, không phải lỗi.** Khi hai người cùng mở form, dòng cảnh báo chỉ xuất hiện **sau**
nhịp giữ chỗ đầu tiên — tức là ngay sau khi bấm "Sửa" thì chưa thấy gì, khoảng một nhịp mạng sau
mới hiện. Không sai, nhưng người gõ nhanh có thể đã viết được vài chữ trước khi biết mình đang
giẫm chân người khác. Đã trao đổi với Long; kết luận là chấp nhận được vì lần lưu vẫn được `If-Match`
chặn, và việc bày cảnh báo sớm hơn đòi một kênh đẩy (SignalR) — ghi vào `roadmap.md`.

**Không phát hiện lỗi chặn phát hành.** Không có mục nào phải mở lại.
