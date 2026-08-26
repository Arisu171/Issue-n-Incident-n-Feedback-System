'use client';

import { usePathname } from 'next/navigation';
import { useCallback, useEffect, useSyncExternalStore } from 'react';

const KEY = 'lastProject';

/**
 * Project đang chọn — **một nguồn duy nhất** cho thanh trên cùng và thanh tab.
 *
 * Trước đây thanh trên cùng đọc slug từ đường dẫn còn thanh tab tự giữ một bản trong
 * `localStorage`. Hệ quả: mọi màn hình không thuộc project nào (Search, Inbox, Boards, Projects)
 * đều làm ô chọn project rơi về "— chọn project —", trong khi hàng tab ngay bên dưới vẫn trỏ
 * đúng project cũ. Hai chỗ trả lời khác nhau cho cùng một câu hỏi.
 *
 * Đây là store ngoài React (`useSyncExternalStore`) chứ không phải context: nó phải sống sót qua
 * chuyển trang và được đọc từ hai cây component không có tổ tiên chung nào ngoài layout gốc.
 */
const listeners = new Set<() => void>();

let cached: string | null = null;
let loaded = false;

function read(): string | null {
  if (!loaded) {
    try { cached = localStorage.getItem(KEY); } catch { cached = null; }
    loaded = true;
  }
  return cached;
}

function emit() {
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** Đọc lựa chọn hiện tại ngoài React — dùng trong callback, nơi không gọi hook được. */
export function activeProject(): string | null {
  return read();
}

/** Đặt project đang chọn. Không điều hướng — nơi gọi tự quyết định có đổi trang hay không. */
export function setActiveProject(slug: string | null): void {
  read();
  if (cached === slug) return;
  cached = slug;
  try {
    if (slug) localStorage.setItem(KEY, slug); else localStorage.removeItem(KEY);
  } catch {
    // Trình duyệt chặn lưu trữ: lựa chọn vẫn giữ cho phiên hiện tại.
  }
  emit();
}

/** Slug lấy từ đường dẫn, hoặc `null` trên màn hình không thuộc project nào. */
export function projectFromPath(pathname: string): string | null {
  return pathname.match(/^\/projects\/([^/]+)/)?.[1] ?? null;
}

/**
 * Project đang chọn: ưu tiên đường dẫn, không có thì lấy lựa chọn gần nhất.
 *
 * `getServerSnapshot` trả `null` để lần dựng trên máy chủ và lần hydrate ở trình duyệt ra cùng
 * một cây; `localStorage` chỉ được đọc ở lần vẽ sau đó.
 */
export function useActiveProject(): string | null {
  const pathname = usePathname();
  const fromPath = projectFromPath(pathname);
  const stored = useSyncExternalStore(subscribe, read, () => null);

  // Đi tới một project là đã chọn nó — không phải bấm thêm vào ô chọn.
  useEffect(() => {
    if (fromPath) setActiveProject(fromPath);
  }, [fromPath]);

  return fromPath ?? stored;
}

/**
 * Danh sách project mình với tới vừa đổi (khách hàng tự tham gia hoặc rời một dự án).
 *
 * Ô chọn project nằm ở thanh trên cùng, tab Projects nằm ở một cây component khác, và cả hai
 * cùng sống trong một lần tải trang — nên không có gì tự bảo cho ô chọn biết là danh sách đã đổi.
 * Thiếu tín hiệu này thì phải chuyển trang hoặc tải lại mới thấy.
 */
const accessListeners = new Set<() => void>();

export function projectAccessChanged(): void {
  for (const listener of accessListeners) listener();
}

export function useProjectAccessChanged(handler: () => void): void {
  const stable = useCallback(handler, [handler]);
  useEffect(() => {
    accessListeners.add(stable);
    return () => { accessListeners.delete(stable); };
  }, [stable]);
}
