# Tuần 4 — Giao diện Next.js

| | |
|---|---|
| Chủ đề | Next.js 16 App Router · Hợp đồng ba trạng thái · i18n · Hệ thống thiết kế |
| Thời lượng | 120 phút tại lớp + hoàn thiện ngoài giờ |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Mốc bàn giao | Tag `week-4` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) mục 7.5 |

---

## 1. Mục tiêu

Dựng giao diện cho toàn bộ nghiệp vụ, và **ép ba quy tắc bằng test** thay vì bằng nhắc nhở.

Kết thúc tuần, nhóm phải giải thích và chứng minh được:

- Vì sao mọi thao tác ghi phải có đủ ba trạng thái, và vì sao để mỗi màn hình tự lo là không đủ.
- Vì sao chuỗi hiển thị không được viết thẳng vào JSX.
- Vì sao giao diện **không** được là hàng rào bảo mật duy nhất.

---

## 2. Ba quy tắc bắt buộc

### 2.1 Hợp đồng ba trạng thái

Mọi thao tác ghi đi qua `useAction`, cho ra ba trạng thái: đang xử lý, thành công, thất bại.

**Vì sao không để mỗi màn hình tự lo.** Trước khi có hook này, 5 trên 9 thao tác thiếu ít nhất một
trạng thái — mỗi màn hình tự giữ một cặp `busy`/`notice` riêng và quên một cái là chuyện thường.

Kết quả hiện dưới dạng **thẻ nổi** góc dưới phải, không chèn vào luồng trang: thẻ chèn giữa nội
dung đẩy mọi thứ bên dưới xuống đúng lúc người dùng vừa bấm; và ở cuối một bảng dài thì nó nằm
ngoài tầm nhìn.

Thành công tự tắt sau 4 giây. **Lỗi thì không** — lỗi mang bước hợp lệ tiếp theo và `correlationId`.

### 2.2 i18n

Mọi chuỗi đi qua `tr()`. Hai lệnh kiểm chạy trong CI:

| Lệnh | Bắt điều gì |
|---|---|
| `npm run i18n:missing` | Khoá có trong mã mà thiếu bản dịch |
| `npm run i18n:stale` | Khoá trong `en.ts` mà mã không còn dùng |

**Giới hạn phải biết:** hai lệnh này đối chiếu các lời gọi `tr()`, nên chuỗi **không** gọi `tr()`
là vô hình với chúng. Phải rà bằng mắt khi thêm màn hình.

### 2.3 Hệ thống thiết kế

| Quy tắc | Cách ép |
|---|---|
| Điều khiển cùng hàng cao bằng nhau | Một biến `--control-h`; biến thể nhỏ chỉ dùng trong popup |
| Công tắc là ngoại lệ duy nhất | Ghi rõ trong CSS |
| Tiêu đề cột bảng không xuống dòng | `th { white-space: nowrap }` |
| Mỗi trang có kicker trên tiêu đề | `kicker` là tham số **bắt buộc** của `PageHead` |
| Khoảng cách kicker → tiêu đề và dưới khối = nửa chiều cao tiêu đề | Buộc vào `--page-title-size` |
| Ô chọn không bày lựa chọn cuối cùng không chọn được | Lọc ngay trong nguồn dữ liệu của ô |

---

## 3. Màn hình phải dựng

| Nhóm | Màn hình |
|---|---|
| Truy cập | Đăng nhập, đăng ký, hồ sơ |
| Dự án | Danh sách + tự tham gia (khách) + tạo/sửa/xoá (admin) |
| Sự cố | Danh sách, chi tiết, việc của tôi |
| Phản hồi | Hàng đợi, trả lời, gắn vào sự cố |
| Ticket | Danh sách, chi tiết, tạo từ template, nhãn & mốc phát hành |
| Chung | Board, hộp thư, quản trị RBAC, cấu hình hệ thống |

---

## 4. Phân công

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Chốt hệ thống thiết kế, luồng màn hình | Owner · BA | Nguyễn Bảo Long | Mục 7.5 của `Architecture.md` |
| Lõi ticket: service, truy vấn, dòng thời gian, bình luận | Dev lõi | Nguyễn Bảo Long | `Modules/Tickets/` |
| `useAction`, bộ component dùng chung, hạ tầng i18n | Developer | Nguyễn Huy Kiên | `lib/`, `components/`, `scripts/` |
| Khung ứng dụng và toàn bộ màn hình | Developer | Lê Sơn Trường | `app/`, `public/` |
| Viết test hợp đồng ba trạng thái, i18n, component | QA | Nguyễn Bảo Long | `frontend/tests/` |
| Chạy test web, rà giao diện trên trình duyệt thật | Tester | Kiên và Trường | Ghi chú trong PR |
| Duyệt giao diện theo hệ thống thiết kế | Tech Lead · QA | Nguyễn Bảo Long | Ghi chú duyệt trong PR |

**Ranh giới sở hữu.** Lõi dự án, cấu hình, script deploy, script test và toàn bộ tài liệu thuộc về Long; Kiên và Trường phát triển tính năng trên nền đó. Vai tester của hai người là **chạy** bộ test ấy trên máy thật rồi ghi lại kết quả — viết script test là việc của vai QA.

---

## 5. Tiêu chí chấp nhận

| # | Điều kiện | Cách kiểm |
|---|---|---|
| 1 | Mọi thao tác ghi đi qua `useAction` | Test canh gác quét mã |
| 2 | Mọi màn hình có thao tác ghi đều render `ActionFeedback` | Test canh gác |
| 3 | Mỗi thao tác thành công kèm thông báo | Test canh gác |
| 4 | `i18n:missing` và `i18n:stale` đều trả 0 | CI |
| 5 | Chuyển sang English không còn chữ Việt sót | Duyệt bằng mắt |
| 6 | Dropdown không bị bảng cắt mất | Test `select` |
| 7 | Đổi dự án không kéo người dùng ra khỏi màn hình đang xem | Duyệt bằng mắt |
| 8 | Dự án đang chọn hiển thị xuyên suốt mọi màn hình | Duyệt bằng mắt |
| 9 | `tsc --noEmit` và `next build` sạch | CI |

---

## 6. Chỗ dễ sai

| Sai lầm | Hậu quả | Cách tránh |
|---|---|---|
| Tự giữ cờ `busy` thay vì dùng `useAction` | Thiếu trạng thái mà không ai nhận ra | Test canh gác chặn ngay |
| Viết chuỗi thẳng vào JSX | Đổi ngôn ngữ vẫn ra tiếng Việt, và lệnh kiểm i18n **không** thấy | Rà bằng mắt khi thêm màn hình |
| Dropdown `position: absolute` trong bảng | `.table-wrap` cắt mất danh sách | Portal ra `body` với `position: fixed` |
| Điều hướng bằng cách gán địa chỉ trình duyệt | Tải lại cả tài liệu, để lại một khung trắng | Dùng router của Next |
| Hai nguồn giữ cùng một trạng thái | Hai chỗ trả lời khác nhau cho cùng câu hỏi | Một store ngoài React, dùng chung |

---

## 7. Bàn giao

- [ ] Bốn test canh gác xanh
- [ ] i18n 0 thiếu 0 thừa
- [ ] `tsc` và `next build` sạch
- [ ] Đã duyệt bằng mắt theo checklist hệ thống thiết kế
