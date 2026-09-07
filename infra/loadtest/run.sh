#!/usr/bin/env bash
# TC-NFR-01 — chạy load test bằng k6 trong container, không cần cài k6 lên máy.
#
#   ./infra/loadtest/run.sh
#
# Yêu cầu: `docker compose up -d --wait` đã chạy và .env đã có SEED_ADMIN_PASSWORD.

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

echo "Đo tải trên ${BASE_URL}"
echo "  NFR-PERF-01  đường đọc   100 VU / 30s   p95 < 500 ms"
echo "  NFR-PERF-02  đường ghi    50 VU / 30s   p95 < 500 ms"
echo

# --network host để container k6 gọi được cổng đã publish của compose.
docker run --rm -i --network host \
  -e BASE_URL="$BASE_URL" \
  -e EMAIL="${SEED_ADMIN_EMAIL}" \
  -e PASSWORD="${SEED_ADMIN_PASSWORD}" \
  grafana/k6:latest run --summary-trend-stats "avg,min,med,p(95),p(99),max" - \
  < infra/loadtest/k6-protected-api.js
