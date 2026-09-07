#!/usr/bin/env bash
# TC-NFR-01 (phần bổ sung) — đo PATCH /status dưới tranh chấp khóa SELECT ... FOR UPDATE.
#
#   ./infra/loadtest/run-contention.sh          # mặc định 50 VU
#   VUS=100 ./infra/loadtest/run-contention.sh

set -euo pipefail

cd "$(dirname "$0")/../.."

if [ ! -f .env ]; then
  echo "Không tìm thấy .env. Chạy: cp .env.example .env" >&2
  exit 1
fi

set -a
# shellcheck disable=SC1091
source .env
set +a

BASE_URL="${BASE_URL:-http://localhost:${API_PORT:-8080}}"

if ! curl -fsS "${BASE_URL}/api/health" > /dev/null; then
  echo "API chưa healthy tại ${BASE_URL}. Chạy: docker compose up -d --wait" >&2
  exit 1
fi

echo "Đo tranh chấp khóa trên ${BASE_URL} với ${VUS:-50} VU"
echo "  Kỳ vọng: đúng 1 request thắng (200), phần còn lại 409, không có 500 hay timeout."
echo

docker run --rm -i --network host \
  -e BASE_URL="$BASE_URL" \
  -e EMAIL="${SEED_ADMIN_EMAIL}" \
  -e PASSWORD="${SEED_ADMIN_PASSWORD}" \
  -e VUS="${VUS:-50}" \
  grafana/k6:latest run --summary-trend-stats "avg,min,med,p(95),p(99),max" - \
  < infra/loadtest/k6-status-contention.js
