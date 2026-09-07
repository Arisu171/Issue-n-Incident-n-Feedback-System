import http from 'k6/http';
import exec from 'k6/execution';
import { check } from 'k6';
import { Trend, Counter } from 'k6/metrics';

/**
 * Phép đo còn thiếu của TC-NFR-01: `PATCH /api/incidents/{id}/status` dưới **tranh chấp khóa**.
 *
 * Endpoint này mở transaction và khóa hàng bằng `SELECT ... FOR UPDATE` (mục 6.5). Đo nó bằng
 * cách mỗi VU thao tác trên một sự cố riêng sẽ không nói lên điều gì — phải cho nhiều VU cùng
 * tranh một bản ghi thì mới thấy chi phí thật của việc khóa.
 *
 * Kịch bản chia làm hai phần:
 *   contention  — toàn bộ VU cùng đánh vào MỘT sự cố. Đúng một request thắng (200), phần còn
 *                 lại phải nhận 409 chứ không phải 500 hay timeout.
 *   throughput  — mỗi VU có sự cố riêng, đo thông lượng thực tế của đường ghi có transaction.
 *
 * Chạy: ./infra/loadtest/run-contention.sh
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';
const EMAIL = __ENV.EMAIL;
const PASSWORD = __ENV.PASSWORD;
const VUS = Number(__ENV.VUS || 50);

const contentionTrend = new Trend('status_contention_ms', true);
const throughputTrend = new Trend('status_throughput_ms', true);
const winners = new Counter('contention_winners');
const conflicts = new Counter('contention_conflicts');
const unexpected = new Counter('contention_unexpected');

export const options = {
  scenarios: {
    contention: {
      executor: 'per-vu-iterations',
      vus: VUS,
      iterations: 1,
      exec: 'contention',
    },
    throughput: {
      executor: 'per-vu-iterations',
      vus: VUS,
      iterations: 1,
      exec: 'throughput',
      startTime: '15s',
    },
  },
  thresholds: {
    // Ngay cả khi phải xếp hàng chờ khóa, request vẫn phải trả lời trong ngưỡng NFR.
    'status_contention_ms': ['p(95)<500'],
    'status_throughput_ms': ['p(95)<500'],
    // Không request nào được rơi ra ngoài cặp 200/409.
    'contention_unexpected': ['count==0'],
    // Đúng một request thắng cuộc trên mỗi sự cố tranh chấp.
    'contention_winners': ['count==1'],
  },
};

function login() {
  const response = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ email: EMAIL, password: PASSWORD }),
    { headers: { 'Content-Type': 'application/json' } },
  );
  if (response.status !== 200) {
    throw new Error(`Đăng nhập thất bại: HTTP ${response.status}`);
  }
  return response.json('accessToken');
}

function createIncident(token, title) {
  const response = http.post(
    `${BASE_URL}/api/incidents`,
    JSON.stringify({ title, description: 'Sinh bởi k6-status-contention.', severity: 'Low' }),
    { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } },
  );
  if (response.status !== 201) {
    throw new Error(`Tạo sự cố thất bại: HTTP ${response.status} — ${response.body}`);
  }
  return response.json('id');
}

export function setup() {
  if (!EMAIL || !PASSWORD) {
    throw new Error('Thiếu biến môi trường EMAIL và PASSWORD.');
  }

  const token = login();

  // Một sự cố cho phần tranh chấp, và mỗi VU một sự cố cho phần thông lượng.
  const contended = createIncident(token, 'k6 — sự cố bị tranh chấp');
  const perVu = [];
  for (let i = 0; i < VUS; i++) {
    perVu.push(createIncident(token, `k6 — sự cố riêng của VU ${i + 1}`));
  }

  return { token, contended, perVu };
}

function headers(data) {
  return {
    headers: {
      Authorization: `Bearer ${data.token}`,
      'Content-Type': 'application/json',
    },
  };
}

/** Toàn bộ VU cùng đánh vào một sự cố — đây là phép đo chi phí khóa thật sự. */
export function contention(data) {
  const response = http.patch(
    `${BASE_URL}/api/incidents/${data.contended}/status`,
    JSON.stringify({ targetStatus: 'Mitigating', note: `VU ${__VU}` }),
    { ...headers(data), tags: { name: 'PATCH /status (tranh chấp)' } },
  );

  contentionTrend.add(response.timings.duration);

  if (response.status === 200) {
    winners.add(1);
  } else if (response.status === 409) {
    conflicts.add(1);
  } else {
    unexpected.add(1);
    console.error(`Mã trạng thái ngoài dự kiến: ${response.status} — ${response.body}`);
  }

  check(response, {
    'chỉ nhận 200 hoặc 409': (r) => r.status === 200 || r.status === 409,
  });
}

/** Mỗi lượt chạy một sự cố riêng — đo thông lượng của đường ghi có transaction. */
export function throughput(data) {
  // Không dùng __VU làm chỉ số: hai scenario dùng chung pool VU nên id không liên tục và
  // hai VU khác nhau có thể trỏ vào cùng một sự cố, sinh 409 giả. iterationInTest là duy nhất
  // trong phạm vi scenario nên ánh xạ một-một với danh sách sự cố đã chuẩn bị.
  const id = data.perVu[exec.scenario.iterationInTest % data.perVu.length];

  const response = http.patch(
    `${BASE_URL}/api/incidents/${id}/status`,
    JSON.stringify({ targetStatus: 'Mitigating' }),
    { ...headers(data), tags: { name: 'PATCH /status (riêng biệt)' } },
  );

  throughputTrend.add(response.timings.duration);
  check(response, { 'chuyển trạng thái thành công': (r) => r.status === 200 });
}
