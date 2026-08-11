# Phân tích nghiệp vụ

| | |
|---|---|
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) |
| Phạm vi tài liệu này | Diễn giải nghiệp vụ cho người đọc không đọc mã |
| Người chịu trách nhiệm | Nguyễn Bảo Long (BA) |

---

## 1. Ai dùng hệ thống

| Vai trò | Họ cần gì | Họ **không** được làm gì |
|---|---|---|
| System | Đặt chính sách SLA và bộ loại issue cho cả hệ thống | — |
| Admin | Tạo tài khoản, phân vai trò, tạo và xoá dự án | Không đổi được cam kết SLA |
| Manager | Điều phối việc và thành viên trong dự án mình phụ trách | Không đụng tới bảng tài khoản toàn hệ thống |
| Support | Ghi nhận sự cố, trả lời phản hồi khách | Không tự nhận việc kỹ thuật |
| Responder | Điều tra, khắc phục, đóng sự cố | Không quản lý tài khoản |
| Khách hàng | Gửi sự cố / phản hồi, theo dõi tiến độ | Không thấy ghi chú nội bộ |

**Vì sao Manager không quản lý tài khoản.** Bảng tài khoản là toàn hệ thống. Cho manager của một
dự án quyền xoá tài khoản thì họ vô hiệu hoá được người đang làm ở dự án khác — leo thang đặc quyền
đi vòng.

---

## 2. Ba dòng công việc

### 2.1 Phản hồi khách hàng

```
Khách gửi phản hồi
   → hệ thống xác nhận tiếp nhận ngay (tự động)
   → Support đọc, gắn vào một sự cố nếu đã có
   → Support trả lời
   → phản hồi chuyển "Đã trả lời"
```

Phản hồi và sự cố nó gắn vào **luôn cùng một dự án**. Gắn nhầm thì đổi được sang sự cố khác.

### 2.2 Sự cố kỹ thuật

```
Đang điều tra → Đang khắc phục → Đã giải quyết
```

Ba điều được ép ở tầng nghiệp vụ:

1. Sự cố **luôn** sinh ra ở trạng thái *Đang điều tra*. Biểu mẫu không nhận trạng thái.
2. Trạng thái tiến **đúng một bước**. Nhảy cóc bị từ chối kèm câu trả lời "bước hợp lệ tiếp theo là gì".
3. Bước sang *Đã giải quyết* đòi thêm một quyền riêng — không phải ai chuyển được trạng thái cũng đóng được sự cố.

Người ghi nhận lấy từ tài khoản đăng nhập, không nhận từ biểu mẫu.

### 2.3 Công việc nội bộ (ticket)

Vòng đời mở/đóng kèm lý do, có nhãn, mốc phát hành, bảng công việc, phân rã sub-issue, và một dòng
thời gian ghi lại mọi thay đổi.

---

## 3. Cách ly theo dự án

Đây là quy tắc chi phối toàn hệ thống.

- Mọi dữ liệu nghiệp vụ thuộc về **đúng một dự án**, hoặc chưa phân loại.
- Chỉ `system` và `admin` có quyền toàn hệ thống. Mọi vai trò khác chỉ có quyền ở dự án được cấp.
- Nhân viên được cấp **theo từng người**; khách hàng được cấp **theo vai trò**, vì với hàng nghìn
  khách thì cấp từng người là việc không ai làm nổi.

### 3.1 Khách hàng phải tự nhận dự án

Quản trị mở dự án cho vai trò khách hàng — đó mới là **danh mục**. Khách hàng còn phải tự tick
"tôi có dùng dịch vụ này" ở tab **Projects**. Quyền thật là **giao** của hai vế.

Mô phỏng đúng đời thật: chỉ nêu được sự cố của dịch vụ mình đang dùng. Và vì là phép giao nên ô
tick không cấp thêm quyền cho ai — tự nhận một dự án chưa mở thì vẫn không vào được.

Bỏ tick là **thôi theo dõi**, không phải xoá dữ liệu: sự cố đã gửi vẫn còn, tick lại là thấy lại.

### 3.2 Dữ liệu chưa phân loại

Sự cố và phản hồi chưa gắn dự án nào nằm ở mục `uncategorized`. Người có quyền quản lý thành viên
dự án thấy mục này và chuyển dữ liệu ra dự án thật. Chuyển sự cố thì phản hồi đã gắn **đi theo** —
nếu không, mở phản hồi ở dự án cũ sẽ thấy nó trỏ sang một sự cố đã ở nơi khác.

---

## 4. Những gì khách hàng không bao giờ thấy

| Thứ | Cách chặn |
|---|---|
| Ghi chú nội bộ | Lọc ở **mọi** đầu ra: timeline, bình luận, tìm kiếm, thông báo, webhook, socket |
| Dữ liệu dự án không tham gia | Cắt ngay trong câu truy vấn, không lọc sau khi lấy về |
| Tên dự án chưa mở cho họ | Danh sách dự án và danh mục tự chọn đều đã lọc — tên dự án cũng là dữ liệu |
| Phản hồi của khách khác | Chỉ người có quyền đọc toàn bộ mới thấy; khách chỉ thấy phần mình gửi |

Khách hàng **thấy được mọi sự cố trong dự án mình tham gia** — nhưng không thấy ghi chú nội bộ trên
đó, vì ghi chú khoá bằng một quyền riêng.

---

## 5. Thông báo

- Đúng **một** luồng thông báo cho mỗi cặp (người dùng, ticket). Sự kiện mới cập nhật luồng đó.
- Không ai nhận thông báo về việc do chính mình làm.
- Tắt thông báo một dự án thì **im thật** — kể cả trên ticket mình đang tự động theo dõi.
- Ngoại lệ duy nhất: **có người gọi thẳng tên mình**. Nuốt luôn lời nhắc trực tiếp là cách chắc
  chắn để người ta bỏ lỡ thứ gửi đích danh cho họ, và không bao giờ biết là đã bỏ lỡ.

---

## 6. Cam kết thời gian (SLA)

Chính sách SLA đặt theo mức ưu tiên, do vai trò `system` quản lý. Hệ thống tự quét và:

- cảnh báo khi sắp vượt hạn,
- đánh dấu vượt hạn,
- leo thang cho quản lý.

Sự cố đã đóng thì đồng hồ dừng — không còn bị tính SLA.

---

## 7. Câu hỏi thường gặp khi đọc nghiệp vụ

**Vì sao một số thao tác trả 404 thay vì 403?**
Vì `403` đã xác nhận bản ghi đó có thật. Với dữ liệu ngoài phạm vi quyền, hệ thống trả `404`.

**Vì sao khách hàng thấy sự cố của khách khác?**
Trong cùng một dự án thì có. Phạm vi đã bị dự án cắt sẵn, và phần nhạy cảm — ghi chú nội bộ — nằm
sau một quyền riêng.

**Đổi mật khẩu có ảnh hưởng gì?**
Mọi phiên khác của tài khoản đó bị chấm dứt ngay. Phiên đang thao tác được giữ lại.
