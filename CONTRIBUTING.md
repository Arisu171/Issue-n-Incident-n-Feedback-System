# Đóng góp

Cảm ơn bạn đã dành thời gian. Tài liệu này mô tả quy trình làm việc và những chuẩn mà mã nguồn
trong kho này tuân theo.

## Quy trình

```
issue → branch → commit → pull request → review → merge
```

1. **Mở issue trước.** Mọi thay đổi bắt đầu từ một issue mô tả *vấn đề*, không phải *giải pháp*.
2. **Tạo nhánh** đặt tên theo dạng `<loại>/<số-issue>-<mô-tả-ngắn>`:

   | Loại | Dùng khi |
   | :--- | :--- |
   | `feat/` | Thêm năng lực mới |
   | `fix/` | Sửa hành vi sai |
   | `test/` | Chỉ thêm hoặc sửa test |
   | `docs/` | Chỉ đổi tài liệu |
   | `chore/` | Hạ tầng, cấu hình, dọn dẹp |

3. **Commit** theo [Conventional Commits](https://www.conventionalcommits.org/):
   `feat(backend): ...`, `fix: ...`, `docs: ...`. Thân commit giải thích **vì sao**, không lặp
   lại **cái gì** — phần đó đã nằm trong diff.
4. **Mở pull request**, tham chiếu issue bằng `Closes #<số>`.
5. **Review** ít nhất một người. CI phải xanh.
6. **Merge** bằng merge commit (`--no-ff`) để giữ ranh giới của từng PR trong lịch sử.

## Trước khi mở pull request

```bash
# Backend
cd backend
export TEST_DB_CONNECTION="Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres"
dotnet build && dotnet test

# Frontend
cd frontend
npm run typecheck && npm test && npm run build

# Toàn hệ thống
docker compose up --build -d --wait
curl -s http://localhost:8080/api/health/system
```

## Chuẩn mã nguồn

### Chung

- **Không có cảnh báo build.** Cả `dotnet build` lẫn `tsc --noEmit` phải sạch.
- **Comment giải thích lý do, không mô tả lại mã.** Một comment tốt trả lời câu "vì sao lại làm
  kiểu kỳ lạ này" — thường là vì có một lỗi đã xảy ra thật.
- **Không commit bí mật.** `.env` nằm trong `.gitignore`; mọi giá trị nhạy cảm đi qua biến môi
  trường.

### Backend

- **Quy tắc nghiệp vụ nằm trong domain service**, không nằm trong controller. Controller chỉ
  điều phối. Đặc biệt: mọi quy tắc về vòng đời sự cố chỉ được khai báo trong
  `IncidentStateMachine` — nếu bạn thấy mình viết `if (status == ...)` ở nơi khác, dừng lại.
- **Integration test chạy trên PostgreSQL thật.** Không dùng InMemory provider: check
  constraint, partial index, `SELECT … FOR UPDATE` và transaction chỉ tồn tại ở tầng cơ sở dữ
  liệu, nên InMemory sẽ cho kết quả xanh giả.
- **Ràng buộc quan trọng phải có ở tầng DB**, không chỉ ở tầng ứng dụng. Kiểm tra trước rồi mới
  ghi luôn để lại khe hở TOCTOU; hãy để unique index hoặc check constraint là thứ thực sự bảo
  vệ, còn kiểm tra trước chỉ để có thông báo lỗi thân thiện.
- **Đổi model thì phải kèm migration**, và migration phải chạy được trên một database rỗng.

### Frontend

- **Mọi thao tác đi qua `useAction`.** Không màn hình nào được tự quản lý cặp `busy`/`notice`
  riêng — đã có lúc làm vậy và kết quả là 5 trên 9 thao tác thiếu ít nhất một trạng thái.
  `tests/threeStateContract.test.ts` quét mã nguồn để chặn việc này, nên vi phạm sẽ làm CI đỏ.
- **Giao diện không phải nơi quyết định quyền.** Ẩn nút theo permission chỉ để thân thiện; API
  vẫn phải kiểm quyền độc lập.

## Vai trò trong nhóm

| Thành viên | Vai trò | Chịu trách nhiệm |
|---|---|---|
| Nguyễn Bảo Long | Owner · Tech Lead · BA · QA | Chốt phạm vi, quyết định kiến trúc, duyệt PR, chủ trì kiểm thử và UAT |
| Nguyễn Huy Kiên | Developer · Tester | Hiện thực, viết test cho phần mình làm, kiểm chéo phần của Trường |
| Lê Sơn Trường | Developer · Tester | Hiện thực, viết test cho phần mình làm, kiểm chéo phần của Kiên |

Người duyệt không phải là người vừa viết mã đó. Mọi PR cần test đi kèm trước khi xin duyệt.

## Test

Mỗi thay đổi hành vi cần test tương ứng. Một số hướng dẫn cụ thể:

- **Test tên theo mã test case** khi nó phủ một mục trong ma trận kiểm chứng
  (`TCBIZ03_...`), để đối chiếu được với ma trận truy vết ở `utils/docs/Architecture.md` mục 9.2.
- **Test đồng thời** dùng `TaskCompletionSource` làm vạch xuất phát để mọi request thực sự chạy
  cùng lúc, thay vì gọi tuần tự rồi hy vọng.
- **Test kiến trúc** (quét mã nguồn) phải được kiểm chứng là *bắt được vi phạm*: cố tình phá,
  chạy lại, xác nhận đỏ, rồi khôi phục. Một guard test không bao giờ đỏ là guard test vô dụng.

## Báo lỗi

Mở issue kèm: cách tái hiện, kết quả mong đợi, kết quả thực tế, và `correlationId` nếu có —
mọi phản hồi lỗi của API đều mang trường này để tra log.

Lỗ hổng bảo mật: **đừng** mở issue công khai, xem [`SECURITY.md`](SECURITY.md).
