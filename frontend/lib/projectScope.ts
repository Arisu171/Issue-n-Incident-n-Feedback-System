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
/** Slug của project ảo chứa dữ liệu chưa gắn project nào. */
export const UNCATEGORIZED = 'uncategorized';

export function isUncategorized(slug: string | null | undefined): boolean {
  return slug?.toLowerCase() === UNCATEGORIZED;
}

/**
 * Những màn hình có nghĩa với `uncategorized`.
 *
 * Danh sách này KHÔNG tuỳ tiện: nó bám đúng những bảng có `project_id` cho phép null —
 * `incidents` và `feedbacks`. Ticket, nhãn, mốc phát hành, template đều bắt buộc thuộc một
 * project, nên "ticket chưa phân loại" là thứ không tồn tại. Trước đây giao diện vẫn cho đi
 * vào và người dùng nhận "Không tìm thấy project 'uncategorized'" — một lỗi kỹ thuật rò ra
 * ngoài, trong khi câu trả lời đúng là "mục này không có phần đó".
 */
export const UNCATEGORIZED_SECTIONS = ['incidents', 'feedbacks'] as const;

export function supportsUncategorized(section: string | null | undefined): boolean {
  return !!section && (UNCATEGORIZED_SECTIONS as readonly string[]).includes(section);
}

/**
 * Màn hình mặc định khi đi tới một project mà chưa nói rõ vào phần nào.
 *
 * Với project thật là Issues. Với mục chưa phân loại thì Issues không tồn tại — ticket bắt buộc
 * thuộc một project — nên phải là màn hình đầu tiên mà nó thật sự có.
 */
export function defaultSection(slug: string | null | undefined): string {
  return isUncategorized(slug) ? UNCATEGORIZED_SECTIONS[0] : 'issues';
}

/**
 * Project nào **đáng lẽ** đang được chọn, sau khi biết danh sách project mình với tới.
 *
 * Sinh ra để chốt một quy tắc mà trước đây nằm rải trong thân effect của thanh trên cùng, và
 * nằm sai: chốt "slug đang chọn còn trong danh sách không, không còn thì về cái đầu tiên" vốn
 * để xử lý việc khách tự rời một dự án. Nhưng `uncategorized` là project **ảo**, cố ý không bao
 * giờ có trong `GET /api/projects`, nên phép kiểm đó luôn trượt với nó. Effect lại chạy mỗi lần
 * đổi đường dẫn, nên đang ở mục chưa phân loại mà bấm bất kỳ tab nào là bị đá về dự án thật đầu
 * tiên ngay lập tức — nhìn hệt như "tab đó không hỗ trợ mục chưa phân loại", trong khi thật ra
 * chưa màn hình nào kịp được mở.
 *
 * Project ảo có điều kiện hiển thị riêng — quyền `project.member.manage`, đúng bằng
 * `VirtualProject.CanSee` ở server — nên phải kiểm theo đúng điều kiện đó thay vì theo danh sách.
 *
 * @param active     slug đang chọn, `null` nếu chưa chọn gì
 * @param accessible slug của các project thật mà người này với tới
 * @param canClassify người này có quyền vào mục chưa phân loại hay không
 * @returns slug nên được chọn — nơi gọi tự so với `active` rồi mới ghi
 */
export function resolveActiveProject(
  active: string | null | undefined,
  accessible: readonly string[],
  canClassify: boolean,
): string | null {
  // Chưa chọn gì thì để nguyên. Tự chọn hộ một project sẽ làm người vừa rời dự án cuối cùng
  // bỗng thấy mình đang đứng trong một dự án mà họ không hề mở.
  if (!active) return null;

  if (isUncategorized(active)) {
    return canClassify ? active : (accessible[0] ?? null);
  }

  return accessible.includes(active) ? active : (accessible[0] ?? null);
}

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
