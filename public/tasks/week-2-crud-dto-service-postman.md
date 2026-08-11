# Tuần 2 — Nghiệp vụ sự cố và phản hồi

| | |
|---|---|
| Chủ đề | DTO · Service layer · State machine · Xoá mềm · Postman |
| Thời lượng | 120 phút tại lớp + hoàn thiện ngoài giờ |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Mốc bàn giao | Tag `week-2` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) mục 3, 4 |

---

## 1. Mục tiêu

Dựng hai dòng nghiệp vụ chính và **đặt quy tắc vào đúng chỗ**: quy tắc vòng đời nằm trong một
state machine riêng, không rải trong controller.

Kết thúc tuần, nhóm phải giải thích và chứng minh được:

- Vì sao entity không được trả thẳng ra API mà phải qua DTO.
- Vì sao controller **chỉ điều phối**, không chứa quy tắc nghiệp vụ.
- Vì sao xoá là xoá mềm và global query filter giải quyết vấn đề gì.
- Vì sao ngoài phạm vi quyền trả `404` chứ không `403`.

---

## 2. Hai vòng đời

### 2.1 Sự cố

```
Investigating → Mitigating → Resolved
```

| Quy tắc | Mã |
|---|---|
| Luôn sinh ra ở *Investigating*; biểu mẫu không nhận trạng thái | BR-BIZ-01 |
| Tiến đúng một bước; nhảy cóc → `409` kèm bước hợp lệ tiếp theo | BR-BIZ-02 |
| Người ghi nhận lấy từ tài khoản đăng nhập | BR-BIZ-04 |
| Bước sang *Resolved* đòi thêm quyền riêng | BR-BIZ-05 |

### 2.2 Phản hồi

```
New → Acknowledged → Responded
```

| Quy tắc | Mã |
|---|---|
| Gắn vào sự cố đã đóng → `409`, phản hồi giữ nguyên trạng thái | BR-BIZ-07 |
| Gắn vào sự cố là một hình thức tiếp nhận; không hạ cấp *Responded* | BR-BIZ-12 |
| Phản hồi và sự cố **cùng project** | BR-BIZ-13 |

---

## 3. Kiến trúc tầng

```
Controller  →  Service  →  DbContext
   điều phối    quy tắc      truy vấn
```

| Tầng | Được làm | Không được làm |
|---|---|---|
| Controller | Nhận request, gọi service, trả DTO | Chứa `if` nghiệp vụ, truy vấn trực tiếp |
| Service | Quy tắc, transaction, ghi audit | Biết về HTTP |
| State machine | Bước chuyển hợp lệ | Truy cập cơ sở dữ liệu |

---

## 4. Phân công

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Chốt hai vòng đời, mã lỗi, thông điệp | Owner · BA | Nguyễn Bảo Long | Mục 4 của `Architecture.md` |
| Entity + migration cho sự cố và lịch sử trạng thái | Developer | Nguyễn Huy Kiên | `Domain/Entities.cs`, migration |
| State machine + service sự cố | Developer | Nguyễn Huy Kiên | `IncidentStateMachine`, `IncidentService` |
| Module phản hồi, gắn phản hồi vào sự cố | Developer | Lê Sơn Trường | `Modules/Feedbacks/` |
| DTO, validation, hợp đồng lỗi `problem+json` | Developer | Lê Sơn Trường | `IncidentDtos.cs`, `AppException` |
| Bộ sưu tập Postman | Tester | Nguyễn Huy Kiên | `infra/postman/` |
| Test vòng đời và ranh giới đọc | Tester | Lê Sơn Trường | `IncidentLifecycleTests`, `OwnershipAndCustomerTests` |
| Duyệt hợp đồng API | Tech Lead · QA | Nguyễn Bảo Long | Ghi chú duyệt trong PR |

---

## 5. Tiêu chí chấp nhận

| # | Điều kiện | Cách kiểm |
|---|---|---|
| 1 | Tạo sự cố với `status` trong body — giá trị bị bỏ qua | Test tích hợp |
| 2 | Nhảy từ *Investigating* thẳng sang *Resolved* → `409` kèm `allowedNextStatus` | Test tích hợp |
| 3 | Xoá sự cố → biến khỏi mọi truy vấn, hàng vẫn còn trong bảng | Test + truy vấn thẳng |
| 4 | Sự cố ngoài phạm vi quyền → `404`, không `403` | Test tích hợp |
| 5 | Gắn phản hồi vào sự cố đã đóng → `409`, trạng thái phản hồi không đổi | Test tích hợp |
| 6 | Người không có quyền đọc toàn bộ chỉ thấy phản hồi của mình | Test tích hợp |
| 7 | Mọi lỗi trả `application/problem+json` kèm `correlationId` | `ErrorContractTests` |
| 8 | Bộ sưu tập Postman chạy sạch bằng `newman` | Chạy CI |

---

## 6. Chỗ dễ sai

| Sai lầm | Hậu quả | Cách tránh |
|---|---|---|
| Đặt quy tắc chuyển trạng thái trong controller | Không unit test được, và mỗi endpoint mới lại chép lại | State machine riêng |
| Trả `403` cho bản ghi ngoài phạm vi | Lộ thông tin bản ghi đó có thật | Trả `404` |
| Mượn "đọc được sự cố" làm điều kiện gắn phản hồi | Khi quyền đọc mở rộng, ai cũng gắn được phản hồi vào sự cố người khác | Nêu điều kiện thật: của mình hoặc được giao cho mình |
| Lọc quyền **sau** khi phân trang | Tổng số đã đếm cả phần không được xem | Lọc ngay trong câu truy vấn |

---

## 7. Bàn giao

- [ ] Hai vòng đời chạy đúng, có test cho từng bước chuyển
- [ ] Xoá mềm không rò rỉ qua bất kỳ endpoint nào
- [ ] Postman xanh
- [ ] Test tuần 1–2 xanh
