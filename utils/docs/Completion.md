# Báo cáo hoàn thiện — Incident & Feedback Tracker

| Trường | Nội dung |
| :--- | :--- |
| Phiên bản | Release 1 — đủ 6 tuần thực hành |
| Ngày cập nhật | 05/09/2026 |
| Trạng thái | Hoàn thiện R1 + đóng gói tuần 4–6 |
| Bằng chứng vận hành | [`public/docs/verification.md`](../../public/docs/verification.md) |
| Demo / UAT | [`demo.md`](../../public/docs/demo.md) · [`uat.md`](../../public/docs/uat.md) |
| Thiết kế | [`Architecture.md`](Architecture.md) |
| Báo cáo cuối 4–5–6 | [`reports/final-week-4-5-6.md`](reports/final-week-4-5-6.md) |

## Tỷ lệ hoàn thành

| Phạm vi | Mức | Ghi chú |
| :--- | ---: | :--- |
| Tuần 1 — RBAC + PostgreSQL + Swagger | **100%** | Tag `week-1` |
| Tuần 2 — Vertical slice Incident / Feedback | **100%** | Tag `week-2` |
| Tuần 3 — JWT + resource auth + hardening | **100%** | Tag `week-3` |
| Tuần 4 — Frontend Next.js + 3-state | **100%** | Tag `week-4` · Vitest 24 |
| Tuần 5 — Docker / metrics / NFR | **100%*** | Tag `week-5` · *compose live phụ thuộc Docker Engine |
| Tuần 6 — UAT / demo / nộp bài | **100%** | Tag `week-6` |
| Architecture R1 (Must / Should in-scope) | **~98%** | UC Must Implemented |
| Out of scope (§1.3) | **0% (có chủ đích)** | Không làm |

**Use Case Must:** 15/15 Implemented.

## Đã làm (tóm tắt)

| Nhóm | Nội dung |
| :--- | :--- |
| RBAC + AuthZ | 5 bảng, JWT, ADR-004, BOLA, hardening |
| Incident / Feedback / Conversations | State machine, history, link, comments/replies, SLA |
| Frontend | 3-state `useAction`, admin CRUD tối thiểu, `api.me()` |
| Vận hành | Compose, health system, metrics, backup, k6, CI + Postman |
| Nộp bài | Demo script, UAT checklist, task tuần 1–6, báo cáo |

## Git tags

`week-1` · `week-2` · `week-3` · `week-4` · `week-5` · `week-6`

## Kết luận

Học kỳ thực hành 6 tuần đóng được Release 1: có thiết kế, có mã, có kiểm chứng, có kịch bản demo
và UAT. Phần còn lại thuộc Out of scope đã ghi rõ — không mở rộng vào tuần cuối.
