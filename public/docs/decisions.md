# Quyết định cài đặt

| | |
|---|---|
| Mục đích | Ghi lại **vì sao** hệ thống làm theo cách này, và những chỗ cố ý lệch khỏi cách làm hiển nhiên |
| Nguồn chuẩn | [`utils/docs/Architecture.md`](../../utils/docs/Architecture.md) |
| Người chịu trách nhiệm | Nguyễn Bảo Long (Tech Lead) |

Mỗi mục trả lời ba câu: **quyết định gì**, **vì sao**, và **cái giá phải trả**.

---

## 1. Phân quyền

### 1.1 Hình dạng đường dẫn quyết định phạm vi kiểm quyền

**Quyết định.** Endpoint nằm dưới `api/projects/{project}/…` thì kiểm quyền **trong project đó**;
không có `{project}` thì kiểm quyền toàn cục ∪ hợp của mọi project.

**Vì sao.** Middleware tính quyền hiệu lực cho từng request rồi phát thành claim, nên hơn 130 chỗ
gắn `[RequirePermission]` tự thành project-aware mà không phải sửa dòng nào.

**Cái giá.** Một endpoint thuộc project mà quên `{project}` sẽ **âm thầm** rơi về kiểm toàn cục —
loại lỗi không ai phát hiện cho tới khi muộn. Vì vậy có test canh gác bắt mọi endpoint tự khai
phạm vi, hoặc nằm trong danh sách toàn cục kèm lý do.

### 1.2 Chỉ vai trò toàn cục mới có quyền toàn cục

**Quyết định.** `Role.IsGlobal` chỉ đúng với `system` và `admin`.

**Vì sao.** Bản đầu không có cờ này, nên mọi vai trò vẫn cấp quyền toàn cục và **mọi phép kiểm theo
project đều rơi vào nhánh dự phòng**. Nhìn thì như đã làm, chạy thì như chưa.

**Cái giá.** Bật cờ làm 122 test đỏ cùng lúc — đó là tín hiệu **đúng**. Sửa bằng cách cấp quyền
project trong fixture, không phải bằng cách nới quy tắc.

### 1.3 Nhóm endpoint quản trị đòi quyền toàn cục

**Quyết định.** RBAC, SLA, issue type, tạo/xoá project dùng `[RequireGlobalPermission]`.

**Vì sao.** Nhánh "quyền ở bất kỳ project nào" mở luôn endpoint quản trị hệ thống: một manager có
`user.read` ở một project đọc được toàn bộ danh bạ.

**Cái giá.** Manager mất `user.read`. Bù lại bằng `GET /api/projects/{slug}/member-candidates` —
nằm trong phạm vi project và chỉ trả tên, login, ảnh đại diện.

### 1.4 Vai trò `system` đứng trên `admin`

**Quyết định.** Chính sách SLA và bộ loại issue rời khỏi `admin`, thuộc về `system` (cấp 120).

**Vì sao.** Đổi chính sách SLA là đổi cam kết dịch vụ của **mọi** dự án cùng lúc; thêm một quản trị
viên là việc thường ngày. Gộp chung thì mỗi lần cần thêm quản trị viên là trao luôn quyền đổi cam kết.

**Cái giá.** Thêm một vai trò để giải thích. Bước di trú cấp `system` cho mọi tài khoản đang mang
`admin` nên không ai mất quyền lúc bật; thu hẹp lại là việc của con người.

### 1.5 Khách hàng: quyền là giao của hai vế

**Quyết định.** `quyền = (project mở cho vai trò) ∧ (người dùng đã tự nhận)`.

**Vì sao.** Vế thứ nhất là quyết định của quản trị, vế thứ hai là của chính khách hàng. Hai câu hỏi
khác nhau nên hai bảng. Phép giao khiến ô tick không thể là đường tự cấp quyền.

**Cái giá.** Hẹp hơn hiện trạng, nên phải backfill khi bật — mọi khách đang dùng được tự nhận hộ.
Và luật chỉ áp cho vai trò tự phục vụ: áp cho `support` thì cả nhóm đứng ngoài cho tới khi từng
người tự tick, hành vi không ai ngờ tới.

---

## 2. Dữ liệu

### 2.1 `uncategorized` là project ảo, không phải hàng trong bảng

**Quyết định.** Dữ liệu chưa phân loại có `ProjectId = null`; slug `uncategorized` được nhận diện
trong mã.

**Vì sao.** Một hàng thật sẽ cần quyền, thành viên, nhãn, milestone — tất cả đều vô nghĩa với một
cái sọt tạm.

**Cái giá.** Middleware phải coi nó là "phạm vi ảo" (hợp mọi project) rồi thu hẹp lại bằng một phép
kiểm riêng, vì không có hàng cấp quyền nào để tra.

### 2.2 Phản hồi và sự cố luôn cùng project

**Quyết định.** Gắn xuyên project trả `404`. Chuyển sự cố thì phản hồi đã gắn đi theo; phản hồi đã
gắn không chuyển riêng được.

**Vì sao.** Mọi màn hình hỏi dữ liệu theo project. Một phản hồi ở project A trỏ sang sự cố ở project
B là thứ không màn hình nào biết bày ở đâu.

**Cái giá.** Người có quyền toàn cục chạm được cả hai project nên chính họ là người làm vỡ bất biến
này nếu không chặn — phép kiểm phải nằm ở chỗ gắn, không phải ở giao diện.

### 2.3 Nội dung sửa được, nhưng mỗi lần sửa để lại một dòng lịch sử ký tên

**Quyết định.** Sự cố, bình luận sự cố, phản hồi khách hàng và câu trả lời đều sửa được qua
`PATCH`. Mỗi **trường** đổi giá trị sinh một hàng trong `content_revisions` — giá trị cũ, giá trị
mới, người sửa, thời điểm, và cờ `on_behalf` khi người sửa không phải chủ bản ghi. Ghi trong cùng
transaction với việc đổi nội dung. Bảng đó chỉ ghi thêm: không endpoint nào sửa hay xoá được hàng
của nó.

Ai được sửa thì theo đúng luật module Ticket vẫn dùng — **tác giả, hoặc người có quyền cấp cao của
chính module đó**: `incident.manage_any` cho sự cố và bình luận sự cố, `feedback.respond` cho phản
hồi và câu trả lời. Cố ý **không** dùng `feedback.read.all`: đọc-được-tất-cả là quyền xem, không
phải quyền viết lại lời khách hàng.

**Vì sao.** Bản đầu không cho sửa gì cả, và điều đó bảo vệ được bằng chứng bằng cách khiến mọi lỗi
chính tả trở thành vĩnh viễn — người dùng đối phó bằng cách gửi thêm một bản ghi mới nói "bản trên
sai", tức là đúng thứ làm hỏng dữ liệu mà lệnh cấm định bảo vệ. Cho sửa **kèm lịch sử** giữ được cả
hai: nội dung hiện tại đúng, và nguyên văn cũ vẫn dựng lại được kèm tên người đã thay nó.

Ba thứ cố ý nằm ngoài: **trạng thái sự cố** (vòng đời một chiều đi qua `PATCH /status` với lịch sử
riêng của nó), **liên kết phản hồi–sự cố** (đã có `POST/DELETE /link` với ràng buộc riêng), và **lời
xác nhận tự động** (không có tác giả để đứng tên, và nó là bản sao đúng câu hệ thống đã gửi cho
khách — sửa nó là sửa lại quá khứ; chặn cả ở tầng service lẫn check constraint).

**Cái giá.** Một bảng lịch sử chung cho bốn thực thể nên **không có khoá ngoại tới bản ghi cha** —
đánh đổi có chủ ý, vì lịch sử phải sống sót qua việc bản ghi cha bị xoá, đúng như
`incident_status_history` đã cố ý không gắn query filter. Thêm vào đó, mỗi bản ghi mang thêm hai cột
`last_edited_at`/`last_edited_by` trùng lặp với hàng mới nhất trong bảng lịch sử: nhãn "đã sửa" phải
hiện trên mọi hàng của danh sách, và tính lại bằng truy vấn gộp cho mỗi trang là đổi một nhãn nhỏ
lấy một phép join không cần thiết.

### 2.4 `If-Match` bắt buộc — nhưng chỉ khi bản ghi đang bị người khác chiếm dụng để sửa

**Quyết định.** Mở form sửa thì client gọi `PUT …/edit-claim` và gia hạn mỗi 45 giây; đóng form thì
`DELETE`. Chừng nào còn **người khác** giữ chỗ chưa hết hạn (TTL 120 giây, `EditClaims:TtlSeconds`),
mọi `PATCH` lên bản ghi đó **buộc** phải mang `If-Match: "v{version}"` — thiếu header trả `428`,
lệch phiên bản trả `412`, và `*` không thay thế được vì nó chỉ khẳng định bản ghi tồn tại. Không ai
giữ chỗ thì `If-Match` vẫn được tôn trọng nếu client gửi, nhưng không bắt buộc — đúng hành vi module
Ticket vẫn có.

`version` tăng đúng một bước cho mỗi lần **nội dung** đổi, ở đúng một chỗ (`EditDraft.Record`), và
cố ý đứng yên khi đổi trạng thái, người xử lý hay liên kết: nó trả lời câu hỏi "form tôi đang mở còn
khớp không", mà những thao tác kia không đụng vào ô nào trong form đó. Bắt chúng bump phiên bản chỉ
đẻ ra `412` giả.

**Vì sao có điều kiện chứ không bắt buộc luôn.** Bắt buộc mọi lúc thì mọi client — kể cả một dòng
`curl` sửa một lỗi chính tả — phải đi hai vòng gọi. Cái giá đó chỉ đáng trả đúng lúc có tranh chấp
thật, và chỗ giữ là thứ nói cho hệ thống biết lúc nào là lúc đó.

**Vì sao không khoá cứng.** Khoá cứng thì một tab quên đóng là bản ghi chết cứng, và luôn phải kèm
một nút "phá khoá" mà rốt cuộc ai cũng bấm — tức là quay về chỗ cũ, chỉ thêm vài bước. Siết điều
kiện ghi giữ được điều thật sự quan trọng (không ai ghi đè lên bản mình chưa nhìn thấy) mà không
dựng thêm một cánh cửa phải có chìa.

**Cái giá.** Ba thứ, đều có chủ ý:
- **Khoá chính của `edit_claims` gồm cả người giữ**, nên hai người cùng mở form là hai hàng và
  **cả hai** đều bị siết. Một hàng duy nhất mỗi bản ghi sẽ chỉ siết người đến sau, trong khi người
  đến trước mới là người ngồi lâu nhất trên một form cũ.
- **Chỗ giữ hết hạn bằng `expires_at`, không bằng một job nền.** Hàng hết hạn mất hiệu lực ngay vì
  mọi truy vấn đều lọc theo cột đó; việc dọn chỉ để bảng khỏi phình nên làm nhân tiện lúc có ai đụng
  tới cùng bản ghi.
- **Giữ chỗ đòi đúng quyền như đường ghi.** Thiếu vế này thì bất kỳ ai đọc được bản ghi cũng ép được
  cả đội phải gửi `If-Match` — một đường quấy rối không tốn gì để thực hiện.

Nhịp gia hạn hiện đi bằng HTTP. SignalR đã có sẵn cho module Ticket và sẽ hợp hơn (đẩy thay vì hỏi),
nhưng nó buộc chỗ giữ phải sống theo vòng đời một kết nối realtime — một bộ phận chuyển động nữa cho
thứ mà một cột `expires_at` đang làm đúng.

### 2.5 Xoá dự án chỉ khi rỗng

**Quyết định.** Còn ticket / sự cố / phản hồi → `409` kèm số lượng từng loại. Muốn dọn thì **lưu trữ**.

**Vì sao.** Xoá một dự án đang có dữ liệu là xoá theo dây toàn bộ, không có đường hoàn tác.

**Cái giá.** Người dùng phải dọn tay. Đổi lại không có cú bấm nào xoá được nhiều tháng dữ liệu.

---

## 3. Sự kiện và thông báo

### 3.1 Sự kiện im lặng dùng chung một danh sách

**Quyết định.** `TicketEventTypes.Silent` là nguồn duy nhất cho cả truy vấn timeline lẫn bộ phát
real-time.

**Vì sao.** Trước đó mỗi bên tự giữ một luật: truy vấn lọc, kênh real-time phát tất. Thả một emoji
thì màn hình hiện dòng "… reacted" rồi biến mất ở lần tải kế tiếp.

**Cái giá.** Không có. Đây thuần tuý là gỡ một nguồn sự thật thứ hai.

### 3.2 Tắt thông báo dự án vẫn nhận lời gọi tên

**Quyết định.** `WatchLevel.Ignore` chặn mọi thứ trừ mention trực tiếp.

**Vì sao.** Nuốt luôn lời nhắc đích danh là cách chắc chắn để người ta bỏ lỡ, và không bao giờ biết
là đã bỏ lỡ.

**Cái giá.** "Tắt" không tuyệt đối. Đây là lựa chọn có chủ đích, không phải thiếu sót.

---

## 4. Bảo mật

### 4.1 Ảnh phục vụ qua URL ký, không qua header

**Quyết định.** `GET /api/storage/public/{key}?t=<HMAC>` cho phép lấy tệp không cần đăng nhập;
đường cũ `GET /api/storage/files/{key}` giữ nguyên phép kiểm quyền cho lời gọi API.

**Vì sao.** Thẻ `img` của trình duyệt không gửi header `Authorization` — token nằm trong
`localStorage`, không phải cookie. Nên **mọi ảnh đã tải lên đều nhận 401**, kể cả khi người dùng
đang đăng nhập.

**Vì sao là endpoint riêng.** Middleware nạp quyền **cố ý bỏ qua** endpoint `[AllowAnonymous]`, để
một token cũ đính nhầm không phá đường đăng nhập lại. Một endpoint vừa ẩn danh vừa muốn tự kiểm
quyền sẽ luôn thấy danh sách quyền rỗng.

**Cái giá.** Chữ ký **không có hạn dùng**, vì URL ảnh được chèn thẳng vào nội dung bình luận và nằm
lại đó vĩnh viễn — chữ ký hết hạn sẽ làm ảnh trong bình luận cũ hỏng dần. Đây là URL dạng "ai cầm
được thì đọc được"; khoá chứa GUID nên không đoán được, và chữ ký ràng theo khoá nên không sửa
đường dẫn để với sang tệp khác. Muốn siết thì phải viết lại URL lúc render Markdown.

### 4.2 Đổi mật khẩu giết mọi phiên khác

**Quyết định.** Token cấp trước mốc `PasswordChangedAt` bị từ chối; phiên đang đổi được giữ.

**Cái giá.** `iat` của JWT chỉ có độ phân giải giây, nên phải so `<=` trên giây đã làm tròn và đẩy
`iat` của token sống sót lên một giây — nếu không, một phiên đăng nhập **cùng giây** với lúc đổi
mật khẩu sẽ lọt, đúng kịch bản người ta đổi vì nghi bị lộ.

### 4.3 Vai trò đã bỏ có danh sách riêng

**Quyết định.** `Permissions.RetiredRoles` là nguồn chung cho cả bước gỡ lúc khởi động lẫn bước
chặn tạo lại.

**Vì sao.** Bước gỡ chạy mỗi lần khởi động và tìm **theo tên**. Nếu quản trị tạo một vai trò trùng
tên cho việc khác thì lần khởi động kế tiếp xoá mất nó và dồn người dùng sang vai trò thay thế —
mất dữ liệu mà không ai làm gì sai.

---

## 5. Giao diện

### 5.1 Dropdown tự vẽ, portal ra `body`

**Quyết định.** Không dùng `<select>` gốc; danh sách portal ra `document.body` với `position: fixed`.

**Vì sao.** Phần bung ra của `<select>` do hệ điều hành vẽ, CSS không với tới. Và khi để
`position: absolute` trong ô thì `.table-wrap { overflow-x: auto }` cắt mất danh sách — theo spec,
trục còn lại đang `visible` bị tính lại thành `auto`.

**Cái giá.** Phải tự lo bàn phím, ARIA, đóng khi bấm ra ngoài, và đo lại vị trí mỗi lần cuộn.

### 5.2 Thông báo kết quả là thẻ nổi

**Quyết định.** `ActionFeedback` cổng nội dung ra một lớp `position: fixed` góc dưới phải.

**Vì sao.** Thẻ chèn giữa nội dung đẩy mọi thứ bên dưới xuống đúng lúc người dùng vừa bấm; và với
thao tác ở cuối một bảng dài thì nó nằm ngoài tầm nhìn — bấm xong không thấy gì, tưởng hỏng.

**Chi tiết.** Thành công tự tắt sau 4 giây; **lỗi thì không**, vì lỗi mang bước hợp lệ tiếp theo và
`correlationId` — thứ cần đọc kỹ, có khi phải chép lại.

### 5.3 Cập nhật lạc quan cho reaction

**Quyết định.** Số và nền đổi ngay khi bấm, gửi ở nền, hoà lại theo câu trả lời của server.

**Vì sao.** Thả emoji là thao tác nhỏ nhất và gần như không bao giờ hỏng. Bắt chờ một vòng mạng rồi
mới vẽ là cái giá sai.

**Cái giá.** Phải xử lý trường hợp gửi hỏng — trả nút về đúng trạng thái trước khi bấm, vì để lại
một con số sai còn tệ hơn không đổi gì.

### 5.4 Một khuôn tiêu đề cho mọi trang

**Quyết định.** `PageHead` bắt buộc có kicker; khoảng cách kicker → tiêu đề và khoảng cách dưới khối
đều bằng nửa chiều cao tiêu đề, buộc vào biến `--page-title-size`.

**Cái giá.** Không có ngoại lệ nào được phép, kể cả trang chi tiết ticket.

---

## 6. Vận hành

### 6.1 Log SQL mặc định tắt

**Quyết định.** Mức mặc định là `Warning`; bật bằng `SQL_LOG_LEVEL=Information`.

**Vì sao.** Một lần khởi động có dữ liệu demo in ra hơn 44 nghìn dòng và mất khoảng một phút mới
lắng — đủ để chôn vùi mọi cảnh báo thật và làm người xem tưởng hệ thống đang chạy vòng lặp.

### 6.2 Seeder tự cấp quyền dự án cho dữ liệu demo

**Quyết định.** `DemoDataSeeder` cấp quyền cho dự án nó vừa tạo, **chỉ khi vừa tạo**.

**Vì sao.** Hai bước di trú cấp quyền nằm trong migration, mà migration chạy **trước** seeder. Trên
cơ sở dữ liệu trắng chúng không có dự án nào để cấp — clone về, seed xong, mọi tài khoản trừ admin
đều không vào được dự án nào.

**Cái giá.** Phải chặn nó chạy lại mỗi lần khởi động: cấp lại sau khi người vận hành vừa thu hẹp là
phá hoại.

---

## 7. Những chỗ cố ý **không** làm

| Việc | Vì sao chưa làm |
|---|---|
| Ref `#N` tới ticket không tồn tại vẫn thành link | Kiểm tra sự tồn tại lúc render tốn một truy vấn cho mỗi ref, và ticket được nhắc hôm nay có thể tạo ngày mai. Bấm vào giờ dẫn tới màn hình giải thích rõ |
| `WatchLevel.Participating` và `Custom` chưa có nghĩa riêng | `Participating` hiện đúng một cách tình cờ; `Custom` chưa dùng |
| Test hợp đồng ba trạng thái so chuỗi, không phân tích cú pháp | Siết chặt làm lộ vài chỗ truyền thông báo dạng biến hoặc biểu thức điều kiện — đều là thông báo thật. Làm đúng thì phải parse |
| `PresenceTracker` nằm trong bộ nhớ tiến trình | Chạy nhiều instance thì mỗi instance chỉ biết kết nối của chính nó. Đổi sang Redis chỉ phải thay đúng lớp đó |
| Trên cơ sở dữ liệu trắng, khách hàng chưa có dự án nào để tự nhận | Cần seeder cấp `role-access` cho `customer`; nằm ngoài phạm vi hiện tại |
