# Checklist UAT

| | |
|---|---|
| Mục đích | Nghiệm thu theo vai trò trước khi bàn giao |
| Kèm theo | [`demo.md`](demo.md) · [`verification.md`](verification.md) |
| Người chủ trì | Nguyễn Bảo Long (QA) |
| Người thực hiện | Nguyễn Huy Kiên, Lê Sơn Trường (chéo phần của nhau) |

**Cách dùng.** Mỗi dòng là một việc làm được hoặc không. Không đánh dấu đạt nếu chỉ "chắc là chạy".
Chạy trên stack sạch: `docker compose down -v && docker compose up --build -d --wait`.

Tài khoản demo: mật khẩu chung `Demo#12345` — `bao.long@demo.local` (admin), `thu.ha@demo.local`
(responder), `gia.bao@demo.local` (support), `anh.tu@demo.local` (customer).

---

## 1. Truy cập và phân quyền

| # | Việc | Đạt |
|---|---|:---:|
| 1.1 | Đăng nhập bằng email **hoặc** username | ☐ |
| 1.2 | Tự đăng ký tạo được tài khoản vai trò `customer` | ☐ |
| 1.3 | Gửi kèm `roles: ["admin"]` khi đăng ký — vai trò bị bỏ qua | ☐ |
| 1.4 | Đổi mật khẩu: phiên khác bị đăng xuất, phiên đang thao tác vẫn dùng được | ☐ |
| 1.5 | Support không mở được dự án chưa được cấp | ☐ |
| 1.6 | Manager thêm được thành viên nhưng không mở được danh bạ toàn hệ thống | ☐ |
| 1.7 | Manager không cấp được vai trò ngang hoặc cao hơn cấp của mình | ☐ |
| 1.8 | admin thường không tạo được issue type; tài khoản `system` thì được | ☐ |

## 2. Khách hàng tự chọn dự án

| # | Việc | Đạt |
|---|---|:---:|
| 2.1 | Tab **Projects** hiện với mọi tài khoản | ☐ |
| 2.2 | Khách thấy danh mục dự án đã mở cho vai trò mình, kèm cờ đã tham gia | ☐ |
| 2.3 | Dự án chưa mở **không** xuất hiện trong danh mục | ☐ |
| 2.4 | Bật công tắc tham gia → vào được ngay, không phải đăng nhập lại | ☐ |
| 2.5 | Tắt công tắc → mất quyền xem, nhưng dữ liệu đã gửi vẫn còn khi bật lại | ☐ |
| 2.6 | Ô chọn dự án trên thanh trên cập nhật ngay sau khi bật/tắt | ☐ |

## 3. Sự cố

| # | Việc | Đạt |
|---|---|:---:|
| 3.1 | Ghi nhận sự cố → luôn ở *Đang điều tra* | ☐ |
| 3.2 | Nhảy cóc trạng thái bị từ chối, kèm bước hợp lệ tiếp theo | ☐ |
| 3.3 | Người không có quyền giải quyết không đóng được sự cố | ☐ |
| 3.4 | Ô chọn người xử lý chỉ liệt kê người thật sự nhận được việc | ☐ |
| 3.5 | Khách hàng không xuất hiện trong danh sách người nhận việc | ☐ |
| 3.6 | Xoá sự cố là xoá mềm — biến khỏi mọi danh sách | ☐ |
| 3.7 | Ghép id sự cố của dự án khác vào URL → `404` | ☐ |
| 3.8 | Ô tìm kiếm lọc theo tiêu đề và mô tả, tổng số cập nhật theo | ☐ |
| 3.9 | Bộ lọc **Breached** chỉ trả sự cố quá hạn | ☐ |

## 4. Phản hồi

| # | Việc | Đạt |
|---|---|:---:|
| 4.1 | Khách gửi phản hồi → nhận lời xác nhận tiếp nhận ngay | ☐ |
| 4.2 | Gắn phản hồi vào sự cố → chuyển *Đã tiếp nhận* | ☐ |
| 4.3 | Gắn vào sự cố đã đóng → bị từ chối, phản hồi giữ nguyên trạng thái | ☐ |
| 4.4 | Đổi sang sự cố khác cùng dự án → liên kết đổi theo | ☐ |
| 4.5 | Khách không thấy phản hồi của khách khác | ☐ |
| 4.6 | Cột Dự án hiển thị tên dự án hiện tại, chuyển được sang dự án khác | ☐ |

## 5. Ticket và điều phối

| # | Việc | Đạt |
|---|---|:---:|
| 5.1 | Tạo ticket từ template, trường bắt buộc chặn được submit thiếu | ☐ |
| 5.2 | Gắn nhãn, mốc phát hành, người nhận việc | ☐ |
| 5.3 | Đóng kèm lý do; mở lại đặt lý do *reopened* | ☐ |
| 5.4 | Bình luận vào ticket đã đóng **không** tự mở lại | ☐ |
| 5.5 | Ticket bị khoá: khách không bình luận và không thả emoji được | ☐ |
| 5.6 | Ghim tối đa 3 ticket mỗi dự án | ☐ |
| 5.7 | Công tắc **Hoạt động** ẩn dấu vết hệ thống, giữ nguyên bình luận | ☐ |
| 5.8 | Thả emoji: số và nền đổi ngay, không nháy, không hiện dòng "reacted" | ☐ |
| 5.9 | Xoá cột board còn thẻ → bị chặn kèm số thẻ còn lại | ☐ |

## 6. Ghi chú nội bộ

| # | Việc | Đạt |
|---|---|:---:|
| 6.1 | Support ghi chú nội bộ, nhân viên khác thấy ngay | ☐ |
| 6.2 | Khách mở cùng ticket — **không** nhận byte nào của ghi chú | ☐ |
| 6.3 | Khách xem lịch sử sự cố: thấy dòng trạng thái, không thấy nội dung ghi chú | ☐ |

## 7. Thông báo

| # | Việc | Đạt |
|---|---|:---:|
| 7.1 | Có người trả lời → hộp thư hiện luồng mới | ☐ |
| 7.2 | Không nhận thông báo về việc do chính mình làm | ☐ |
| 7.3 | Tắt thông báo một dự án → không còn nhận | ☐ |
| 7.4 | Vẫn nhận khi có người gọi thẳng tên mình | ☐ |
| 7.5 | Ô tìm trong hộp thư lọc theo tiêu đề ticket | ☐ |

## 8. Tệp và nội dung

| # | Việc | Đạt |
|---|---|:---:|
| 8.1 | Tải ảnh đại diện lên và **hiển thị được** | ☐ |
| 8.2 | Chèn ảnh vào bình luận, ảnh hiện đúng | ☐ |
| 8.3 | Nhắc tên người có thật → thành link tới hồ sơ | ☐ |
| 8.4 | Nhắc tên người không có thật → vẫn là chữ thường | ☐ |
| 8.5 | Mở ticket không tồn tại → màn hình giải thích, có lối quay lại | ☐ |
| 8.6 | Mở hồ sơ không tồn tại → tương tự | ☐ |

## 9. Giao diện

| # | Việc | Đạt |
|---|---|:---:|
| 9.1 | Đổi sang English — **không** còn chữ Việt sót lại | ☐ |
| 9.2 | Đổi dự án ở thanh trên: giữ nguyên loại màn hình, không nhảy về Issues | ☐ |
| 9.3 | Dự án đang chọn hiển thị xuyên suốt, kể cả ở Search / Inbox / Boards | ☐ |
| 9.4 | Thông báo kết quả nổi góc dưới phải; lỗi không tự tắt | ☐ |
| 9.5 | Điều khiển trên cùng một hàng cao bằng nhau | ☐ |
| 9.6 | Tiêu đề cột bảng không xuống dòng | ☐ |
| 9.7 | Mọi trang có kicker màu accent trên tiêu đề | ☐ |
| 9.8 | Không có cú nháy trắng khi chuyển trang | ☐ |

## 10. Vận hành

| # | Việc | Đạt |
|---|---|:---:|
| 10.1 | `docker compose up --build -d --wait` từ máy sạch — ba service healthy | ☐ |
| 10.2 | `GET /api/health/system` trả `Healthy` | ☐ |
| 10.3 | `GET /metrics` trả định dạng Prometheus | ☐ |
| 10.4 | Log khởi động **không** đầy câu lệnh SQL | ☐ |
| 10.5 | Sao lưu và phục hồi chạy được | ☐ |

---

## Kết luận

| | |
|---|---|
| Ngày nghiệm thu | |
| Số mục đạt / tổng | |
| Mục không đạt và hướng xử lý | |
| Người chủ trì ký | |
