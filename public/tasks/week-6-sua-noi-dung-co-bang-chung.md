# Tuần 6 — Sửa được nội dung mà vẫn giữ bằng chứng

| | |
|---|---|
| Chủ đề | Lịch sử sửa có chữ ký · Chỗ giữ sửa · `If-Match` có ngưỡng |
| Thời lượng | Ngoài giờ, song song với nghiệm thu và demo |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Mốc bàn giao | Tag `v1.0` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) · [`public/docs/decisions.md`](../docs/decisions.md) mục 2.3–2.4 |

> Tuần 6 có hai mạch việc song song: nghiệm thu và bàn giao ở
> [`week-6-uat-demo-submission.md`](week-6-uat-demo-submission.md), và tính năng sửa nội dung ở
> tài liệu này.

---

## 1. Vấn đề

Bản đầu cấm sửa gần như mọi thứ. Sự cố tạo xong là cố định tiêu đề, mô tả, mức độ; bình luận sự
cố, phản hồi khách hàng và câu trả lời đều chỉ ghi thêm.

Điều đó **bảo vệ được bằng chứng, bằng cách biến mọi lỗi chính tả thành vĩnh viễn**. Và người
dùng luôn tìm ra đường vòng: họ gửi thêm một bản ghi mới nói "bản trên sai". Kết quả là dữ liệu
có hai bản ghi mâu thuẫn, không bản nào nói rõ bản kia sai chỗ nào — đúng thứ hỏng hóc mà lệnh
cấm sinh ra để chặn.

Câu hỏi không phải "có nên cho sửa không" mà là **"cho sửa với cái giá nào"**.

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

Ranh giới sở hữu áp cho cả tuần này: **lõi dự án, cấu hình, script deploy, script test và toàn bộ
tài liệu thuộc về Long**; Kiên và Trường phát triển tính năng trên nền đó. Vai tester của hai
người là **chạy** bộ test của Long trên máy thật và ghi lại kết quả — script test là sản phẩm của
vai QA, báo cáo chạy test là sản phẩm của vai tester.

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Thiết kế lịch sử sửa và chỗ giữ; quyết định cho sửa gì, ai sửa | Tech Lead · BA | Nguyễn Bảo Long | `Modules/Revisions/`, `ResourceAuthorization.cs` |
| Lược đồ dữ liệu, migration và sửa nội dung sự cố | Dev lõi | Nguyễn Bảo Long | `Entities.cs`, `AppDbContext.cs`, `Persistence/Migrations/`, `IncidentService.cs` |
| `If-Match` có ngưỡng trong `EntityTags` | Dev lõi | Nguyễn Bảo Long | `Modules/Tickets/Infrastructure/WebPrimitives.cs` |
| Đăng ký service, cấu hình TTL và đường chỉnh qua `.env` | Dev lõi | Nguyễn Bảo Long | `Program.cs`, `appsettings.json`, `docker-compose.yml`, `.env.example` |
| Bộ test tích hợp cho lịch sử sửa và `If-Match` | QA | Nguyễn Bảo Long | `ContentRevisionTests.cs` (12), `EditClaimTests.cs` (13) |
| Tài liệu tuần và quyết định cài đặt | BA · QA | Nguyễn Bảo Long | Tài liệu này, `decisions.md` 2.3–2.4, `README`, `SECURITY` |
| Component lịch sử sửa, cảnh báo người cùng sửa, hook giữ chỗ | Developer | Nguyễn Huy Kiên | `components/EditTrail.tsx`, `ActiveEditorsNotice.tsx`, `lib/useEditClaim.ts` |
| Client API, bản dịch English, rào xoá tài khoản | Developer | Nguyễn Huy Kiên | `lib/api.ts`, `lib/messages/en.ts`, `RbacService.cs` |
| Sửa phản hồi, câu trả lời và bình luận sự cố | Developer | Lê Sơn Trường | `FeedbackModule.cs`, `IncidentCommentModule.cs` |
| Màn hình sửa nội dung sự cố và phản hồi | Developer | Lê Sơn Trường | `app/projects/`, `app/globals.css` |
| Chạy bộ test và UAT, ghi báo cáo | Tester | Nguyễn Huy Kiên | [`test-report-kien.md`](../docs/test-report-kien.md) |
| Chạy bộ test và UAT, ghi báo cáo | Tester | Lê Sơn Trường | [`test-report-truong.md`](../docs/test-report-truong.md) |
| Rà soát và nghiệm thu | QA | Nguyễn Bảo Long | Ghi chú duyệt |

**Kiểm chéo.** Kiên kiểm phần Trường làm, Trường kiểm phần Kiên làm — không ai tự nghiệm thu phần
mình viết.

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

- [x] Migration `ContentRevisions` và `EditClaims` chạy sạch trên database rỗng và trên DB test
- [x] Migration chạy sạch trên DB production, có bản sao lưu trước khi chạy
- [x] 25 test tích hợp mới xanh; toàn bộ 313 test backend xanh
- [x] Frontend typecheck sạch, 108 test xanh, i18n đủ hai ngôn ngữ
- [x] `decisions.md` ghi rõ quyết định và **cái giá phải trả** của cả hai mục 2.3 và 2.4
- [ ] Nghiệm thu chéo giữa Kiên và Trường
- [ ] Tag `v1.0` trỏ đúng commit đã nghiệm thu
