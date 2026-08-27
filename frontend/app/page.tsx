import { redirect } from 'next/navigation';

/** Trang chủ = danh sách issue của project mặc định (≈ tab Issues của repo). */
export default function Home() {
  redirect('/projects/support/issues');
}
