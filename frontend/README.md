# Frontend — Incident & Feedback Tracker Web

Next.js 16 (App Router) · React 19 · TypeScript · CSS thuần, không phụ thuộc UI framework.

Đây là **CNT-01** trong mục 6.3: một API client. Giao diện ẩn/hiện chức năng theo permission
trong token cho thân thiện, nhưng **quyết định quyền cuối cùng luôn nằm ở API** — gọi thẳng
endpoint mà thiếu quyền vẫn nhận 403 (Hình 4, ISS-07).

## Màn hình

| Đường dẫn | Use case | Permission cần có |
| :--- | :--- | :--- |
| `/login` | UC-06 Đăng nhập nhận JWT | công khai |
| `/register` | UC-08 Tự đăng ký tài khoản | công khai |
| `/incidents` | UC-BIZ-01, UC-BIZ-05 — danh sách, bộ lọc, ghi nhận sự cố | `incident.read` |
| `/incidents/mine` | UC-BIZ-05 "Việc của tôi" | `incident.read` |
| `/incidents/[id]` | UC-BIZ-02/03/05/06/07 — chi tiết, lịch sử, chuyển trạng thái, gán, xóa mềm | `incident.read` |
| `/feedbacks` | UC-BIZ-04 — hàng đợi phân loại phản hồi | `feedback.read` |
| `/admin` (tab Tài khoản) | UC-01, UC-04 — tạo tài khoản, bật/tắt, gán/gỡ role, xóa user (khi chưa có dữ liệu nghiệp vụ) | `user.read` (+ `user.delete` để xóa) |
| `/admin` (tab Vai trò & quyền) | UC-02, UC-03, UC-05 — tạo vai trò, tạo/sửa mô tả/xóa permission, tick gán quyền | `role.read` |
| `/healthz` | healthcheck của docker compose | công khai |
| `/` | chuyển hướng tới `/projects/support/issues` | — |
| `/projects/[p]/issues` | UC-14 danh sách ticket kiểu GitHub: Query DSL, tab Open/Closed, lọc label/milestone/assignee/sort | `ticket.read` |
| `/projects/[p]/issues/new` | UC-17 chọn template (Issue Form) hoặc blank issue, Markdown editor + kéo-thả tệp | `ticket.create` |
| `/projects/[p]/issues/[n]` | UC-01…UC-18 chi tiết ticket: timeline real-time, comment/ghi chú nội bộ, reactions, close as…/reopen, sidebar (assignees, labels, type, milestone, priority/SLA, relationships, subscribe, lock/pin/transfer/delete) | `ticket.read` (+ quyền theo hành động) |
| `/projects/[p]/labels`, `/projects/[p]/milestones` | UC-07/UC-08 | `ticket.read` (+ `label.write` / `milestone.write`) |
| `/projects/[p]/settings` | Chính sách project, Issue templates, Webhooks + deliveries, Watch project | `ticket.write` (+ `project.manage` / `webhook.manage`) |
| `/boards`, `/boards/[id]` | UC-09 board Kanban kéo-thả, automation | `ticket.read` (+ `board.write`) |
| `/notifications` | UC-13 inbox thông báo (Inbox/Saved/Done) | `ticket.read` |
| `/search` | UC-14 tìm liên project | `ticket.read` |
| `/admin/tickets` | Issue types, SLA policies, Projects | `ticket.read` (+ `issue_type.manage` / `sla.manage` / `project.manage`) |

## Chạy trực tiếp

```bash
cp .env.example .env.local
npm install
npm run dev          # http://localhost:3000
```

Cần API chạy sẵn ở `NEXT_PUBLIC_API_BASE_URL` (mặc định `http://localhost:8080`).

```bash
npm run build        # bản production, output standalone
npm run typecheck    # tsc --noEmit
npm test             # vitest — 29 test (hợp đồng ba trạng thái + sanitize Markdown + DSL)
```

## Biến môi trường

| Biến | Mặc định | Ghi chú |
| :--- | :--- | :--- |
| `NEXT_PUBLIC_API_BASE_URL` | `http://localhost:8080` | Được **nhúng vào bundle lúc build**, nên trong Docker phải truyền qua build arg chứ không phải biến môi trường lúc chạy. Không đặt bí mật vào biến `NEXT_PUBLIC_*`. |

## Ba trạng thái của mọi thao tác

Yêu cầu *"Tất cả events 3 status"* = `processing` · `success` · `failed`, áp cho **mọi** thao
tác người dùng thực hiện trên giao diện.

Cài đặt tập trung ở [`lib/useAction.ts`](lib/useAction.ts) và hai component dùng chung
`<ActionFeedback>` cùng `<SubmitButton>` trong [`components/ui.tsx`](components/ui.tsx).
Không màn hình nào được tự quản lý cặp `busy`/`notice`/`error` riêng nữa — trước đây làm vậy
và kết quả là 5 trên 9 thao tác thiếu ít nhất một trạng thái.

```tsx
const assignAction = useAction<void>();

<ActionFeedback action={assignAction} processingLabel="Đang gán người xử lý…" />
<SubmitButton action={assignAction} disabled={!assignee} processingLabel="Đang gán…">
  Gán
</SubmitButton>
```

`useAction` còn chặn chạy chồng bằng một ref: người dùng bấm hai lần thì lần thứ hai bị bỏ qua
chứ không gửi hai request. Điều này quan trọng với `PATCH /status` vì lần gọi thứ hai sẽ bị API
từ chối bằng 409 và làm người dùng bối rối dù thao tác đầu đã thành công.

Riêng lượt **tải dữ liệu** dùng `useAction({ latest: true })`: lượt gọi mới thay thế lượt đang
chạy và chỉ kết quả mới nhất được áp — quy tắc "bỏ qua lần hai" hợp với thao tác ghi nhưng với
lượt tải nó sẽ nuốt mất thay đổi bộ lọc rơi đúng lúc đang tải. Đi kèm là một quy ước ở
`lib/api.ts`: response 204 trả `null` (không phải `undefined`) để call site vẫn phân biệt
được "thành công không có body" với "thất bại" qua phép kiểm `ok !== undefined`.

### Kiểm chứng tự động

`npm test` chạy 24 test trong [`tests/`](tests/), gồm ba nhóm:

| File | Kiểm chứng |
| :--- | :--- |
| `useAction.test.tsx` | Hook đi đúng `idle → processing → success` và `idle → processing → failed`; trả `undefined` khi thất bại để màn hình không điều hướng nhầm; chặn chạy chồng; xóa thông báo cũ khi bắt đầu lượt mới |
| `ActionFeedback.test.tsx` | Người dùng thực sự **thấy** gì ở mỗi trạng thái, gồm cả `aria-busy` và `role="status"` cho trình đọc màn hình; lỗi 409 hiện `allowedNextStatus` |
| `threeStateContract.test.ts` | Quét mã nguồn: không màn hình nào tự cài lại `busy`/`notice`, mọi lời gọi API ghi đều đi qua `useAction`, mọi màn hình có thao tác ghi đều render `<ActionFeedback>`, mọi thao tác thành công đều có thông báo |

Nhóm thứ ba là quan trọng nhất về lâu dài. Hai nhóm đầu chứng minh hook làm đúng; nhóm thứ ba
ngăn việc sáu tháng sau ai đó thêm màn hình mới rồi lại tự cài trạng thái riêng — đúng nguyên
nhân đã khiến 5 trên 9 thao tác thiếu trạng thái trước đây. Đã kiểm chứng test này thật sự bắt
được vi phạm bằng cách cố tình phá rồi chạy lại.

## Ghi chú thiết kế

- **Lưu token.** Access token nằm trong `localStorage`. Đây là lựa chọn đơn giản cho phạm vi học
  phần; môi trường production nên dùng cookie `HttpOnly` + `SameSite` để giảm rủi ro XSS.
- **Hết hạn phiên.** ADR-001 chốt token 15 phút và Release 1 không có refresh token. Mọi response
  401 sẽ xóa phiên và đưa người dùng về `/login?expired=1` kèm giải thích, thay vì để màn hình
  trống không rõ lý do.
- **Trang đăng ký đọc chính sách từ API.** Tên miền cho phép, độ dài mật khẩu tối thiểu và
  việc có phải chờ duyệt hay không đều là cấu hình phía máy chủ, nên giao diện hỏi
  `GET /api/auth/registration-policy` thay vì hardcode. Liên kết "Tạo tài khoản" ở trang đăng
  nhập cũng chỉ hiện khi API báo đang mở đăng ký, để không dẫn người dùng vào ngõ cụt.
- **Người chưa được cấp quyền thấy thông báo riêng.** Nói "thiếu permission `incident.read`"
  với người vừa đăng ký là vô nghĩa; `Guard` phát hiện trường hợp không có quyền nào và hướng
  dẫn liên hệ quản trị viên.
- **Đồng bộ quyền trên menu.** Mỗi lần đổi route, `TopBar` gọi `GET /api/auth/me` và cập nhật
  roles/permissions trong `localStorage` (giữ nguyên access token). Nhờ ADR-004, server đã nạp
  quyền từ DB cho request đó — admin vừa đổi role thì menu người dùng cập nhật khi họ điều hướng,
  không cần đợi token hết hạn.
- **Nhãn SLA.** Ngưỡng do API tính và trả trong trường `sla` của mỗi sự cố; giao diện chỉ hiển
  thị. Nhờ vậy màn hình, bộ lọc `slaBreachedOnly` và job cảnh báo nền luôn dùng chung một định
  nghĩa thay vì mỗi nơi tự tính một kiểu.
- **Thông báo lỗi 409.** API luôn kèm `allowedNextStatus` (NFR-USE-01) nên giao diện hiển thị
  thẳng bước hợp lệ tiếp theo thay vì chỉ báo "thao tác thất bại". Mã `correlationId` cũng được
  hiện ra để đối chiếu log.
