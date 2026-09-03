# infra — Hạ tầng và vận hành

## Vị trí các file hạ tầng

| File | Vai trò |
| :--- | :--- |
| `../docker-compose.yml` | Nguồn duy nhất để dựng cả ba service web/api/db (NFR-PORT-01) |
| `../.env.example` | Mẫu cấu hình cho compose; sao chép thành `.env` |
| `../backend/Dockerfile` | Ảnh API — multi-stage, chạy bằng user không đặc quyền |
| `../frontend/Dockerfile` | Ảnh web — Next.js standalone |
| `../backend/docker-compose.yml` | Chỉ PostgreSQL, dùng cho `dotnet run` và `dotnet test` |
| `../.github/workflows/ci.yml` | CI: build, migration trên DB rỗng, test, ba service healthy, error contract và diễn tập backup |
| `loadtest/` | Kịch bản k6 cho TC-NFR-01 và kết quả đo được |
| `scripts/backup.sh` · `scripts/restore.sh` | Sao lưu và diễn tập phục hồi (mục 5.7) |
| `NFR-MNT-01.md` | Quy trình thêm permission mới không đổi schema RBAC |
| `../render.yaml` | Blueprint deploy API lên Render — bí mật khai bằng `sync: false` nên không nằm trong repo |
| `postman/` | Collection kiểm chứng sức khỏe / vòng đời (`gitissues`) và hardening tuần 3 (`week-3-auth-rbac`) — CI chạy cả hai |

Thư mục này cố ý không chứa thêm manifest nào. Mục 6.3 của tài liệu thiết kế chốt baseline
không có message broker, cache hay microservice — chỉ thêm khi có driver đo được.

## Topology theo môi trường (mục 6.8)

| Môi trường | Topology | Cấu hình / bí mật | Triển khai | Rollback |
| :--- | :--- | :--- | :--- | :--- |
| Dev | web, api, db qua compose trên máy cá nhân | `.env` sinh từ `.env.example` | `docker compose up --build` | Revert migration và code |
| Test (CI) | api + PostgreSQL service của GitHub Actions | Secret của repository | Chạy theo từng PR | Redeploy build trước đó |
| Production | HTTPS ingress + API + managed PostgreSQL | Secret store hoặc KMS | Rolling deploy bằng container | Artifact trước đó kèm kế hoạch DB |

## Healthcheck

| Service | Kiểm tra | Ý nghĩa |
| :--- | :--- | :--- |
| `db` | `pg_isready` | PostgreSQL nhận kết nối |
| `api` | `GET /api/health` | API sống **và** kết nối được DB; mất DB thì api chuyển sang unhealthy |
| `web` | `GET /healthz` | Tiến trình Next.js còn sống. Cố ý **không** gọi sang API để một sự cố tạm thời của API không kéo web unhealthy theo |

### Xem toàn cảnh bằng một lời gọi API

`GET /api/health/system` trả trạng thái cả ba service — đây là endpoint dùng khi vận hành hoặc
khi kiểm chứng bằng Postman.

Nó **không** được dùng làm healthcheck của container. Compose khai báo
`web depends_on api healthy`; nếu api lại chờ web trả lời thì hai bên chờ nhau và không service
nào lên được. Vì vậy check `web` chỉ mang tag `system`, còn `api` và `db` mang cả tag `ready`
mà healthcheck của compose đọc.

| Tình huống | `/api/health` | `/api/health/system` |
| :--- | :---: | :--- |
| Bình thường | 200 | 200 · cả ba `Healthy` |
| `web` chết | 200 | 503 · chỉ `web` đỏ |
| `db` chết | 503 | 503 · chỉ `db` đỏ |

Báo cáo lỗi cố ý không kèm thông điệp exception — thông điệp của Npgsql có thể chứa chuỗi kết
nối. Thay vào đó trả `correlationId` để tra log.

### Kiểm chứng bằng Postman

`postman/gitissues.postman_collection.json` — 9 request, 31 assertion. Import vào
Postman rồi đặt biến `adminPassword`, hoặc chạy bằng newman:

```bash
docker run --rm --network host -v "$PWD/infra/postman:/etc/newman"   postman/newman:alpine run /etc/newman/gitissues.postman_collection.json   --env-var "adminPassword=$(grep '^SEED_ADMIN_PASSWORD=' .env | cut -d= -f2-)"
```

## OpenAPI và tư thế bảo mật

Yêu cầu kỹ thuật *"OpenAPI 2 endpoint"* nói về việc đặc tả phải mô tả tối thiểu hai endpoint
nghiệp vụ, **không** buộc phải phơi Swagger ra bản triển khai. Vì vậy mặc định là an toàn:
bật ở `Development`, tắt ở nơi khác.

Lý do: đặc tả công khai giúp bất kỳ ai truy cập được API cũng liệt kê được toàn bộ bề mặt của
nó. Điều đó **không** làm lộ dữ liệu — mọi endpoint nghiệp vụ vẫn đòi JWT hợp lệ và đúng
permission, và đã có test khẳng định đặc tả không mang theo bí mật nào — nhưng nó rút ngắn công
đoạn dò tìm của kẻ tấn công. Không có lợi ích bù lại thì không nên bật.

| Tình huống | `SWAGGER_ENABLED` |
| :--- | :--- |
| Phát triển tại máy | bỏ trống — tự bật vì môi trường là Development |
| Bản triển khai nội bộ, sau VPN, hoặc để demo | `true` |
| API phơi ra Internet công cộng | bỏ trống hoặc `false`; nếu vẫn cần thì chặn `/swagger/*` ở tầng ingress và chỉ mở cho dải IP nội bộ |

```bash
SWAGGER_ENABLED=true docker compose up -d api    # bật tường minh
```

Giá trị sai định dạng rơi về mặc định theo môi trường thay vì làm sập ứng dụng.

## Giám sát

`GET /metrics` phơi số liệu theo định dạng Prometheus. Endpoint này **không** nằm sau
authorization vì hệ thống giám sát thường chạy trong mạng nội bộ và không có JWT — ở production
phải chặn bằng network policy hoặc tắt hẳn bằng `OBSERVABILITY_ENABLE_PROMETHEUS=false`.

Cảnh báo SLA do `SlaMonitor` ghi ra log dưới dạng `Alert sla.breach incident=… elapsedHours=…`.
Hệ thống giám sát bên ngoài bắt theo chuỗi này, hoặc đặt rule trên metric
`incident_sla_breached > 0`. Cố ý không gửi email/Slack: mục 1.3 đã đưa tích hợp thông báo ra
ngoài phạm vi.

## Sao lưu, RPO và RTO

| Chủ đề | Quyết định |
| :--- | :--- |
| Công cụ | `pg_dump --format=custom`, xem `scripts/backup.sh` |
| Tần suất ở môi trường học phần | Chạy tay trước mỗi buổi demo |
| Tần suất ở production | Snapshot của managed PostgreSQL, tối thiểu hàng ngày |
| Diễn tập phục hồi | `scripts/restore.sh` nạp vào database tạm rồi đối chiếu số bản ghi từng bảng; CI chạy mỗi lần build compose |
| RPO / RTO | Vẫn cần Product Owner chốt. Với dữ liệu đo SLA, mất tối đa **một ngày** là chấp nhận được về mặt kỹ thuật, nhưng con số cam kết phải do phía nghiệp vụ quyết |

## Runbook ngắn

**API không lên được.** Xem `docker compose logs api`. Nguyên nhân phổ biến:

- `Thiếu ConnectionStrings:Default` → chưa có `.env`, hoặc thiếu `POSTGRES_PASSWORD`.
- `Jwt:SigningKey phải dài tối thiểu 32 ký tự` → khóa trong `.env` quá ngắn.
- `db` chưa healthy → compose đã có `depends_on: condition: service_healthy`, hãy chờ hết
  `start_period`.

**Đăng nhập được nhưng mọi request đều 401.** Access token chỉ sống 15 phút theo ADR-001 và
Release 1 không có refresh token. Đăng nhập lại. Muốn kéo dài cho buổi demo thì đặt
`JWT_EXPIRY_MINUTES` (tối đa 60).

**Không có tài khoản để đăng nhập.** `SEED_ADMIN_PASSWORD` trống thì hệ thống cố ý bỏ qua bước
tạo admin (mục 5.7 cấm seed mật khẩu mặc định đoán được). Đặt biến đó rồi khởi động lại `api`.

**Đổi role cho user nhưng quyền chưa đổi.** Đúng như ADR-001 đã ghi nhận: permission nằm trong
token nên thay đổi chỉ có hiệu lực ở lần đăng nhập kế tiếp, chậm nhất là sau `JWT_EXPIRY_MINUTES`.

**Xóa dữ liệu và làm lại từ đầu.**

```bash
docker compose down -v && docker compose up --build -d --wait
```
