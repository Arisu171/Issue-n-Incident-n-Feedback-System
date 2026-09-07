# Load test — TC-NFR-01

Bằng chứng cho **NFR-PERF-01**, **NFR-PERF-02** và là câu trả lời cho **ISS-05**
(*"ngưỡng p95 500 ms là giả định, chưa đo trên dữ liệu thật cỡ 50k bản ghi"*).

## Cách chạy

```bash
docker compose up -d --wait              # 1. dựng hệ thống
./infra/loadtest/seed-dataset.sh         # 2. sinh 50.000 incident + ~100.000 dòng lịch sử
./infra/loadtest/run.sh                  # 3. đo đường đọc và đường ghi
./infra/loadtest/run-contention.sh       # 4. đo PATCH /status dưới tranh chấp khóa
```

Không cần cài k6 lên máy — `run.sh` chạy ảnh `grafana/k6` với `--network host`.

`seed-dataset.sh` chèn thẳng bằng SQL thay vì gọi API, vì mục tiêu là **dựng khối lượng dữ liệu
để đo**, không phải đo chính quá trình chèn. Dữ liệu vẫn tôn trọng mọi check constraint của
schema và phân bố theo đúng tình huống README mô tả: khoảng 200 sự cố chưa đóng, phần còn lại
đã `Resolved`.

## Kịch bản

| Scenario | NFR | Tải | Endpoint |
| :--- | :--- | :--- | :--- |
| `read_path` | NFR-PERF-01 | 100 VU / 30 s | `GET /api/incidents` (lọc + phân trang) và `GET /api/incidents/{id}` |
| `write_path` | NFR-PERF-02 | 50 VU / 30 s | `POST /api/incidents` |
| `contention` | NFR-PERF-01 + mục 6.5 | 50 VU cùng đánh vào **một** sự cố | `PATCH /api/incidents/{id}/status` |
| `throughput` | NFR-PERF-01 | 50 VU, mỗi VU một sự cố riêng | `PATCH /api/incidents/{id}/status` |

## Kết quả đo được

Môi trường: Docker Desktop trên Windows 11, PostgreSQL 16-alpine, API .NET 8 trong container,
**50.000 incident và 99.400 dòng lịch sử** trong database.

| Phép đo | Ngưỡng | Đo được | Kết quả |
| :--- | ---: | ---: | :---: |
| `GET /api/incidents` p95 | < 500 ms | **50,8 ms** | ✅ |
| `GET /api/incidents/{id}` p95 | < 500 ms | **34,4 ms** | ✅ |
| `POST /api/incidents` p95 | < 500 ms | **12,1 ms** | ✅ |
| Tỷ lệ lỗi | < 1 % | **0,00 %** | ✅ |

Số liệu bổ sung: p99 của đường đọc là 84,8 ms, giá trị lớn nhất 451 ms; thông lượng đạt
**907 request/giây** với 59.477 request và **0 request lỗi**.

### Tranh chấp khóa trên `PATCH /status`

Đây là phép đo riêng cho `SELECT … FOR UPDATE` của mục 6.5. Đo đường ghi mà mỗi VU thao tác
trên một sự cố khác nhau sẽ không nói lên điều gì — phải cho nhiều VU **cùng tranh một bản
ghi** mới thấy chi phí thật của việc khóa hàng.

| Phép đo | Kỳ vọng | Đo được | Kết quả |
| :--- | ---: | ---: | :---: |
| Số request thắng cuộc trên một sự cố | đúng **1** | **1** | ✅ |
| Số request nhận 409 | 49 | **49** | ✅ |
| Số request có mã trạng thái ngoài `200`/`409` | 0 | **0** | ✅ |
| p95 khi phải xếp hàng chờ khóa | < 500 ms | **63,4 ms** | ✅ |
| p95 khi không tranh chấp | < 500 ms | **102,0 ms** | ✅ |

50 kỹ thuật viên bấm "chuyển trạng thái" cùng một khoảnh khắc trên cùng một sự cố: **đúng một
người thành công**, 49 người còn lại nhận 409 kèm `allowedNextStatus`, không ai nhận 500 và
không request nào timeout. Đây chính là câu trả lời cho câu hỏi phản biện số 3 của README.

Bổ sung thêm: bốn test trong `ConcurrencyTests.cs` kiểm chứng cùng tính chất đó ở tầng
integration, gồm cả trường hợp nhiều người cùng bấm `Resolved` (chỉ một `resolved_at` được ghi)
và cùng tạo user trùng email (đúng một `201`, không có `500`).

## Đọc kết quả này thế nào

Cả ba ngưỡng đều đạt với biên độ khoảng **10 lần**. Điều đó nói lên hai điều:

1. **Ngưỡng 500 ms của mục 4.4 quá rộng so với tải thực tế của hệ thống.** Với 30–50 lần đóng
   sự cố mỗi ngày và khoảng 200 bản ghi chưa đóng, hệ thống còn rất nhiều dư địa. Nhóm đề xuất
   siết ngưỡng xuống **p95 < 150 ms** ở lần rà soát NFR kế tiếp để chỉ số có ý nghĩa cảnh báo
   thật, thay vì luôn xanh.
2. **Partial index của mục 5.6 đang phát huy tác dụng.** Truy vấn danh sách lọc theo `status`
   chạy trên tập chỉ mục 400 dòng thay vì toàn bộ 50.000 bản ghi — đây là lý do p95 giữ được ở
   mức 50 ms dù bảng đã lớn.

## Giới hạn của phép đo

- Đo trên **một máy cá nhân**, API và database dùng chung tài nguyên. Kết quả ở môi trường
  production có ingress và managed PostgreSQL sẽ khác.
- Kịch bản tranh chấp chỉ chạy **một vòng** trên **một** sự cố. Tình huống thật hiếm khi có
  50 người bấm cùng lúc; phép đo này là giới hạn trên, không phải mức tải điển hình.
- `write_path` để lại dữ liệu rác trong database. Dọn bằng:

```bash
docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  -c "delete from incidents where title like 'Load test —%' or title like 'Sự cố mẫu #%'"
```
