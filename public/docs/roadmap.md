# Roadmap

> Tách bạch **đã làm** và **còn nợ**. Mục nào là đề xuất thì ghi rõ là đề xuất — không ghi thành
> đã xong.

| | |
|---|---|
| Người chịu trách nhiệm | Nguyễn Bảo Long (Owner) |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) |

---

## 1. Đã hoàn thành

### 1.1 Phân quyền

- Vai trò theo project: `project_members` cho nhân viên, `project_role_access` cho khách hàng.
- Chỉ `system` và `admin` có quyền toàn cục.
- Vai trò `system` (cấp 120) giữ chính sách SLA và bộ loại issue.
- Vai trò `manager` (cấp 80) điều phối trong project được cấp.
- Khách hàng tự nhận project ở tab **Projects**; quyền là giao của hai vế.
- Thang cấp vai trò: không tác động lên người ngang hoặc cao cấp hơn.
- Nhóm endpoint quản trị đòi quyền toàn cục.

### 1.2 Dữ liệu và nghiệp vụ

- Sự cố và phản hồi cách ly theo project; mục ảo `uncategorized` cho dữ liệu chưa phân loại.
- Chuyển sự cố sang project khác, phản hồi đã gắn đi theo.
- Đổi được sự cố đã gắn cho một phản hồi.
- Ghi chú nội bộ tách sang quyền riêng, nên khách xem được sự cố mà không thấy ghi chú.
- Tìm kiếm ngay trong danh sách sự cố, phản hồi, hộp thư, board.

### 1.3 Tài khoản

- Đăng nhập bằng email hoặc username.
- Trang hồ sơ, ảnh đại diện, công tắc hiện/ẩn từng trường.
- Đổi mật khẩu giết mọi phiên khác.
- Trạng thái hiện diện, `invisible` chặn ở server.

### 1.4 Giao diện

- Thông báo kết quả dạng thẻ nổi; lỗi không tự tắt.
- Cập nhật lạc quan cho reaction.
- Công tắc ẩn dấu vết hệ thống trên timeline.
- Một khuôn tiêu đề cho mọi trang, kicker bắt buộc.
- Điều khiển cùng hàng cao bằng nhau; tiêu đề cột không xuống dòng.
- Chọn project không kéo người dùng ra khỏi màn hình đang xem.

### 1.5 Vận hành

- Ảnh và tệp phục vụ qua URL ký HMAC.
- Log SQL mặc định tắt.
- Seeder tự cấp quyền project cho dữ liệu demo.

---

## 2. Còn nợ — theo thứ tự ưu tiên

### 2.1 Thu hồi token theo `jti`

`jti` được sinh ra nhưng **không nơi nào kiểm**. Một token bị lộ dùng được tới khi hết hạn, và cách
duy nhất cắt nó là vô hiệu hoá tài khoản. Đây là lý do tuổi thọ token không được nới.

*Hướng làm:* bảng denylist theo `jti`, TTL bằng tuổi thọ token, kiểm trong middleware nạp quyền
(cùng chỗ đã kiểm mốc đổi mật khẩu).

### 2.2 Trạng thái hiện diện dùng chung nhiều instance

`PresenceTracker` nằm trong bộ nhớ tiến trình. Chạy nhiều instance thì mỗi instance chỉ biết kết
nối của chính nó.

*Hướng làm:* chuyển sang Redis. Chỉ phải thay đúng lớp đó.

### 2.3 Dữ liệu demo trên cơ sở dữ liệu trắng

Hai bước di trú cấp quyền chạy **trước** seeder, nên trên máy mới chúng không có project nào để
cấp. Seeder đã tự cấp cho project nó tạo, nhưng nếu bật demo sau khi đã có dữ liệu thật thì tình
huống này chưa được kiểm.

*Hướng làm:* kiểm thêm kịch bản bật demo trên cơ sở dữ liệu đã có dữ liệu.

### 2.4 `WatchLevel.Participating` và `Custom`

Chưa nơi nào đọc. `Participating` hiện đúng một cách **tình cờ** — vì không có nhánh nào thêm người
theo dõi toàn project, nên còn lại đúng là "chỉ khi tôi có liên quan". `Custom` chưa có ý nghĩa nào.

### 2.5 Test hợp đồng ba trạng thái so chuỗi

Phép so hiện tại tìm điểm kết `);` cuối dòng, nên một lời gọi kết thúc giữa dòng có thể nuốt phần
mã phía sau. Siết chặt làm lộ vài chỗ truyền thông báo dạng biến hoặc biểu thức điều kiện — đều là
thông báo thật.

*Hướng làm:* phân tích cú pháp thay vì so chuỗi.

### 2.6 Ref tới ticket không tồn tại

`#N` vẫn thành link kể cả khi ticket chưa có. Bấm vào dẫn tới màn hình giải thích rõ, nên hậu quả
đã hết — nhưng link vẫn là link chết.

*Hướng làm:* truyền danh sách số ticket có thật vào bộ render, giống cách đã làm với `@login`.

### 2.7 Duyệt giao diện tự động

Không có công cụ chụp màn hình trong quy trình. Mọi kết luận về giao diện dựa trên test, CSS được
phục vụ, và route trả `200`.

---

## 3. Đề xuất — chưa cam kết

| Đề xuất | Lý do cân nhắc |
|---|---|
| Rút gọn URL ảnh có hạn dùng + viết lại lúc render | Chữ ký hiện không hết hạn; siết được nhưng phải xử lý URL đã nằm trong bình luận cũ |
| Gộp `label.write` và `milestone.write` | Hai quyền luôn đi cùng nhau trong mọi vai trò dựng sẵn |
| Một reaction mỗi người | Đổi luật nghiệp vụ, cần siết ràng buộc DB và di trú dữ liệu đang có |
| Xuất báo cáo SLA | Chưa có yêu cầu cụ thể từ phía nghiệp vụ |
