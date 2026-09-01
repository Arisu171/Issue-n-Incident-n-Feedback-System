#!/usr/bin/env bash
# Diễn tập restore (mục 5.7 — "thực hành restore mỗi cuối sprint").
#
#   ./infra/scripts/restore.sh infra/backups/gitissues-20260823T120000Z.dump
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

# Đọc đúng khoá cần thay vì `source .env`. Tệp .env KHÔNG phải script shell: một giá trị có
# dấu cách làm hỏng lệnh, còn dấu ` hoặc $( ) thì bị THỰC THI — mà đây là tệp chứa mật khẩu.
env_value() {
  sed -n "s/^$1=//p" .env | tail -1 | sed -e 's/^"//' -e 's/"$//' -e "s/^'//" -e "s/'\$//"
}

POSTGRES_DB="$(env_value POSTGRES_DB)"
POSTGRES_USER="$(env_value POSTGRES_USER)"
: "${POSTGRES_DB:?Thiếu POSTGRES_DB trong .env}"
: "${POSTGRES_USER:?Thiếu POSTGRES_USER trong .env}"

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

# Vì sao không hỏi thẳng bằng `read`: vòng lặp đối chiếu ở trên gọi `docker compose exec -T`,
# và lệnh đó ĐỌC CẠN stdin của script rồi chuyển vào container. Chạy tự động kiểu
# `echo Y | restore.sh` thì tới đây stdin đã hết, `read` trả về khác 0, và `set -e` giết
# script ngay sau khi nó vừa in ra bảng đối chiếu — trông như hỏng dù mọi thứ đều khớp.
# Nên: tự động thì đặt ASSUME_YES=1, còn `read` có lưới đỡ cho trường hợp EOF.
if [ -n "${ASSUME_YES:-}" ]; then
  answer=Y
  echo "ASSUME_YES=1 — tự dọn database tạm '${TARGET_DB}'."
else
  read -r -p "Xóa database tạm '${TARGET_DB}'? [Y/n] " answer || answer=Y
fi

if [ "${answer:-Y}" != "n" ]; then
  docker compose exec -T db psql -U "$POSTGRES_USER" -d postgres \
    -c "DROP DATABASE \"${TARGET_DB}\" WITH (FORCE)"
  echo "Đã dọn."
else
  echo "Giữ lại '${TARGET_DB}' để kiểm tra thêm."
fi
