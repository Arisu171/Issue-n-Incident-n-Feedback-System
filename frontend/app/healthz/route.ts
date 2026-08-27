/**
 * Healthcheck của service web trong docker compose (NFR-PORT-01 — cả ba service phải healthy).
 * Chỉ báo tiến trình Next.js còn sống; không gọi sang API để tránh web unhealthy dây chuyền
 * khi API tạm thời lỗi.
 */
export const dynamic = 'force-dynamic';

export function GET() {
  return Response.json({ status: 'Healthy', service: 'web' });
}
