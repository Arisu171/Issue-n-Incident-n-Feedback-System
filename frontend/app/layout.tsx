import type { Metadata } from 'next';
import './globals.css';
import { TopBar } from '@/components/TopBar';
import { ProjectNav } from '@/components/ProjectNav';
import { I18nProvider } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Git Issues',
  // Metadata dựng trên máy chủ, không theo người dùng, nên không đi qua i18n.
  description: 'Nền tảng xử lý sự cố hướng sự kiện',
};

/**
 * Chạy trước khi trình duyệt vẽ khung hình đầu tiên để không nháy trắng ở chế độ tối
 * (script nội tuyến trong <head>, đọc lựa chọn chủ đề từ localStorage).
 */
const THEME_BOOTSTRAP = `
(function () {
  try {
    var t = localStorage.getItem('theme');
    if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t;
  } catch (e) {}
})();
`;

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="vi" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: THEME_BOOTSTRAP }} />
      </head>
      <body>
        <I18nProvider>
          <TopBar />
          <ProjectNav />
          {/* Khung nội dung dùng chung cho mọi trang: cùng bề rộng, cùng lề, cùng khoảng cách dọc. */}
          <main className="shell">{children}</main>
        </I18nProvider>
      </body>
    </html>
  );
}
