# Báo cáo chạy test — Lê Sơn Trường

| | |
|---|---|
| Vai trò | Tester |
| Phạm vi | Bộ test tích hợp backend, diễn tập sao lưu/phục hồi, và UAT nhóm 6–10 |
| Bộ test | Do Nguyễn Bảo Long (QA) viết và bảo trì |
| Máy chạy | Windows 11, Docker Desktop, .NET 8.0.424, PostgreSQL 16 trong container |
| Ngày chạy | 07/09/2026 |

> Báo cáo này ghi **kết quả chạy**, không phải mã test. Script test thuộc về vai QA; việc của
> tester là chạy chúng trên máy thật, đối chiếu với thao tác tay, và ghi lại những gì thấy.

---

## 1. Kết quả tự động

| Bộ | Lệnh | Kết quả |
|---|---|---|
| Backend | `dotnet test` | 313/313 xanh |
| Migration trên database rỗng | `dotnet ef database update` vào DB mới tạo | 15 migration, 45 bảng, không lỗi |
| Tải | `infra/loadtest/run.sh` (k6) | Đạt ngưỡng NFR-PERF-01 |
| Tranh chấp | `infra/loadtest/run-contention.sh` | Không có lần chuyển trạng thái trùng lọt qua |
| Ba service | `docker compose up --build -d --wait` | web, api, db đều healthy |

---

## 2. Diễn tập sao lưu và phục hồi

| Bước | Kết quả |
|---|---|
| `infra/scripts/backup.sh` trên stack đang chạy | Tạo được bản dump, kích thước hợp lý |
| `docker compose down -v` — xoá sạch volume | Database biến mất như mong đợi |
| `infra/scripts/restore.sh` từ bản dump | Phục hồi đủ dữ liệu, API lên lại healthy |
| Đối chiếu số bản ghi trước/sau | Khớp hoàn toàn |

**Một điều đáng ghi.** `backup.sh` chỉ dump được container `db` trong compose. Khi triển khai
dùng PostgreSQL bên ngoài qua `DB_CONNECTION_STRING`, script này **không** chạm tới database
thật — nó sẽ lặng lẽ sao lưu một container rỗng. Đã báo Long; ghi vào `roadmap.md` phần còn nợ.

---

## 3. UAT nhóm 6–10 (thao tác tay)

| Nhóm | Nội dung | Kết quả |
|---|---|---|
| 6 | Vòng đời sự cố: chuyển trạng thái, chặn nhảy bậc và lùi | Đạt |
| 7 | Phản hồi khách hàng: gửi, phân loại, gắn vào sự cố, trả lời | Đạt |
| 8 | Real-time: hai tab cùng mở, sự kiện tới nơi không phải tải lại | Đạt |
| 9 | Vận hành: `/api/health/system`, `/metrics`, log có `correlationId` | Đạt |
| 10 | Cách ly dự án: không đọc được dữ liệu của project mình không thuộc | Đạt |

**Cách kiểm cách ly.** Lấy id một sự cố ở project khác rồi ghép vào slug mình có quyền. Trả `404`,
không phải `403` — đúng như tài liệu nói, vì `403` sẽ xác nhận sự cố đó có thật.

---

## 4. Phần sửa nội dung (tuần 6)

Kiểm chéo phần Nguyễn Huy Kiên làm — component lịch sử, hook giữ chỗ, client API.

| Việc | Kết quả |
|---|---|
| Nhãn "đã sửa bởi X" chỉ hiện khi bản ghi thật sự đã sửa | Đạt |
| Bảng lịch sử tải lười — chỉ gọi API khi bấm mở | Đạt, xem tab Network |
| Đóng/mở lại bảng lịch sử | Đạt — không gọi lại máy chủ |
| Sửa bình luận sự cố, xem lại nguyên văn cũ | Đạt |
| Đóng tab giữa chừng rồi mở lại bằng tài khoản khác | Đạt — chỗ giữ cũ hết hạn sau 2 phút, không kẹt |
| Mất mạng giữa lúc đang sửa | Đạt — giữ chỗ hỏng nhưng form vẫn gõ và lưu được |

---

## 5. Ghi nhận

**Một chỗ gợn, không phải lỗi.** Sau khi nhả chỗ giữ, phải chờ hết TTL thì cảnh báo bên tab kia
mới biến mất — nó chỉ cập nhật vào nhịp gia hạn kế tiếp, tức là chậm tối đa 45 giây. Không ảnh
hưởng tính đúng đắn vì `If-Match` mới là thứ quyết định, nhưng người dùng thấy một cảnh báo đã cũ.
Cùng nguyên nhân với ghi nhận của Kiên — muốn tức thì thì cần kênh đẩy.

**Không phát hiện lỗi chặn phát hành.**
