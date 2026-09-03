#!/usr/bin/env bash
# Diễn tập restore (mục 5.7 — "thực hành restore mỗi cuối sprint").
#
#   ./infra/scripts/restore.sh infra/backups/incident_tracker-20260823T120000Z.dump
#
# Restore vào MỘT DATABASE RIÊNG rồi đối chiếu số bản ghi, không ghi đè database đang chạy.
# Đây mới là diễn tập có giá trị: nó chứng minh bản sao lưu dùng được mà không gây rủi ro.

set -euo pipefail

DUMP="${1:-}"
if [ -z "$DUMP" ] || [ ! -f "$DUMP" ]; then
  echo "Cách dùng: $0 <đường-dẫn-file-dump>" >&2
  exit 1
fi

cd "$(dirname "$0")/../.."

set -a
# shellcheck disable=SC1091
source .env
set +a

TARGET_DB="restore_drill_$(date -u +%Y%m%d%H%M%S)"

echo "Tạo database tạm '${TARGET_DB}'..."
docker compose exec -T db psql -U "$POSTGRES_USER" -d postgres \
  -c "CREATE DATABASE \"${TARGET_DB}\""

echo "Nạp bản sao lưu..."
docker compose exec -T db pg_restore \
  -U "$POSTGRES_USER" -d "$TARGET_DB" --no-owner --no-privileges < "$DUMP"

echo
echo "Đối chiếu số bản ghi giữa database đang chạy và bản restore:"
for table in users roles permissions incidents incident_status_history feedbacks; do
  live=$(docker compose exec -T db psql -tAU "$POSTGRES_USER" -d "$POSTGRES_DB" \
    -c "select count(*) from ${table}")
  restored=$(docker compose exec -T db psql -tAU "$POSTGRES_USER" -d "$TARGET_DB" \
    -c "select count(*) from ${table}")
  status="OK"
  [ "$live" = "$restored" ] || status="LỆCH"
  printf "  %-26s live=%-8s restored=%-8s %s\n" "$table" "$live" "$restored" "$status"
done

echo
read -r -p "Xóa database tạm '${TARGET_DB}'? [Y/n] " answer
if [ "${answer:-Y}" != "n" ]; then
  docker compose exec -T db psql -U "$POSTGRES_USER" -d postgres \
    -c "DROP DATABASE \"${TARGET_DB}\" WITH (FORCE)"
  echo "Đã dọn."
else
  echo "Giữ lại '${TARGET_DB}' để kiểm tra thêm."
fi
