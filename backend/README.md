# Backend — Incident & Feedback Tracker API

ASP.NET Core 8 · EF Core 8 · PostgreSQL 16 · modular monolith (mục 6 của `utils/docs/Architecture.md`).

## Cấu trúc

```
backend/
├─ global.json                       SDK pin 8.0.x
├─ IncidentTracker.sln
├─ Dockerfile                        multi-stage, chạy bằng user không đặc quyền
├─ src/IncidentTracker.Api/
│  ├─ Program.cs                     pipeline: correlation → lỗi → authn → authz → controller
│  ├─ Domain/Entities.cs             8 entity của mục 5.5
│  ├─ Persistence/
│  │  ├─ AppDbContext.cs             CMP-05 — mapping, check constraint, partial index, query filter
│  │  ├─ DatabaseInitializer.cs      migration + seed 24 permission và 5 role
│  │  ├─ DesignTimeDbContextFactory.cs
│  │  └─ Migrations/                 EF Core migration, chạy được trên DB rỗng
│  ├─ Authorization/
│  │  ├─ Permissions.cs              danh mục permission — nguồn duy nhất
│  │  ├─ PermissionPolicy.cs         CMP-03 handler + policy provider theo quy ước
│  │  └─ RequireStatusPermissionFilter.cs
│  ├─ Common/                        correlation id, ProblemDetails, phân trang, rate limit
│  ├─ Observability/                 metrics, tracing, Prometheus exporter (mục 6.9)
│  └─ Modules/
│     ├─ Identity/                   CMP-02 — login, JWT, tự đăng ký
│     ├─ Rbac/                       CMP-01 + CMP-04 — user, role, permission
│     ├─ Incidents/                  CMP-06 + CMP-08 — state machine, transaction, SLA
│     └─ Feedbacks/                  CMP-07 + CMP-09
└─ tests/IncidentTracker.Api.Tests/  unit + integration trên PostgreSQL thật
```

## Chạy trực tiếp (không dùng compose)

```bash
cp .env.example .env          # đổi mọi giá trị bí mật
set -a && source .env && set +a
dotnet run --project src/IncidentTracker.Api
```

API lắng nghe ở `http://localhost:8080`.

| Endpoint | Nội dung |
| :--- | :--- |
| `/swagger/v1/swagger.json` | Đặc tả OpenAPI 3.0.1 máy đọc được |
| `/swagger/index.html` | Swagger UI để thử API |
| `/api/health/system` | Sức khỏe api + db + web trong một lời gọi |
| `/api/health` | Sức khỏe api + db, dùng cho healthcheck của compose |

Mặc định bật ở `Development` và tắt ở nơi khác. Đặt `SWAGGER__ENABLED=true` để bật tường minh
ở bản triển khai nội bộ, hoặc `false` để tắt hẳn ngay cả khi đang chạy Development.

## Migration

Migration được áp dụng tự động khi khởi động nếu `SEED__APPLYMIGRATIONS=true`.
Chạy thủ công:

```bash
dotnet tool install --global dotnet-ef --version 8.0.11
dotnet ef database update --project src/IncidentTracker.Api
```

Thêm migration mới sau khi đổi model:

```bash
dotnet ef migrations add <TênMigration> \
  --project src/IncidentTracker.Api --output-dir Persistence/Migrations
```

> `DesignTimeDbContextFactory` giúp lệnh `dotnet ef` chạy được mà không cần biến môi trường
> JWT hay một database đang sống.

## Test

```bash
# Cần một PostgreSQL đang chạy; mỗi lần chạy tạo database tạm rồi tự hủy.
export TEST_DB_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres"
dotnet test
```

Bộ test phủ TC-001…TC-005, TC-BIZ-01…TC-BIZ-09 và TC-NFR-02 của mục 7.2, cộng thêm error
contract (401/403/404/409 cùng định dạng ProblemDetails) và ngưỡng cảnh báo SLA.
Integration test chạy trên PostgreSQL thật chứ không dùng InMemory provider, vì phần lớn ràng
buộc của thiết kế (check constraint, partial index, `SELECT … FOR UPDATE`, transaction) chỉ tồn
tại ở tầng cơ sở dữ liệu.

## Biến môi trường

| Biến | Bắt buộc | Mặc định | Ý nghĩa |
| :--- | :---: | :--- | :--- |
| `CONNECTIONSTRINGS__DEFAULT` | ✔ | — | Chuỗi kết nối PostgreSQL |
| `JWT__SIGNINGKEY` | ✔ | — | Khóa ký HMAC, tối thiểu 32 ký tự |
| `JWT__ISSUER` | | `incident-tracker-api` | Claim `iss` |
| `JWT__AUDIENCE` | | `incident-tracker-web` | Claim `aud` |
| `JWT__EXPIRYMINUTES` | | `15` | Hạn access token (ADR-001) |
| `SEED__APPLYMIGRATIONS` | | `true` | Chạy migration khi khởi động |
| `SEED__ADMINEMAIL` | | `admin@incident.local` | Admin bootstrap |
| `SEED__ADMINPASSWORD` | | *(trống)* | Bỏ trống thì **không** tạo admin |
| `SWAGGER__ENABLED` | | *(theo môi trường)* | Bật Swagger. Bỏ trống = bật ở Development, tắt ở nơi khác |
| `HEALTHCHECKS__WEBURL` | | `http://localhost:3000/healthz` | Địa chỉ health của web cho `/api/health/system` |
| `HEALTHCHECKS__TIMEOUTSECONDS` | | `5` | Thời gian chờ tối đa khi hỏi web |
| `RATELIMIT__LOGINPERMINUTE` | | `10` | Rate limit login theo IP |
| `RATELIMIT__REGISTERPERHOUR` | | `5` | Rate limit đăng ký theo IP |
| `REGISTRATION__ENABLED` | | `true` | Cho phép tự đăng ký |
| `REGISTRATION__ALLOWEDEMAILDOMAINS` | | *(trống)* | Tên miền cho phép, phân tách bằng dấu phẩy |
| `REGISTRATION__REQUIREAPPROVAL` | | `false` | Tài khoản mới chờ admin kích hoạt |
| `REGISTRATION__DEFAULTROLES` | | `customer` | Vai trò cấp cho tài khoản tự đăng ký; chỉ `customer` và `viewer` được chấp nhận |
| `REGISTRATION__MINPASSWORDLENGTH` | | `8` | Độ dài mật khẩu tối thiểu khi đăng ký |
| `CORS__ALLOWEDORIGINS__0` | | `http://localhost:3000` | Origin của web |
| `SLA__INVESTIGATINGHOURS` | | `24` | Ngưỡng cảnh báo ở `Investigating` |
| `SLA__MITIGATINGHOURS` | | `48` | Ngưỡng cảnh báo ở `Mitigating` |
| `SLA__SCANINTERVALMINUTES` | | `15` | Chu kỳ job nền; `0` để tắt |
| `FEEDBACK__AUTOACKENABLED` | | `true` | Chèn lời xác nhận tiếp nhận tự động vào luồng trả lời của phản hồi mới (FR-BIZ-14) |
| `FEEDBACK__AUTOACKMESSAGE` | | *(nội dung mặc định)* | Nội dung lời xác nhận tự động, tối đa 2000 ký tự |
| `OBSERVABILITY__ENABLEPROMETHEUS` | | `true` | Phơi `GET /metrics` |
| `OBSERVABILITY__TRACESAMPLERATIO` | | `0.1` | Tỷ lệ lấy mẫu trace |
| `OBSERVABILITY__OTLPENDPOINT` | | *(trống)* | Collector OTLP; trống thì span không xuất đi đâu |
| `SECURITY__REQUIREHTTPS` | | `false` | Bật HSTS và HTTPS redirect |
| `TICKETING__PUBLICBASEURL` | | `http://localhost:3000` | URL web dùng trong link email/webhook/mention |
| `TICKETING__PUBLICAPIBASEURL` | | `http://localhost:8080` | URL API dùng cho presigned URL local |
| `TICKETING__READPERMINUTE` / `WRITEPERMINUTE` | | `300` / `100` | Rate limit đọc/ghi theo user cho API ticket (BR-SEC-03) |
| `TICKETING__HUBINVOCATIONSPERMINUTE` | | `60` | Rate limit lời gọi SignalR hub mỗi connection |
| `TICKETING__IDEMPOTENCYTTLHOURS` | | `24` | Thời gian giữ `Idempotency-Key` |
| `TICKETING__MAINTENANCEINTERVALMINUTES` | | `360` | Job tạo partition `ticket_events` tháng kế + dọn key; `0` để tắt |
| `TICKETING__RABBITMQ__HOST` | | *(trống)* | Trống = MassTransit in-memory; đặt host để dùng RabbitMQ (`__USERNAME`, `__PASSWORD`, `__VIRTUALHOST`) |
| `TICKETING__WEBHOOKS__ALLOWPRIVATENETWORKS` | | `false` | Cho phép webhook tới http/loopback/mạng nội bộ |
| `TICKETING__WEBHOOKS__RETRYSCANSECONDS` | | `30` | Chu kỳ quét delivery đến hạn retry; `0` để tắt |
| `TICKETING__WEBHOOKS__TIMEOUTSECONDS` | | `10` | Timeout gửi webhook |
| `TICKETING__SLA__SCANINTERVALMINUTES` | | `1` | Chu kỳ quét SLA ticket; `0` để tắt |
| `TICKETING__STORAGE__LOCALPATH` | | `./storage` | Thư mục lưu tệp khi không dùng S3 |
| `TICKETING__STORAGE__PRESIGNMINUTES` | | `15` | Hạn presigned URL |
| `TICKETING__STORAGE__MAXBYTES` | | `26214400` | Kích thước tệp tối đa (25 MB) |
| `TICKETING__STORAGE__S3__BUCKET` | | *(trống)* | Có bucket = dùng S3/MinIO (`__REGION`, `__SERVICEURL`, `__ACCESSKEY`, `__SECRETKEY`, `__PUBLICBASEURL`, `__FORCEPATHSTYLE`) |

Cấu hình dùng dấu `__` để phân tách cấp — đây là quy ước chuẩn của
`Microsoft.Extensions.Configuration` khi đọc từ biến môi trường.
