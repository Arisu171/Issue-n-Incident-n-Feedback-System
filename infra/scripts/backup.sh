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

set -a
# shellcheck disable=SC1091
source .env
set +a

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
