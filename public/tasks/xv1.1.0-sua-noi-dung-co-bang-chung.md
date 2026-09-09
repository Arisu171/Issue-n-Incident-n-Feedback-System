# Bản cập nhật 1.1.0 — Sửa được nội dung mà vẫn giữ bằng chứng

| | |
|---|---|
| Chủ đề | Lịch sử sửa có chữ ký · Chỗ giữ sửa · `If-Match` có ngưỡng |
| Loại | Bản cập nhật tính năng, tương thích ngược |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Mốc phát hành | `xv1.1.0`, nối tiếp `v1.0` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) · [`public/docs/decisions.md`](../docs/decisions.md) mục 2.3–2.4 |

> **Từ bản này trở đi dự án đánh số theo _bản cập nhật_, không theo tuần học.** Sáu tuần đầu đã
> khép lại ở `v1.0`; những gì đến sau là thay đổi trên một sản phẩm đang chạy, và tuần là đơn vị
> của lớp học chứ không phải của sản phẩm. Tài liệu tuần 1–6 giữ nguyên tên, không đổi hồi tố —
> đổi tên một lịch sử đã phát hành không làm nó đúng hơn, chỉ làm mọi đường dẫn trỏ vào nó gãy.

---

## 1. Vấn đề

Bản đầu cấm sửa gần như mọi thứ. Sự cố tạo xong là cố định tiêu đề, mô tả, mức độ; bình luận sự
cố, phản hồi khách hàng và câu trả lời đều chỉ ghi thêm.

Điều đó **bảo vệ được bằng chứng, bằng cách biến mọi lỗi chính tả thành vĩnh viễn**. Và người
dùng luôn tìm ra đường vòng: họ gửi thêm một bản ghi mới nói "bản trên sai". Kết quả là dữ liệu
có hai bản ghi mâu thuẫn, không bản nào nói rõ bản kia sai chỗ nào — đúng thứ hỏng hóc mà lệnh
cấm sinh ra để chặn.

Câu hỏi của bản cập nhật này không phải "có nên cho sửa không" mà là **"cho sửa với cái giá nào"**.

---

## 2. Phạm vi

| Thực thể | Trường sửa được | Ai được sửa |
|---|---|---|
| Sự cố | `title`, `description`, `severity` | Người báo cáo · `incident.manage_any` |
| Bình luận sự cố | `body` | Tác giả bình luận · `incident.manage_any` |
| Phản hồi khách hàng | `content`, `customerEmail`, `channel` | Người gửi · `feedback.respond` |
| Câu trả lời | `body` | Người viết · `feedback.respond` |

Luật giữ đúng hình dạng module Ticket đã dùng từ đầu — **tác giả, hoặc người có quyền cấp cao của
chính module đó** — thay vì nghĩ ra một luật thứ hai. Hai luật cho cùng một câu hỏi là cách chắc
chắn để chúng lệch nhau.

**Cố ý nằm ngoài phạm vi.** Ba thứ dưới đây có đường đi riêng với ràng buộc riêng; mở thêm một cửa
tới chúng ở đây là bỏ qua đúng những ràng buộc đó.

| Không sửa qua đường này | Vì |
|---|---|
| Trạng thái sự cố | Vòng đời một chiều đi qua `PATCH /status`, có lịch sử riêng (FR-BIZ-05) |
| Liên kết phản hồi ↔ sự cố | Đã có `POST`/`DELETE /link`, soát ràng buộc ở cả hai đầu (BR-BIZ-07) |
| Lời xác nhận tự động | Không có tác giả để đứng tên, và nó là bản sao đúng câu đã gửi cho khách |

---

## 3. Cái giá đi kèm

Ba điều được giữ cùng lúc, trong **một** transaction. Tách ra là có đường để nội dung đổi mà lịch
sử không kịp ghi, và một lịch sử có lỗ thì không còn là bằng chứng.

1. **Nội dung mới** trên chính bản ghi.
2. **Một dòng lịch sử cho mỗi trường đã đổi** trong `content_revisions` — giá trị cũ, giá trị mới,
   người sửa, thời điểm, và cờ `on_behalf` khi người sửa không phải chủ bản ghi.
3. **Chữ ký sửa lần cuối** đóng lên bản ghi, để nhãn "đã sửa bởi X" hiện được trên danh sách mà
   không phải join.

Bảng lịch sử **chỉ ghi thêm**: không endpoint nào sửa hay xoá được hàng của nó — cùng nguyên tắc
với `incident_status_history`.

---

## 4. Hai người cùng sửa

Mở form sửa là **giữ một chỗ** (`PUT …/edit-claim`, TTL 120 giây, gia hạn mỗi 45 giây). Chừng nào
còn **người khác** đang giữ chỗ chưa hết hạn, lần `PATCH` kế tiếp buộc phải mang `If-Match` đúng
phiên bản.

| Tình huống | Kết quả |
|---|---|
| Không ai khác giữ chỗ | `If-Match` tuỳ chọn — hành vi không đổi so với `v1.0` |
| Có người khác giữ chỗ, thiếu header | `428` kèm danh sách người đang sửa |
| Có người khác giữ chỗ, `If-Match: *` | `428` — `*` không nói gì về phiên bản |
| `If-Match` lệch phiên bản | `412` kèm phiên bản hiện tại |

**Không phải khoá.** Chỗ giữ không chặn ai; nó chỉ nâng điều kiện ghi lên thành "phải chứng minh
anh đang nhìn đúng bản mới nhất". Khoá cứng thì một tab quên đóng là bản ghi chết cứng, và luôn
phải kèm một nút "phá khoá" mà rốt cuộc ai cũng bấm.

---

## 5. Phân công

Giữ nguyên vai trò từ sáu tuần đầu. Long làm phần lõi và toàn bộ tài liệu; Kiên và Trường cùng vai
Developer, chia theo **bản đồ sở hữu file đã có** — một file chỉ thuộc về một nhánh, không bao giờ
hai, nên `WebPrimitives.cs` về Trường (thư mục `Modules/Tickets/Infrastructure` vốn của anh) dù nó
là hạ tầng dùng chung.

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Thiết kế lịch sử sửa và chỗ giữ; quyết định cho sửa gì, ai sửa | Tech Lead · BA | Nguyễn Bảo Long | `Modules/Revisions/`, `ResourceAuthorization.cs` |
| Lược đồ dữ liệu và sửa nội dung sự cố | Dev lõi | Nguyễn Bảo Long | `Entities.cs`, `AppDbContext.cs`, `IncidentService.cs` |
| Tài liệu bản cập nhật và quyết định cài đặt | BA · QA | Nguyễn Bảo Long | Tài liệu này, `decisions.md` 2.3–2.4, `README`, `SECURITY` |
| Giao diện lịch sử sửa, cảnh báo người cùng sửa, hook giữ chỗ | Developer | Nguyễn Huy Kiên | `components/EditTrail.tsx`, `ActiveEditorsNotice.tsx`, `lib/useEditClaim.ts` |
| Client API, bản dịch English, rào xoá tài khoản | Developer | Nguyễn Huy Kiên | `lib/api.ts`, `lib/messages/en.ts`, `RbacService.cs` |
| Migration, `If-Match` trong `EntityTags`, cấu hình khởi động | Developer | Lê Sơn Trường | `Persistence/Migrations/`, `WebPrimitives.cs`, `Program.cs` |
| Sửa phản hồi, câu trả lời, bình luận sự cố và các màn hình | Developer | Lê Sơn Trường | `FeedbackModule.cs`, `IncidentCommentModule.cs`, `app/projects/` |
| Test tích hợp — nửa nghiệp vụ | Tester | Nguyễn Huy Kiên | `ContentRevisionTests.cs` (12 test) |
| Test tích hợp — nửa đồng thời | Tester | Lê Sơn Trường | `EditClaimTests.cs` (13 test) |
| Rà soát và nghiệm thu bản cập nhật | QA | Nguyễn Bảo Long | Ghi chú duyệt |

**Kiểm chéo.** Kiên kiểm phần Trường làm, Trường kiểm phần Kiên làm — không ai tự nghiệm thu phần
mình viết, giữ nguyên nguyên tắc từ tuần 6.

---

## 6. Tiêu chí chấp nhận

| # | Điều kiện |
|---|---|
| 1 | Mọi lần sửa để lại dòng lịch sử kèm chữ ký, **kể cả** khi tác giả sửa bài mình |
| 2 | Sửa bài người khác thì dòng lịch sử mang cờ `on_behalf` |
| 3 | Gửi đúng giá trị cũ thì **không** sinh dòng lịch sử nào |
| 4 | `/revisions` chịu đúng ràng buộc đọc của bản ghi cha — không có đường vòng đọc giá trị cũ |
| 5 | Không ai sửa được nội dung qua đường không có quyền; giữ chỗ đòi đúng quyền như đường ghi |
| 6 | Có người khác giữ chỗ → thiếu `If-Match` trả `428`, lệch phiên bản trả `412`, `*` không thay được |
| 7 | Chỗ giữ hết hạn thì hết hiệu lực, không cần tiến trình nào chạy đúng giờ |
| 8 | Trạng thái sự cố không đổi được qua đường sửa nội dung |
| 9 | Test backend và frontend xanh; migration chạy sạch trên cả DB test và DB production |

---

## 7. Chỗ dễ sai

| Sai lầm | Hậu quả | Cách tránh |
|---|---|---|
| Ghi lịch sử ngoài transaction đổi nội dung | Nội dung đổi mà lịch sử không kịp ghi — lịch sử có lỗ | Một cửa duy nhất `EditDraft.Record()`, người gọi tự `SaveChangesAsync` trong cùng transaction |
| Đọc phiên bản trước khi khoá hàng | `If-Match` so với bản chụp đã cũ; phép kiểm thành trang trí | Khoá `FOR UPDATE` rồi mới soát |
| Cho `feedback.read.all` kèm luôn quyền sửa | Kỹ thuật viên viết lại lời khách hàng | Quyền sửa khoá theo `feedback.respond` |
| Mở `/revisions` rộng hơn bản ghi cha | Giá trị cũ của `content`/`customer_email` là PII đầy đủ, rò nguyên vẹn | Kiểm quyền đọc bản ghi cha trước, ở mọi endpoint lịch sử |
| Cho giữ chỗ mà không đòi quyền sửa | Ai đọc được cũng ép cả đội gửi `If-Match` — quấy rối không tốn gì | Đường giữ chỗ và đường ghi dùng chung một phép kiểm |
| Bump phiên bản khi đổi trạng thái | `412` giả trên form không hề bị ảnh hưởng | `Version` chỉ tăng khi **nội dung** đổi |

---

## 8. Bàn giao

- [x] Migration `ContentRevisions` và `EditClaims` chạy sạch trên DB test
- [x] Migration chạy sạch trên DB production, có bản sao lưu trước khi chạy
- [x] 25 test tích hợp mới xanh; toàn bộ 313 test backend xanh
- [x] Frontend typecheck sạch, 108 test xanh, i18n đủ hai ngôn ngữ
- [x] `decisions.md` ghi rõ quyết định và **cái giá phải trả** của cả hai mục 2.3 và 2.4
- [ ] Nghiệm thu chéo giữa Kiên và Trường
- [ ] Tag `xv1.1.0` trỏ đúng commit đã nghiệm thu
