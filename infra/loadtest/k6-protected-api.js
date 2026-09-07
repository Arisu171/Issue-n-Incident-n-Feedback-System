import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

/**
 * TC-NFR-01 · NFR-PERF-01/02 — đo p95 của API nghiệp vụ dưới tải đồng thời.
 *
 * Mục 4.4 đặt hai ngưỡng:
 *   NFR-PERF-01  p95 protected API      ≤ 500 ms @ 100 concurrent
 *   NFR-PERF-02  p95 POST /api/incidents < 500 ms @ 50 concurrent
 *
 * ISS-05 ghi rõ đây là giả định chưa đo trên dữ liệu thật cỡ 50k bản ghi, nên kịch bản này
 * seed dữ liệu trước rồi mới đo (xem seed-dataset.sh).
 *
 * Chạy:
 *   docker run --rm -i --network host \
 *     -e BASE_URL=http://localhost:8080 -e EMAIL=... -e PASSWORD=... \
 *     grafana/k6 run - < infra/loadtest/k6-protected-api.js
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const EMAIL = __ENV.EMAIL;
const PASSWORD = __ENV.PASSWORD;

const listTrend = new Trend('list_incidents_ms', true);
const detailTrend = new Trend('detail_incident_ms', true);
const createTrend = new Trend('create_incident_ms', true);

export const options = {
  scenarios: {
    // NFR-PERF-01 — đường đọc, 100 người dùng đồng thời.
    read_path: {
      executor: 'constant-vus',
      vus: 100,
      duration: '30s',
      exec: 'readPath',
      tags: { nfr: 'NFR-PERF-01' },
    },
    // NFR-PERF-02 — đường ghi, 50 người dùng đồng thời.
    write_path: {
      executor: 'constant-vus',
      vus: 50,
      duration: '30s',
      exec: 'writePath',
      startTime: '35s',
      tags: { nfr: 'NFR-PERF-02' },
    },
  },
  thresholds: {
    'list_incidents_ms': ['p(95)<500'],
    'detail_incident_ms': ['p(95)<500'],
    'create_incident_ms': ['p(95)<500'],
    'http_req_failed': ['rate<0.01'],
  },
};

/** Đăng nhập một lần cho toàn bộ VU; token 15 phút đủ cho kịch bản ~70 giây. */
export function setup() {
  if (!EMAIL || !PASSWORD) {
    throw new Error('Thiếu biến môi trường EMAIL và PASSWORD.');
  }

  const response = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ email: EMAIL, password: PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } },
  );

  if (response.status !== 200) {
    throw new Error(`Đăng nhập thất bại: HTTP ${response.status} — ${response.body}`);
  }

  const token = response.json('accessToken');

  // Lấy sẵn một trang sự cố để đường đọc có id thật mà truy vấn chi tiết.
  const page = http.get(`${BASE_URL}/api/incidents?pageSize=100`, {
    headers: { Authorization: `Bearer ${token}` },
  });

  return {
    token,
    ids: page.json('items').map((i) => i.id),
  };
}

function authHeaders(data) {
  return {
    headers: {
      Authorization: `Bearer ${data.token}`,
      'Content-Type': 'application/json',
    },
  };
}

export function readPath(data) {
  const params = authHeaders(data);

  const list = http.get(
    `${BASE_URL}/api/incidents?status=Investigating&page=1&pageSize=20`,
    { ...params, tags: { name: 'GET /api/incidents' } },
  );
  listTrend.add(list.timings.duration);
  check(list, { 'list 200': (r) => r.status === 200 });

  if (data.ids.length > 0) {
    const id = data.ids[Math.floor(Math.random() * data.ids.length)];
    const detail = http.get(`${BASE_URL}/api/incidents/${id}`, {
      ...params,
      tags: { name: 'GET /api/incidents/{id}' },
    });
    detailTrend.add(detail.timings.duration);
    check(detail, { 'detail 200': (r) => r.status === 200 });
  }

  sleep(0.1);
}

export function writePath(data) {
  const params = authHeaders(data);

  const payload = JSON.stringify({
    title: `Load test — sự cố ${__VU}-${__ITER}`,
    description: 'Bản ghi sinh bởi k6, xóa được bằng script dọn dẹp.',
    severity: 'Low',
  });

  const created = http.post(`${BASE_URL}/api/incidents`, payload, {
    ...params,
    tags: { name: 'POST /api/incidents' },
  });
  createTrend.add(created.timings.duration);
  check(created, { 'create 201': (r) => r.status === 201 });

  sleep(0.1);
}
