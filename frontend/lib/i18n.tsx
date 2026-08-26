'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { en } from './messages/en';

/**
 * i18n nhẹ, tự viết — không thêm phụ thuộc, đúng tinh thần frontend này (không dùng thư viện UI).
 *
 * ## Khoá dịch là chính chuỗi tiếng Việt
 *
 * Kiểu gettext, không phải tên khoá tự đặt. Ba lý do:
 *   1. Đọc component vẫn thấy ngay câu chữ thật, không phải tra bảng.
 *   2. Thiếu bản dịch thì rơi về tiếng Việt, không hiện khoá thô cho người dùng.
 *   3. Không phải bịa và bảo trì hàng trăm tên khoá cho một sản phẩm có đúng hai ngôn ngữ.
 *
 * Đổi lại: sửa câu tiếng Việt thì phải sửa khoá tương ứng trong `messages/en.ts`.
 * `npm run i18n:check` soát việc đó bằng máy.
 *
 * ## `tr()` là hàm cấp module, không phải hook
 *
 * Tên là `tr` chứ không phải `t` vì mã nguồn đã dùng `t` khắp nơi làm biến vòng lặp cho
 * ticket và issue type; đặt tên `t` sẽ bị các biến đó che mất.
 *
 * Nhờ vậy gọi được ở mọi nơi — trong component, trong hàm xử lý sự kiện, trong hằng số cấp
 * module — mà không phải luồn hook qua từng chỗ. Ngôn ngữ hiện hành giữ trong một biến cấp
 * module, chỉ được ghi từ trình duyệt.
 *
 * Để cây React vẽ lại khi đổi ngôn ngữ, provider gắn `key={locale}` cho phần con: đổi ngôn ngữ
 * là gắn lại toàn bộ cây. Mất state cục bộ của component, nhưng đổi ngôn ngữ là việc hiếm và
 * người dùng vốn mong đợi cả trang thay đổi.
 *
 * ## Từ vựng sản phẩm không dịch
 *
 * Issues, Incident, Feedback, Board, Inbox, Labels, Milestones, SLA, RBAC, Open, Closed giữ
 * nguyên ở **cả hai** ngôn ngữ — đây là tên gọi trong bản thiết kế và trên menu. Xem `LEXICON`.
 */

export type Locale = 'vi' | 'en';

export const LOCALES: { code: Locale; label: string; short: string }[] = [
  { code: 'vi', label: 'Tiếng Việt', short: 'VI' },
  { code: 'en', label: 'English', short: 'EN' },
];

const STORAGE_KEY = 'locale';

/**
 * Từ vựng sản phẩm — cố ý không dịch. Liệt kê ở đây để lần sau không ai "dịch cho đều".
 */
export const LEXICON = [
  'Issues', 'Incident', 'Feedback', 'Board', 'Inbox', 'Search', 'Labels', 'Milestones',
  'Settings', 'RBAC', 'SLA', 'Open', 'Closed',
] as const;

const DICTIONARIES: Record<Locale, Record<string, string>> = { vi: {}, en };

/**
 * Ngôn ngữ hiện hành. Mặc định 'vi' và **chỉ** đổi từ trình duyệt (provider gọi trong effect),
 * nên khi dựng trên máy chủ giá trị luôn là 'vi' — không có chuyện request này ảnh hưởng
 * request kia.
 */
let activeLocale: Locale = 'vi';

/**
 * Dịch một chuỗi. `vars` thay các chỗ giữ chỗ dạng `{ten}`.
 * Không có bản dịch thì trả lại nguyên chuỗi nguồn.
 */
export function tr(source: string, vars?: Record<string, unknown>): string {
  let out = DICTIONARIES[activeLocale][source] ?? source;
  if (vars) {
    for (const [name, value] of Object.entries(vars)) {
      out = out.split(`{${name}}`).join(String(value));
    }
  }
  return out;
}

interface Ctx {
  locale: Locale;
  setLocale: (next: Locale) => void;
}

const LocaleContext = createContext<Ctx>({ locale: 'vi', setLocale: () => undefined });

export function I18nProvider({ children }: { children: React.ReactNode }) {
  // Lần render đầu luôn là 'vi' để bản dựng trên máy chủ và bản thủy hoá trên trình duyệt khớp
  // nhau. Lựa chọn thật đọc từ localStorage ngay sau khi gắn, nên người dùng English thấy một
  // nháy tiếng Việt rất ngắn — đánh đổi có chủ đích để khỏi cần routing theo locale.
  const [locale, setStored] = useState<Locale>('vi');

  useEffect(() => {
    let saved: string | null = null;
    try { saved = localStorage.getItem(STORAGE_KEY); } catch { /* trình duyệt chặn storage */ }
    if (saved === 'vi' || saved === 'en') {
      activeLocale = saved;
      setStored(saved);
    }
  }, []);

  useEffect(() => {
    document.documentElement.lang = locale;
  }, [locale]);

  const setLocale = useCallback((next: Locale) => {
    activeLocale = next;
    setStored(next);
    try { localStorage.setItem(STORAGE_KEY, next); } catch { /* bỏ qua */ }
  }, []);

  const value = useMemo(() => ({ locale, setLocale }), [locale, setLocale]);

  // `key` buộc cả cây gắn lại khi đổi ngôn ngữ, nhờ vậy mọi `t()` được tính lại.
  return (
    <LocaleContext.Provider value={value}>
      <div key={locale} className="locale-root">{children}</div>
    </LocaleContext.Provider>
  );
}

export function useLocale(): Ctx {
  return useContext(LocaleContext);
}
