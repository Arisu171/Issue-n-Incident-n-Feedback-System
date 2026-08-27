'use client';

import { useEffect, useState } from 'react';
import { tr } from '@/lib/i18n';

/**
 * Ba trạng thái chứ không phải hai: `system` là mặc định thật sự, còn `light`/`dark` là lựa
 * chọn tường minh của người dùng. Nếu chỉ có hai trạng thái thì người đã bấm một lần sẽ không
 * bao giờ quay lại được chế độ đi theo hệ điều hành.
 *
 * Giá trị lưu ở `localStorage`, và được đọc lại trong một script chạy trước khi trang vẽ
 * (xem `app/layout.tsx`) để không có cú nhấp nháy trắng khi tải trang ở chế độ tối.
 */
export type ThemeChoice = 'system' | 'light' | 'dark';

export const THEME_KEY = 'theme';

const NEXT: Record<ThemeChoice, ThemeChoice> = {
  system: 'light',
  light: 'dark',
  dark: 'system',
};

const LABEL: Record<ThemeChoice, string> = {
  system: 'Theo hệ thống',
  light: 'Sáng',
  dark: 'Tối',
};

function apply(choice: ThemeChoice) {
  const root = document.documentElement;
  if (choice === 'system') {
    delete root.dataset.theme;
  } else {
    root.dataset.theme = choice;
  }

  try {
    if (choice === 'system') {
      localStorage.removeItem(THEME_KEY);
    } else {
      localStorage.setItem(THEME_KEY, choice);
    }
  } catch {
    // Chế độ ẩn danh hoặc trình duyệt chặn lưu trữ: chủ đề vẫn đổi cho phiên hiện tại.
  }
}

function readStored(): ThemeChoice {
  try {
    const raw = localStorage.getItem(THEME_KEY);
    return raw === 'light' || raw === 'dark' ? raw : 'system';
  } catch {
    return 'system';
  }
}

export function ThemeToggle() {
  // Server không biết lựa chọn của người dùng nên lần render đầu phải trung lập,
  // nếu không markup của server và client sẽ lệch nhau.
  const [choice, setChoice] = useState<ThemeChoice | null>(null);

  useEffect(() => {
    setChoice(readStored());
  }, []);

  function cycle() {
    const next = NEXT[choice ?? 'system'];
    apply(next);
    setChoice(next);
  }

  const current = choice ?? 'system';

  return (
    <button
      type="button"
      className="btn btn-secondary theme-toggle"
      data-pending={choice === null}
      onClick={cycle}
      aria-label={tr('Chủ đề: {v0}. Bấm để đổi sang {v1}.', { v0: tr(LABEL[current]), v1: tr(LABEL[NEXT[current]]) })}
      title={tr('Chủ đề: {v0}', { v0: tr(LABEL[current]) })}
    >
      <Icon choice={current} />
    </button>
  );
}

function Icon({ choice }: { choice: ThemeChoice }) {
  const common = {
    viewBox: '0 0 24 24',
    fill: 'none',
    stroke: 'currentColor',
    strokeWidth: 2,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    'aria-hidden': true,
  };

  if (choice === 'dark') {
    return (
      <svg {...common}>
        <path d="M20 14.5A8.5 8.5 0 1 1 9.5 4a6.8 6.8 0 0 0 10.5 10.5z" fill="currentColor" stroke="none" />
      </svg>
    );
  }

  if (choice === 'light') {
    return (
      <svg {...common}>
        <circle cx="12" cy="12" r="9" fill="currentColor" stroke="none" />
      </svg>
    );
  }

  return (
    <svg {...common}>
      <rect x="2" y="4" width="20" height="13" rx="2" />
      <path d="M8 21h8M12 17v4" />
    </svg>
  );
}
