#!/usr/bin/env bash
# Mục 6.9 "Backup / DR" và mục 5.7 "Backup / restore" — hai mục này bị đánh dấu [XÁC NHẬN]
# và chưa có quy trình chạy được. Script này biến nó thành thao tác thực hiện được.
#
#   ./infra/scripts/backup.sh                    # tạo bản sao lưu vào infra/backups/
#   BACKUP_DIR=/mnt/nas ./infra/scripts/backup.sh
#
# Định dạng custom (-Fc) để restore chọn lọc được từng bảng và nén sẵn.

set -euo pipefail

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

BACKUP_DIR="${BACKUP_DIR:-infra/backups}"
mkdir -p "$BACKUP_DIR"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET="${BACKUP_DIR}/${POSTGRES_DB}-${STAMP}.dump"

echo "Sao lưu '${POSTGRES_DB}' → ${TARGET}"

docker compose exec -T db pg_dump \
  -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
  --format=custom --no-owner --no-privileges > "$TARGET"

SIZE="$(du -h "$TARGET" | cut -f1)"
echo "Xong — ${SIZE}"

# RPO phụ thuộc tần suất chạy script này. Với môn học, chạy tay trước mỗi buổi demo là đủ;
# production cần cron hoặc snapshot của managed PostgreSQL (xem infra/README.md).
