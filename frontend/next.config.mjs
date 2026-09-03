/** @type {import('next').NextConfig} */
const nextConfig = {
  // Ảnh runtime nhỏ gọn cho docker compose (CNT-01) — Dockerfile chép thẳng .next/standalone.
  //
  // Trên Vercel thì KHÔNG bật: nền tảng đó tự đóng gói theo cách riêng, `standalone` chỉ tạo
  // thêm một bản server không ai chạy. Vercel luôn đặt sẵn biến VERCEL trong môi trường build.
  output: process.env.VERCEL ? undefined : 'standalone',
  reactStrictMode: true,
};

export default nextConfig;
