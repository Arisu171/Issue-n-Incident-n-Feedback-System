/** @type {import('next').NextConfig} */
const nextConfig = {
  // Ảnh runtime nhỏ gọn cho docker compose (CNT-01).
  output: 'standalone',
  reactStrictMode: true,
};

export default nextConfig;
