#!/usr/bin/env bash
# Chuyển máy sang môi trường PROD / deploy.
#
#   ./infra/scripts/use-prod.sh
#   ./infra/scripts/use-prod.sh --force     # bỏ luôn chỉnh sửa tay đang có trong .env
#
# Chép .env.prod đè lên .env. Xem infra/scripts/env-switch.sh để biết nó kiểm những gì.
set -euo pipefail
exec "$(dirname "$0")/env-switch.sh" prod "$@"
