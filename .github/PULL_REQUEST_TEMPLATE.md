## Thay đổi gì

<!-- Mô tả ngắn. Phần "vì sao" quan trọng hơn phần "cái gì" — cái gì đã nằm trong diff. -->

Closes #

## Vì sao

<!-- Vấn đề nào được giải quyết. Nếu sửa lỗi: lỗi biểu hiện ra sao, nguyên nhân gốc là gì. -->

## Kiểm chứng

<!-- Bạn đã chạy gì để tin rằng thay đổi này đúng. Dán kết quả nếu có ích. -->

- [ ] `dotnet test` xanh
- [ ] `npm test` và `npm run typecheck` xanh
- [ ] `docker compose up --build -d --wait` cho cả ba service healthy
- [ ] Đã thêm test cho hành vi mới, hoặc giải thích vì sao không cần

## Ảnh hưởng

- [ ] Có đổi schema → đã kèm migration, chạy được trên database rỗng
- [ ] Có đổi API contract → đã cập nhật OpenAPI và collection Postman
- [ ] Có đổi biến môi trường → đã cập nhật `.env.example` và tài liệu
- [ ] Có lệch khỏi bản thiết kế → đã ghi vào `public/docs/decisions.md`
