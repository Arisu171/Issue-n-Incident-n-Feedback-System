# Tuần 6 — Nghiệm thu, demo và bàn giao

| | |
|---|---|
| Chủ đề | UAT theo vai trò · Kịch bản demo · Tài liệu bàn giao |
| Thời lượng | 120 phút tại lớp + hoàn thiện ngoài giờ |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Mốc bàn giao | Tag `v1.0` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) mục 10 |

---

## 1. Mục tiêu

Chứng minh hệ thống dùng được, không phải chứng minh nó chạy được.

Kết thúc tuần, nhóm phải:

- Chạy UAT theo **vai trò**, không phải theo màn hình.
- Demo được trên máy sạch, không phụ thuộc trạng thái máy của người trình bày.
- Bàn giao tài liệu đủ để người ngoài dựng lại hệ thống mà không hỏi ai.

---

## 2. UAT

Checklist đầy đủ ở [`public/docs/uat.md`](../docs/uat.md), chia theo 10 nhóm.

**Nguyên tắc.** Mỗi dòng là một việc **làm được hoặc không**. Không đánh dấu đạt nếu chỉ "chắc là
chạy". Chạy trên stack sạch, không dùng dữ liệu đang có sẵn trên máy.

**Người kiểm chéo.** Kiên kiểm phần Trường làm; Trường kiểm phần Kiên làm. Không ai tự nghiệm thu
phần mình viết.

---

## 3. Demo

Kịch bản 18 phút ở [`public/docs/demo.md`](../docs/demo.md), có phân vai và mốc thời gian.

| Điều | Chuẩn bị |
|---|---|
| Bốn tab đăng nhập sẵn | admin, support, responder, customer |
| Dự phòng | Ảnh chụp `health/system`, bảng số đo k6 |
| Bản ghi màn hình | Đoạn ghi chú nội bộ — cần hai tab song song, dễ trục trặc nhất |
| Thời gian dựng lại | Khoảng 2 phút kể cả seed |

---

## 4. Bộ tài liệu bàn giao

| Tài liệu | Nội dung |
|---|---|
| [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) | Đặc tả **duy nhất** của hệ thống |
| [`public/docs/analysis.md`](../docs/analysis.md) | Nghiệp vụ cho người không đọc mã |
| [`public/docs/decisions.md`](../docs/decisions.md) | Vì sao làm theo cách này, cái giá phải trả |
| [`public/docs/verification.md`](../docs/verification.md) | Bằng chứng cho từng lời tự nhận |
| [`public/docs/uat.md`](../docs/uat.md) | Checklist nghiệm thu |
| [`public/docs/demo.md`](../docs/demo.md) | Kịch bản trình bày |
| [`public/docs/roadmap.md`](../docs/roadmap.md) | Đã làm và **còn nợ**, tách bạch |
| `README.md` | Dựng hệ thống từ con số 0 |

---

## 5. Phân công

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Chủ trì UAT, ký nghiệm thu | Owner · QA | Nguyễn Bảo Long | `public/docs/uat.md` đã điền |
| Viết kịch bản demo, dẫn buổi trình bày | Owner · BA | Nguyễn Bảo Long | `public/docs/demo.md` |
| Toàn bộ bộ test tích hợp và test web | QA | Nguyễn Bảo Long | `backend/tests/`, `frontend/tests/` |
| Chạy UAT nhóm 1–5, thao tác demo phần nghiệp vụ | Tester | Nguyễn Huy Kiên | [`test-report-kien.md`](../docs/test-report-kien.md) |
| Chạy UAT nhóm 6–10, thao tác demo phần vận hành | Tester | Lê Sơn Trường | [`test-report-truong.md`](../docs/test-report-truong.md) |
| Sửa lỗi phát hiện trong UAT | Developer | Kiên và Trường | PR kèm test |
| Rà soát tài liệu bàn giao | Tech Lead | Nguyễn Bảo Long | Ghi chú duyệt |

**Ranh giới sở hữu.** Lõi dự án, cấu hình, script deploy, script test và toàn bộ tài liệu thuộc về Long; Kiên và Trường phát triển tính năng trên nền đó. Vai tester của hai người là **chạy** bộ test ấy trên máy thật rồi ghi lại kết quả — viết script test là việc của vai QA.

---

## 6. Tiêu chí chấp nhận

| # | Điều kiện |
|---|---|
| 1 | Toàn bộ checklist UAT có kết quả; mục không đạt ghi rõ hướng xử lý |
| 2 | Demo chạy trọn vẹn trên máy sạch, không quá 20 phút |
| 3 | Test backend và frontend xanh trên CI |
| 4 | `README.md` dựng lại được hệ thống mà không hỏi ai |
| 5 | `roadmap.md` ghi đúng phần còn nợ, không ghi thành đã xong |
| 6 | Tag `v1.0` trỏ đúng commit đã nghiệm thu |

---

## 7. Chỗ dễ sai

| Sai lầm | Hậu quả | Cách tránh |
|---|---|---|
| Demo trên máy đã chạy nhiều ngày | Không phát hiện lỗi dựng từ đầu | Dựng lại từ volume trắng trước buổi |
| Tự nghiệm thu phần mình viết | Bỏ sót đúng chỗ mình không nghĩ tới | Kiểm chéo bắt buộc |
| Ghi "đã xong" cho phần chưa kiểm | Người tiếp nhận tin nhầm | `roadmap.md` tách bạch đã làm / còn nợ |
| Sửa lỗi UAT mà không thêm test | Lỗi quay lại ở lần sau | Mọi bản sửa kèm test đỏ trước |

---

## 8. Bàn giao cuối

- [ ] UAT có biên bản, có chữ ký người chủ trì
- [ ] Demo đã chạy thử ít nhất một lượt đầy đủ
- [ ] Bộ 8 tài liệu đầy đủ và trỏ đúng nhau
- [ ] CI xanh trên commit được tag
- [ ] Phần còn nợ đã ghi rõ, không giấu
