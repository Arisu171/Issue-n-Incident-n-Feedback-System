# Kịch bản demo

| | |
|---|---|
| Thời lượng | 18 phút + 5 phút hỏi đáp |
| Chuẩn bị | Stack sạch, `SEED_DEMO_DATA=true`, đã đăng nhập sẵn 4 tab trình duyệt |
| Kèm theo | [`uat.md`](uat.md) · [`verification.md`](verification.md) |
| Người dẫn | Nguyễn Bảo Long |
| Người thao tác | Nguyễn Huy Kiên, Lê Sơn Trường |

**Bốn tab chuẩn bị sẵn**, mật khẩu chung `Demo#12345`:

| Tab | Tài khoản | Vai trò |
|---|---|---|
| 1 | `bao.long@demo.local` | admin |
| 2 | `gia.bao@demo.local` | support |
| 3 | `thu.ha@demo.local` | responder |
| 4 | `anh.tu@demo.local` | customer |

---

## Tiến trình

| Phút | Người | Nội dung | Chứng minh điều gì |
|---:|---|---|---|
| 0–1 | Long | Bài toán: ba dòng công việc ở ba hệ thống rời | Bối cảnh |
| 1–3 | Kiên | Tab 4 — khách mở tab **Projects**, bật công tắc tham gia một dự án, vào được ngay | Quyền là giao của hai vế; không phải đăng nhập lại |
| 3–4 | Kiên | Tab 4 — thử vào một dự án chưa mở: `403`; dự án đó không có trong danh mục | Không tự cấp quyền được; tên dự án cũng là dữ liệu |
| 4–6 | Kiên | Tab 4 — khách gửi phản hồi, nhận xác nhận tiếp nhận ngay | Luồng phản hồi |
| 6–8 | Trường | Tab 2 — support gắn phản hồi vào một sự cố, rồi **đổi sang sự cố khác** | Gắn nhầm sửa được |
| 8–10 | Trường | Tab 3 — responder chuyển trạng thái kèm ghi chú nội bộ. **Mở tab 4 song song** | Ghi chú nội bộ không tới khách |
| 10–11 | Trường | Tab 4 — khách xem lịch sử: thấy dòng trạng thái, **không** thấy nội dung ghi chú | Cô lập ở mọi đầu ra |
| 11–13 | Kiên | Tab 1 — mở `uncategorized`, chuyển một sự cố sang dự án thật | Phản hồi đã gắn đi theo |
| 13–15 | Trường | Tab 2 — thả emoji (số nhảy ngay, không nháy), bật/tắt công tắc **Hoạt động** | Cập nhật lạc quan; lọc dấu vết hệ thống |
| 15–16 | Kiên | Tab 1 — tắt thông báo một dự án, tab 3 bình luận, rồi bình luận **có gọi tên** | Tắt là im, trừ lời gọi đích danh |
| 16–17 | Long | `GET /api/health/system`, `GET /metrics` | Vận hành quan sát được |
| 17–18 | Long | Đổi ngôn ngữ sang English trên một màn hình bất kỳ | i18n không sót chữ |

---

## Câu hỏi dự kiến

| Câu hỏi | Trả lời ngắn |
|---|---|
| Khách hàng có thấy sự cố của khách khác không? | Có, trong cùng dự án — nhưng không thấy ghi chú nội bộ, vì ghi chú nằm sau một quyền riêng |
| Vì sao khách phải tự tick dự án? | Quyền là giao của hai vế: quản trị mở dự án, khách tự nhận. Mô phỏng "phải dùng dịch vụ mới nêu được sự cố" |
| Tại sao có vai trò `system` tách khỏi `admin`? | Đổi chính sách SLA là đổi cam kết dịch vụ của mọi dự án cùng lúc; thêm một quản trị viên là việc thường ngày |
| Dữ liệu chưa phân loại đi đâu? | Mục ảo `uncategorized`, chỉ người quản lý thành viên dự án thấy và phân loại được |
| Xoá dự án được không? | Chỉ dự án rỗng. Còn dữ liệu thì phải lưu trữ, không xoá |
| Có bao nhiêu test? | 281 backend trên PostgreSQL thật, 86 frontend |

---

## Chuẩn bị dự phòng

1. Chụp sẵn ảnh `health/system` và bảng số đo k6, phòng khi mạng hỏng giữa buổi.
2. Chuẩn bị một bản ghi màn hình 3 phút cho đoạn ghi chú nội bộ — đoạn cần hai tab chạy song song,
   dễ trục trặc nhất.
3. Nếu container chưa healthy: `docker compose down -v && docker compose up --build -d --wait`,
   mất khoảng 2 phút kể cả seed.
