# Kiểm chứng

> Hệ thống tự nhận làm được những gì, và **bằng chứng** cho từng lời tự nhận đó.

| | |
|---|---|
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) mục 9 |
| Người chịu trách nhiệm | Nguyễn Bảo Long (QA) |

---

## 1. Tổng quan

| Loại | Số lượng | Chạy bằng |
|---|---:|---|
| Test backend — unit + integration trên PostgreSQL **thật** | 281 | `cd backend && dotnet test` |
| Test frontend — hợp đồng ba trạng thái, i18n, component | 86 | `cd frontend && npm test` |
| Bộ sưu tập Postman | 2 | `newman run infra/postman/*.json` |
| Kịch bản tải k6 | 4 | `./infra/loadtest/run.sh` |
| Kiểm i18n | 2 lệnh | `npm run i18n:missing`, `npm run i18n:stale` |

---

## 2. Bốn nguyên tắc

### 2.1 Test chạy trên PostgreSQL thật

Không dùng in-memory provider. Ràng buộc unique, check constraint, partition theo tháng, và
`SELECT … FOR UPDATE` chỉ đúng trên máy thật. Mỗi lần chạy tạo một database riêng rồi xoá đi.

### 2.2 Kiểm chứng bằng cách hoàn tác

Một test xanh chưa chứng minh gì nếu không biết nó đỏ khi nào. Với mỗi luật quan trọng: **gỡ bản
sửa ra, test phải đỏ; lắp lại, test phải xanh.**

Đã làm với: cách ly theo project, tách quyền đọc ghi chú nội bộ, xoá cột board đòi cột trống, luật
giao-của-hai-vế cho khách hàng, sự kiện im lặng không dựng dòng timeline, lỗi không tự tắt trên
thẻ nổi.

### 2.3 Test canh gác

Test tự quét mã nguồn để một quy tắc không thể bị quên khi thêm màn hình mới:

| Test | Bắt điều gì |
|---|---|
| `Moi_endpoint_deu_tu_khai_pham_vi` | Mọi controller nằm dưới `api/projects/{project}` hoặc trong danh sách toàn cục **kèm lý do** |
| Hợp đồng ba trạng thái | Mọi thao tác ghi đi qua `useAction` và hiển thị đủ ba trạng thái |
| `i18n:missing` / `i18n:stale` | `en.ts` không thiếu và không thừa khoá |

Test canh gác endpoint đã bắt được `api/presence` ngay khi nó vừa được thêm.

### 2.4 Đo trên hệ thống đang chạy

Kết luận về hành vi phải có lệnh gọi thật kèm mã trạng thái, không chỉ dựa vào test.

---

## 3. Đã kiểm chứng bằng lệnh gọi thật

| Điều được khẳng định | Cách kiểm | Kết quả |
|---|---|---|
| Khách xem được sự cố của khách khác nhưng không thấy ghi chú | Gọi lịch sử sự cố bằng hai tài khoản | Khách: `note = null`; nhân viên: 2 ghi chú có nội dung |
| Danh sách dự án cắt theo quyền | Tạo dự án chưa cấp cho ai | admin thấy 5, support và customer thấy 4 |
| `invisible` chặn ở server | Đặt trạng thái rồi đọc hồ sơ bằng tài khoản khác | Chuỗi `invisible` **không** có trong payload; người khác thấy `offline` |
| Quyền khách là giao của hai vế | Bỏ tick một dự án | Số dự án 4 → 3, mở dự án đó nhận `403`; tick lại về 4 và `200` — **cùng token cũ** |
| Không tự cấp quyền được | Tạo dự án chưa mở cho `customer` | Không có trong danh mục; tự nhận nhận `404` |
| Phân loại `uncategorized` | Chuyển sự cố đi và về | Cả hai chiều `200`; khách thử chuyển nhận `403` |
| Xoá dự án đòi rỗng | Xoá dự án còn dữ liệu | `409` kèm `{tickets: 9, incidents: 13, feedbacks: 13}` |
| Ảnh lấy được không cần header | Gọi URL ký khi chưa đăng nhập | `404` vì tệp chưa upload — **không còn `401`** |
| Reaction theo người xem | Hai tài khoản trên cùng ticket | Mỗi người chỉ thấy phần của mình, cả ở chi tiết lẫn danh sách |
| Vai trò `system` | admin thường tạo issue type | `403`; tài khoản mang `system` thì được |

---

## 4. Quan sát

| Tín hiệu | Nơi lấy |
|---|---|
| Request rate, tỷ lệ 4xx/5xx, p95 | `GET /metrics` |
| Đăng nhập thất bại / thành công | `incident_login_failures_total`, `incident_login_success_total` |
| Chuyển trạng thái theo cặp from→to | `incident_status_transitions_total` |
| Request bị chặn 401/403 | `incident_authorization_denials_total` |
| Sự cố đang vượt ngưỡng SLA | `incident_sla_breached` |
| Trace | Span HTTP → controller → service → DB |

**Cảnh báo SLA.** Job nền quét theo chu kỳ cấu hình được và ghi log `Alert sla.breach` cho sự cố ở
*Đang điều tra* quá ngưỡng, hoặc ở *Đang khắc phục* quá ngưỡng. Đồng hồ tính từ mốc **bước vào
trạng thái hiện tại**, không phải từ lúc tạo — nhờ vậy thời gian điều tra và thời gian khắc phục
được đo tách bạch. Sự cố đã giải quyết ngừng tính giờ.

**Log SQL mặc định tắt.** Bật ở mức `Information` thì một lần khởi động có dữ liệu demo in ra hơn
44 nghìn dòng và mất khoảng một phút mới lắng. Đặt `SQL_LOG_LEVEL=Information` khi cần soi truy vấn.

---

## 5. Hiệu năng

Đo bằng k6 — kịch bản và tập dữ liệu ở [`infra/loadtest/`](../../infra/loadtest/README.md).

| Kịch bản | Kiểm điều gì |
|---|---|
| `k6-protected-api.js` | p95 của các endpoint đọc dưới tải |
| `k6-status-contention.js` | Tranh chấp khi nhiều người cùng chuyển trạng thái một sự cố |
| `run.sh` | Kịch bản hỗn hợp đọc / ghi |
| `seed-dataset.sh` | Dựng tập dữ liệu lớn trước khi đo |

Tranh chấp chuyển trạng thái: đúng **một** request thành công, phần còn lại nhận `409` — không có
trường hợp hai request cùng thắng.

---

## 6. Sao lưu và phục hồi

Script ở [`infra/scripts/`](../../infra/scripts). Quy trình đã diễn tập: dump → xoá volume →
restore → chạy lại test khói.

---

## 7. Chỗ chưa kiểm chứng được bằng máy

| Việc | Vì sao |
|---|---|
| Duyệt giao diện bằng mắt | Không có công cụ chụp màn hình tự động trong quy trình hiện tại. Mọi kết luận về giao diện dựa trên test, CSS được phục vụ, và route trả `200` |
| `npm run lint` | Hỏng sẵn từ trước do Next 16 bỏ `next lint` |
