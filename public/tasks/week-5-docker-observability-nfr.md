# Tuần 5 — Đóng gói, quan sát và NFR

| | |
|---|---|
| Chủ đề | Docker Compose · OpenTelemetry · Prometheus · k6 · Sao lưu |
| Thời lượng | 120 phút tại lớp + hoàn thiện ngoài giờ |
| Nhóm | Nhóm 2 — Nguyễn Bảo Long, Nguyễn Huy Kiên, Lê Sơn Trường |
| Mốc bàn giao | Tag `week-5` |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) mục 8 |

---

## 1. Mục tiêu

Đưa hệ thống từ "chạy trên máy tôi" sang "chạy được từ máy sạch bằng một lệnh", và **nhìn thấy
được** khi nó chạy.

Kết thúc tuần, nhóm phải giải thích và chứng minh được:

- Vì sao migration chạy lúc khởi động ứng dụng chứ không phải một bước riêng.
- Vì sao seeder phải phân biệt "vừa tạo" và "đã có".
- Số đo nào chứng minh được NFR, và đo trên tập dữ liệu nào.

---

## 2. Đóng gói

Ba service: `db`, `api`, `web`. Một lệnh dựng cả ba:

```bash
cp .env.example .env      # đổi POSTGRES_PASSWORD, JWT_SIGNING_KEY, SEED_ADMIN_PASSWORD
docker compose up --build -d --wait
```

| Điều | Cách làm |
|---|---|
| Migration | Tự áp lúc API khởi động |
| Dữ liệu demo | `SEED_DEMO_DATA=true` — **chỉ** cho môi trường demo |
| Cập nhật mã | `docker compose up -d --build api web` |
| Dựng lại sạch | `docker compose down -v && docker compose up --build -d --wait` |

**Seeder chỉ cấp quyền cho project nó vừa tạo.** Chạy lại mỗi lần khởi động thì sẽ dựng lại thứ
người vận hành vừa cố ý gỡ đi.

---

## 3. Quan sát

| Tín hiệu | Nơi lấy |
|---|---|
| Health | `/api/health`, `/api/health/system` |
| Metrics | `/metrics` — định dạng Prometheus |
| Trace | OpenTelemetry, tỷ lệ lấy mẫu cấu hình được |
| Log | Có cấu trúc, kèm `correlationId` |

**Log SQL mặc định tắt.** Bật ở mức `Information` thì một lần khởi động có dữ liệu demo in ra hơn
44 nghìn dòng và mất khoảng một phút mới lắng — đủ để chôn vùi mọi cảnh báo thật. Bật bằng
`SQL_LOG_LEVEL=Information` khi cần soi truy vấn.

### 3.1 Metric nghiệp vụ

| Metric | Ý nghĩa |
|---|---|
| `incident_login_failures_total` | Đăng nhập thất bại, kèm lý do |
| `incident_login_success_total` | Đăng nhập thành công |
| `incident_status_transitions_total` | Chuyển trạng thái theo cặp from→to |
| `incident_authorization_denials_total` | Request bị chặn 401/403 |
| `incident_sla_breached` | Sự cố đang vượt ngưỡng |

---

## 4. NFR và cách đo

| ID | Yêu cầu | Cách đo |
|---|---|---|
| NFR-PORT-01 | Chạy được từ máy sạch bằng một lệnh | Dựng trên volume trắng, đo thời gian tới lúc healthy |
| NFR-REL-01 | Không mất cập nhật khi ghi đồng thời | `k6-status-contention.js` — đúng 1 thành công, còn lại `409` |
| NFR-SEC-01 | Rate limit có hiệu lực | Bắn vượt ngưỡng, kiểm `429` và `Retry-After` |
| NFR-SEC-02 | Không log bí mật | Rà log khởi động và log lỗi |
| NFR-AUD-01 | Thao tác nhạy cảm có vết | Tìm dòng `Audit …` cho đổi quyền, xoá, chuyển project |
| NFR-MNT-01 | Sao lưu và phục hồi diễn tập được | `infra/scripts` |
| NFR-USE-01 | Mọi thao tác có phản hồi ba trạng thái | Test canh gác frontend |

Kịch bản k6 và tập dữ liệu ở [`infra/loadtest/`](../../infra/loadtest/README.md).

---

## 5. Phân công

| Việc | Vai trò | Người | Sản phẩm |
|---|---|---|---|
| Chốt danh mục NFR và ngưỡng chấp nhận | Owner · BA | Nguyễn Bảo Long | Mục 4 tài liệu này |
| `docker-compose.yml`, Dockerfile, healthcheck | Developer | Lê Sơn Trường | Gốc repo, `backend/`, `frontend/` |
| `.env.example` và tài liệu biến môi trường | Developer | Lê Sơn Trường | `.env.example` |
| OpenTelemetry, metric nghiệp vụ | Developer | Nguyễn Huy Kiên | `Modules/Tickets/Infrastructure/` |
| Job quét SLA và cảnh báo | Developer | Nguyễn Huy Kiên | `Sla/` |
| Script sao lưu / phục hồi | Developer | Lê Sơn Trường | `infra/scripts/` |
| Kịch bản k6 và tập dữ liệu | Tester | Nguyễn Huy Kiên | `infra/loadtest/` |
| Diễn tập phục hồi, ghi số đo | Tester | Lê Sơn Trường | `public/docs/verification.md` |
| Duyệt kết quả đo, ký NFR | Tech Lead · QA | Nguyễn Bảo Long | Ghi chú duyệt trong PR |

---

## 6. Tiêu chí chấp nhận

| # | Điều kiện | Cách kiểm |
|---|---|---|
| 1 | `docker compose up --build -d --wait` từ volume trắng → ba service healthy | Chạy thật |
| 2 | Migration tự áp, không cần lệnh riêng | Nhìn log khởi động |
| 3 | Seed lần đầu tạo đủ dữ liệu demo **và** quyền project cho mọi tài khoản | Đăng nhập bằng 4 vai trò |
| 4 | Chạy lại `up -d --build` không seed lại, không mất dữ liệu | Chạy thật |
| 5 | `/api/health/system` trả `Healthy` cho cả ba service | `curl` |
| 6 | `/metrics` trả định dạng Prometheus, có đủ metric nghiệp vụ | `curl` |
| 7 | Log khởi động **không** đầy câu lệnh SQL | Đếm dòng log |
| 8 | Tranh chấp trạng thái: đúng 1 thành công | k6 |
| 9 | Phục hồi từ bản sao lưu chạy được test khói | Diễn tập |

---

## 7. Chỗ dễ sai

| Sai lầm | Hậu quả | Cách tránh |
|---|---|---|
| Seeder chạy lại mỗi lần khởi động | Dựng lại thứ người vận hành vừa gỡ | Chỉ cấp khi vừa tạo |
| Backfill quyền đặt trong migration mà dữ liệu tạo ở seeder | Trên máy trắng không có gì để cấp | Seeder tự cấp cho project nó tạo |
| Log SQL bật ở môi trường Development | Hàng chục nghìn dòng, che mất cảnh báo thật | Mặc định `Warning`, bật khi cần |
| Đo hiệu năng trên tập dữ liệu nhỏ | Số đo đẹp nhưng không nói lên gì | Dựng tập lớn trước khi đo |
| `docker compose restart` sau khi sửa mã | Dùng lại image cũ, thay đổi không có hiệu lực | Luôn kèm `--build` |

---

## 8. Bàn giao

- [ ] Dựng được từ máy sạch, có ghi thời gian
- [ ] Số đo NFR đã ghi vào `public/docs/verification.md`, không bịa
- [ ] Diễn tập phục hồi có biên bản
- [ ] Test tuần 1–5 xanh
