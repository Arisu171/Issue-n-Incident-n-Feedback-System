#!/usr/bin/env bash
# ISS-05 — ngưỡng p95 phải được đo trên khối lượng thật, không phải trên bảng rỗng.
# Script sinh 50.000 incident và 100.000 dòng lịch sử trạng thái thẳng vào PostgreSQL.
#
#   ./infra/loadtest/seed-dataset.sh            # mặc định 50000
#   ./infra/loadtest/seed-dataset.sh 5000       # bộ nhỏ để thử nhanh
#
# Chèn bằng SQL thay vì gọi API: mục tiêu là dựng khối lượng dữ liệu để đo, không phải đo
# chính quá trình chèn. Dữ liệu vẫn tôn trọng mọi check constraint của schema.

set -euo pipefail

COUNT="${1:-50000}"
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.yml}"

cd "$(dirname "$0")/../.."

if [ ! -f .env ]; then
  echo "Không tìm thấy .env. Chạy: cp .env.example .env" >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
source .env
set +a

echo "Sinh ${COUNT} incident vào database '${POSTGRES_DB}'..."

docker compose -f "$COMPOSE_FILE" exec -T db psql -v ON_ERROR_STOP=1 \
  -U "$POSTGRES_USER" -d "$POSTGRES_DB" <<SQL
-- Cần ít nhất một user để làm reporter; lấy admin đã seed.
do \$\$
declare
  reporter uuid;
begin
  select id into reporter from users order by created_at limit 1;
  if reporter is null then
    raise exception 'Chưa có user nào. Đặt SEED_ADMIN_PASSWORD trong .env rồi khởi động lại api.';
  end if;

  -- Phân bố xấp xỉ tình huống thật của README: khoảng 200 sự cố chưa đóng,
  -- phần còn lại đã Resolved.
  insert into incidents (
    id, title, description, severity, status,
    reporter_id, created_at, mitigating_at, resolved_at, resolved_by, is_deleted
  )
  select
    gen_random_uuid(),
    'Sự cố mẫu #' || g,
    'Bản ghi sinh bởi seed-dataset.sh để đo hiệu năng.',
    (array['Low','Medium','High','Critical'])[1 + (g % 4)],
    case when g % 250 = 0 then 'Investigating'
         when g % 250 = 1 then 'Mitigating'
         else 'Resolved' end,
    reporter,
    now() - (g || ' minutes')::interval,
    case when g % 250 = 0 then null
         else now() - (g || ' minutes')::interval + interval '30 minutes' end,
    case when g % 250 in (0, 1) then null
         else now() - (g || ' minutes')::interval + interval '2 hours' end,
    case when g % 250 in (0, 1) then null else reporter end,
    false
  from generate_series(1, ${COUNT}) g;

  -- Lịch sử tương ứng: 1 dòng cho Mitigating, 2 dòng cho Resolved.
  insert into incident_status_history (id, incident_id, from_status, to_status, changed_by, changed_at)
  select gen_random_uuid(), i.id, 'Investigating', 'Mitigating', reporter, i.mitigating_at
  from incidents i
  where i.mitigating_at is not null
    and not exists (select 1 from incident_status_history h where h.incident_id = i.id);

  insert into incident_status_history (id, incident_id, from_status, to_status, changed_by, changed_at)
  select gen_random_uuid(), i.id, 'Mitigating', 'Resolved', reporter, i.resolved_at
  from incidents i
  where i.resolved_at is not null
    and (select count(*) from incident_status_history h where h.incident_id = i.id) < 2;
end
\$\$;

analyze incidents;
analyze incident_status_history;

select
  status,
  count(*) as so_luong
from incidents
group by status
order by status;

select 'incident_status_history' as bang, count(*) as so_dong from incident_status_history;
SQL

echo "Xong. Chạy load test bằng: ./infra/loadtest/run.sh"
