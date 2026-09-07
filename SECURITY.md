# Bảo mật

## Báo lỗ hổng

**Đừng mở issue công khai.** Liên hệ riêng nhóm phát triển và mô tả:

- Cách tái hiện
- Ảnh hưởng bạn đánh giá được
- Phiên bản hoặc commit đang chạy

Chúng tôi phản hồi trong vòng 7 ngày.

## Mô hình bảo mật

### Xác thực và ủy quyền

- **JWT có chữ ký HMAC-SHA256.** Mọi token đều được kiểm chữ ký, `issuer`, `audience` và hạn
  dùng. Khóa ký tối thiểu 32 ký tự, chỉ đến từ biến môi trường.
- **Token chỉ mang danh tính, không mang quyền.** Payload đúng năm claim: `sub`, `jti`, `iss`,
  `aud`, `exp`. Mọi thứ suy ra được từ `sub` — email, tên, vai trò, permission — đều bị bỏ ra
  ngoài. Lý do: payload JWT chỉ là base64 nên bất kỳ ai cầm token cũng đọc được; nhét danh mục
  permission vào đó là phơi ra tấm bản đồ năng lực của hệ thống, đồng thời làm token phình to
  trên mọi request.
- **Vai trò và permission nạp từ database ở mỗi request**, ngay sau khi xác thực danh tính.
  Nhờ vậy **thu hồi quyền có hiệu lực tức thì**: gỡ quyền hay vô hiệu hóa tài khoản là request
  kế tiếp đã bị chặn, không phải chờ token hết hạn. Đổi lại là một truy vấn có index cho mỗi
  request được bảo vệ; bật `AUTH_PERMISSION_CACHE_SECONDS` nếu đo được đây là nút thắt, và
  chấp nhận độ trễ thu hồi đúng bằng khoảng đã đặt.
- **Quyền được kiểm trước khi request chạm controller.** Không có `if` kiểm quyền nào nằm trong
  controller nghiệp vụ.
- **Mặc định là từ chối.** `FallbackPolicy` đòi đăng nhập cho mọi endpoint; endpoint công khai
  phải nói ra bằng `[AllowAnonymous]`. Quên khai báo quyền cho một action mới thì nó bị khóa,
  chứ không âm thầm mở.
- **Hai tầng, hai câu hỏi khác nhau.** Permission trả lời *"được làm loại việc này không"*;
  authorization theo bản ghi trả lời *"trên bản ghi này thì có không"*. Tầng thứ hai chạy sau
  khi entity đã được nạp, và là thứ duy nhất chặn được BOLA — người có đúng vai trò nhưng đụng
  vào dữ liệu của người khác. Quy tắc nằm trong `ResourceAccessRules` và hai
  `AuthorizationHandler`, không rải trong controller.
- **Danh sách cũng phải lọc, không chỉ chi tiết.** Người không có `incident.read.all` được lọc
  ngay trong câu SQL. Lọc sau khi nạp thì `totalCount` vẫn đếm dữ liệu của người khác — vẫn là
  rò rỉ, chỉ ít hơn.
- **Giao diện không phải nơi quyết định quyền.** Ẩn nút chỉ để thân thiện; gọi thẳng API mà
  thiếu quyền vẫn nhận `403`.

### Tự đăng ký

`POST /api/auth/register` là endpoint công khai duy nhất ghi được vào database, nên nó là bề
mặt tấn công đáng chú ý nhất.

- **Đặc quyền tối thiểu.** Tài khoản tự đăng ký nhận vai trò `customer`: gửi được sự cố và
  phản hồi, nhưng chỉ đọc lại được đúng những gì mình đã gửi, và không chạm được vào vòng đời
  xử lý. Đây là đường khách hàng vào hệ thống (ACT-BIZ-03).
- **Danh sách trắng cứng trong mã.** `RegistrationOptions.AssignableRoles` chỉ chứa `customer`
  và `viewer`. Cấu hình sai không thể biến đường đăng ký thành đường leo thang đặc quyền; vai
  trò bị chặn được ghi cảnh báo lúc khởi động.
- **DTO không có trường nhạy cảm.** `RegisterRequest` không khai báo `roles` hay `isActive`.
- **Rate limit** 5 lần/giờ/IP.
- **Giới hạn tên miền email** qua `REGISTRATION_ALLOWED_DOMAINS` — hàng rào thật sự khi hệ
  thống phơi ra Internet.

**Đánh đổi đã biết:** đăng ký trùng email trả `409`, tức là kẻ tấn công có thể dò xem email nào
đã có tài khoản. Điều này mâu thuẫn với nguyên tắc ở đường đăng nhập (lỗi chung, không lộ email
tồn tại hay không). Chấp nhận vì không có hạ tầng gửi email để xác minh: trả về thành công giả
sẽ khiến người dùng thật không biết vì sao mình không đăng nhập được. Hàng rào thay thế là danh
sách tên miền và rate limit.

### Mật khẩu

- Băm bằng `PasswordHasher<T>` của ASP.NET Core (PBKDF2, có salt riêng cho từng bản ghi).
- Không bao giờ ghi log, không bao giờ xuất hiện trong response — có test khẳng định điều này.
- Tối thiểu 8 ký tự.
- Không có mật khẩu mặc định: `SEED_ADMIN_PASSWORD` để trống thì hệ thống không tạo tài khoản
  quản trị nào.

### Dữ liệu

- **PII của khách hàng** (`customer_email`, nội dung phản hồi) chỉ trả về cho người có
  `feedback.read.all`; người chỉ có `feedback.read` đọc được đúng phản hồi do mình gửi. PII
  **không bao giờ vào log** — có test quét toàn bộ log để khẳng định.
- **Phản hồi lỗi không lộ chi tiết nội bộ.** Không stack trace, không chuỗi kết nối. Thay vào
  đó là `correlationId` để tra log.
- **Lịch sử trạng thái chỉ ghi thêm.** Không có endpoint nào sửa hay xóa được.

### Bề mặt tấn công

| Hạng mục | Trạng thái |
| :--- | :--- |
| Rate limit đăng nhập | 10 lần/phút/IP, cấu hình được |
| Rate limit đăng ký | 5 lần/giờ/IP, cấu hình được |
| Tự đăng ký | Bật mặc định, chỉ cấp vai trò `viewer`. Tắt bằng `REGISTRATION_ENABLED=false` |
| CORS | Chỉ origin khai báo tường minh |
| Swagger / OpenAPI | Mặc định chỉ bật ở Development. Đặc tả công khai giúp người lạ liệt kê toàn bộ bề mặt API nên phải chọn mới có |
| `/metrics` | Công khai. Chạy nội bộ thì chấp nhận được; phơi ra Internet thì chặn ở tầng ingress hoặc tắt bằng `OBSERVABILITY_ENABLE_PROMETHEUS=false` |
| HTTPS | Tắt mặc định vì compose chạy HTTP thuần. Bật bằng `SECURITY_REQUIRE_HTTPS=true` khi có ingress TLS |
| Security headers | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` trên mọi response, kể cả response lỗi |
| Endpoint chưa khai báo quyền | Bị khóa mặc định bởi `FallbackPolicy` |

## Threat model v1

Mức rủi ro là kết hợp *khả năng xảy ra* × *mức thiệt hại*, không phải cảm tính. Một lỗi dễ
khai thác và làm rò dữ liệu cá nhân luôn xếp **Cao**, kể cả khi chưa từng xảy ra lần nào.

| Mã | Tài sản | Nguy cơ | Mức rủi ro | Biện pháp đã làm | Bằng chứng |
| :--- | :--- | :--- | :--- | :--- | :--- |
| THR-01 | Sự cố và phản hồi của khách hàng | Khách hàng đọc hoặc sửa dữ liệu của khách hàng khác bằng cách đoán id (BOLA) | Cao | Authorization theo bản ghi ở cả chiều đọc lẫn chiều ghi; danh sách lọc ngay trong SQL | `OwnershipAndCustomerTests` — TC-BIZ-10/11/13 |
| THR-02 | Mật khẩu tài khoản | Dò mật khẩu tự động qua `/api/auth/login` | Trung bình | Rate limit 10 lần/phút/IP; thông báo lỗi chung không phân biệt sai email hay sai mật khẩu | `Rate_limit_chan_do_mat_khau_qua_endpoint_dang_nhap` |
| THR-03 | Khóa ký JWT | Khóa bị commit vào Git rồi dùng để tự phát token hợp lệ | Cao | Khóa chỉ đến từ biến môi trường; `appsettings.json` để trống; `.env` nằm trong `.gitignore` | `Dac_ta_khong_chua_bi_mat_nao`; rà `Password=` trước khi push |
| THR-04 | Quy trình xử lý sự cố | Người không được giao tự ý đóng sự cố, làm hỏng số đo thời gian khắc phục | Trung bình | Chỉ người được giao hoặc người có `incident.manage_any` mới chuyển được trạng thái; mọi lần chuyển đều ghi lịch sử bất biến | TC-BIZ-11; `AuditLogTests` |
| THR-05 | Toàn bộ API | Một action mới quên gắn permission trở thành endpoint công khai | Trung bình | `FallbackPolicy` khóa mặc định mọi endpoint chưa khai báo | `Endpoint_khong_khai_bao_quyen_van_doi_dang_nhap` |
| THR-06 | Phiên của người dùng | Token bị lộ qua XSS vì đang lưu ở `localStorage` | Trung bình | Hạn token 15 phút; token không mang quyền nên kẻ lấy được cũng không đọc thêm được gì về hệ thống; vô hiệu hóa tài khoản chặn ngay lập tức. **Chưa khắc phục triệt để** — xem Hạn chế đã biết | — |
| THR-09 | Danh mục năng lực của hệ thống | Người cầm token giải mã payload để liệt kê toàn bộ permission rồi nhắm vào endpoint nhạy cảm | Thấp | Token không còn chứa claim vai trò hay permission (ADR-004) | Giải mã payload chỉ thấy `sub`, `jti`, `iss`, `aud`, `exp` |
| THR-07 | Tài khoản quản trị | Xóa nhầm admin cuối cùng hoặc xóa người đã để lại dấu vết, làm mất khả năng truy vết | Thấp | `DELETE /api/users/{id}` chặn tự xóa mình, chặn admin cuối cùng, chặn user đã có dữ liệu nghiệp vụ | `HardeningAndAdminCrudTests` |
| THR-08 | Đường đăng ký công khai | Tạo hàng loạt tài khoản rác | Trung bình | Rate limit 5 lần/giờ/IP; danh sách trắng vai trò; giới hạn tên miền email khi cần | `Rate_limit_chan_tao_hang_loat_tai_khoan` |

## Hạn chế đã biết

Những điểm sau là **có chủ đích** trong Release 1, không phải sơ suất:

- **Token lưu ở `localStorage`.** Đơn giản cho phạm vi hiện tại nhưng dễ tổn thương với XSS.
  Production nên dùng cookie `HttpOnly` + `SameSite`.
- **Không có refresh token.** Hết 15 phút phải đăng nhập lại.
- **Không có khóa tài khoản sau nhiều lần đăng nhập sai.** Chỉ có rate limit theo IP.

## Trước khi triển khai ra ngoài

- [ ] Đổi toàn bộ giá trị bí mật trong `.env`, không dùng lại giá trị mẫu
- [ ] `SWAGGER_ENABLED` để trống hoặc `false`
- [ ] `SECURITY_REQUIRE_HTTPS=true`, đặt sau ingress có TLS
- [ ] Chặn `/metrics` ở tầng mạng
- [ ] Bật sao lưu định kỳ (`infra/scripts/backup.sh`) và diễn tập phục hồi
- [ ] Rà lại `SEED_ADMIN_PASSWORD` và đổi mật khẩu admin sau lần đăng nhập đầu
- [ ] Đặt `REGISTRATION_ALLOWED_DOMAINS` theo tên miền của tổ chức, hoặc tắt hẳn tự đăng ký
