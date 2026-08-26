'use client';

import { LOCALES, useLocale } from '@/lib/i18n';

/**
 * Nút đổi ngôn ngữ trên thanh trên, đặt cạnh nút chủ đề và dùng đúng lớp áo của nó
 * (`gh-iconbtn`, vuông 34px) để hai nút không lệch nhau.
 *
 * Chỉ có hai ngôn ngữ nên bấm là đảo qua lại, không cần menu thả xuống. Nhãn hiện là mã ngôn
 * ngữ **đang dùng** (`VI` / `EN`), còn `title` và `aria-label` nói rõ bấm sẽ đổi sang gì —
 * người dùng không phải đoán nhãn đang mô tả trạng thái hay hành động.
 */
export function LocaleToggle() {
  const { locale, setLocale } = useLocale();
  const current = LOCALES.find((l) => l.code === locale) ?? LOCALES[0];
  const next = LOCALES[(LOCALES.indexOf(current) + 1) % LOCALES.length];

  return (
    <button
      type="button"
      className="btn btn-secondary gh-iconbtn locale-toggle"
      onClick={() => setLocale(next.code)}
      title={`${current.label} — ${next.label}`}
      aria-label={`${current.label}. ${next.label}.`}
    >
      {current.short}
    </button>
  );
}
