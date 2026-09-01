#!/usr/bin/env bash
# Chuyển máy sang môi trường TEST / phát triển.
#
#   ./infra/scripts/use-test.sh
#   ./infra/scripts/use-test.sh --force     # bỏ luôn chỉnh sửa tay đang có trong .env
#
# Chép .env.test đè lên .env. Xem infra/scripts/env-switch.sh để biết nó kiểm những gì.
set -euo pipefail
exec "$(dirname "$0")/env-switch.sh" test "$@"
