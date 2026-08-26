import { tr } from '@/lib/i18n';
/** Tiện ích hiển thị cho giao diện kiểu GitHub. */

/** Màu chữ tương phản cho chip label (GitHub dùng luminance để chọn đen/trắng). */
export function labelTextColor(hex: string): string {
  const clean = hex.replace('#', '');
  if (!/^[0-9a-fA-F]{6}$/.test(clean)) return '#1f2328';
  const r = parseInt(clean.slice(0, 2), 16) / 255;
  const g = parseInt(clean.slice(2, 4), 16) / 255;
  const b = parseInt(clean.slice(4, 6), 16) / 255;
  const lin = (c: number) => (c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
  const luminance = 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
  return luminance > 0.45 ? '#1f2328' : '#ffffff';
}

/** "3 phút trước", "2 ngày trước"… như GitHub. */
export function timeAgo(iso: string | null | undefined, now: Date = new Date()): string {
  if (!iso) return '';
  const then = new Date(iso).getTime();
  const seconds = Math.max(0, Math.round((now.getTime() - then) / 1000));
  if (seconds < 45) return tr('vừa xong');
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return tr('{v0} phút trước', { v0: minutes });
  const hours = Math.round(minutes / 60);
  if (hours < 24) return tr('{v0} giờ trước', { v0: hours });
  const days = Math.round(hours / 24);
  if (days < 30) return tr('{v0} ngày trước', { v0: days });
  const months = Math.round(days / 30);
  if (months < 12) return tr('{v0} tháng trước', { v0: months });
  return tr('{v0} năm trước', { v0: Math.round(months / 12) });
}

export function formatDate(iso: string | null | undefined): string {
  if (!iso) return '';
  return new Date(iso).toLocaleString('vi-VN', { hour12: false });
}

/** Dựng chuỗi Query DSL (mục 2.7) từ trạng thái bộ lọc; giữ phần người dùng gõ tay. */
export function buildDsl(parts: { state?: 'open' | 'closed' | 'all'; labels?: string[]; milestone?: string; assignee?: string; author?: string; type?: string; sort?: string; text?: string }): string {
  const tokens: string[] = [];
  if (parts.state && parts.state !== 'all') tokens.push(`is:${parts.state}`);
  for (const l of parts.labels ?? []) tokens.push(/\s/.test(l) ? `label:"${l}"` : `label:${l}`);
  if (parts.milestone) tokens.push(/\s/.test(parts.milestone) ? `milestone:"${parts.milestone}"` : `milestone:${parts.milestone}`);
  if (parts.assignee) tokens.push(`assignee:${parts.assignee}`);
  if (parts.author) tokens.push(`author:${parts.author}`);
  if (parts.type) tokens.push(`type:${parts.type}`);
  if (parts.sort) tokens.push(`sort:${parts.sort}`);
  if (parts.text?.trim()) tokens.push(parts.text.trim());
  return tokens.join(' ');
}

/** Đọc `is:` từ DSL để bật đúng tab Open/Closed. */
export function stateFromDsl(q: string): 'open' | 'closed' | 'all' {
  if (/\bis:open\b/.test(q) || /\bstate:open\b/.test(q)) return 'open';
  if (/\bis:closed\b/.test(q) || /\bstate:closed\b/.test(q)) return 'closed';
  return 'all';
}

export const REACTION_EMOJI: Record<string, string> = {
  THUMBS_UP: '👍', THUMBS_DOWN: '👎', LAUGH: '😄', HOORAY: '🎉', CONFUSED: '😕', HEART: '❤️', ROCKET: '🚀', EYES: '👀',
};

export const EVENT_TEXT: Record<string, (p: Record<string, unknown>) => string> = {
  CLOSED: (p) => tr('đã đóng ticket này{v0}', { v0: p.state_reason === 'NOT_PLANNED' ? ' (not planned)' : p.state_reason === 'DUPLICATE' ? tr(' (trùng lặp)') : p.commit_sha ? ` qua commit ${String(p.commit_sha).slice(0, 7)}` : ' (completed)' }),
  REOPENED: () => tr('đã mở lại ticket này'),
  RENAMED: (p) => tr('đã đổi tiêu đề từ "{v0}" thành "{v1}"', { v0: p.from, v1: p.to }),
  LABELED: (p) => tr('đã gắn nhãn {v0}', { v0: p.label_name }),
  UNLABELED: (p) => tr('đã gỡ nhãn {v0}', { v0: p.label_name }),
  MILESTONED: (p) => tr('đã thêm vào milestone {v0}', { v0: p.title }),
  DEMILESTONED: (p) => tr('đã gỡ khỏi milestone {v0}', { v0: p.title }),
  ASSIGNED: (p) => tr('đã giao cho @{v0}', { v0: p.login }),
  UNASSIGNED: (p) => tr('đã bỏ giao @{v0}', { v0: p.login }),
  TYPED: (p) => tr('đã đặt loại {v0}', { v0: p.name }),
  UNTYPED: (p) => tr('đã gỡ loại {v0}', { v0: p.name }),
  LOCKED: (p) => tr('đã khoá hội thoại{v0}', { v0: p.lock_reason ? ` (${String(p.lock_reason).toLowerCase().replace('_', ' ')})` : '' }),
  UNLOCKED: () => tr('đã mở khoá hội thoại'),
  PINNED: () => tr('đã ghim ticket này'),
  UNPINNED: () => tr('đã bỏ ghim ticket này'),
  TRANSFERRED: (p) => tr('đã chuyển ticket này từ {v0}#{v1}', { v0: p.from_project, v1: p.from_number }),
  SUB_ISSUE_ADDED: (p) => tr('đã thêm sub-issue #{v0} {v1}', { v0: p.number, v1: p.title }),
  SUB_ISSUE_REMOVED: (p) => tr('đã gỡ sub-issue #{v0}', { v0: p.number }),
  PARENT_ISSUE_ADDED: (p) => tr('đã đặt ticket cha #{v0} {v1}', { v0: p.number, v1: p.title }),
  PARENT_ISSUE_REMOVED: (p) => tr('đã gỡ ticket cha #{v0}', { v0: p.number }),
  BLOCKED_BY_ADDED: (p) => tr('đã đánh dấu bị chặn bởi #{v0}', { v0: p.number }),
  BLOCKED_BY_REMOVED: (p) => tr('đã bỏ chặn bởi #{v0}', { v0: p.number }),
  BLOCKING_ADDED: (p) => tr('đang chặn #{v0}', { v0: p.number }),
  BLOCKING_REMOVED: (p) => tr('không còn chặn #{v0}', { v0: p.number }),
  MARKED_AS_DUPLICATE: (p) => tr('đã đánh dấu trùng với #{v0}', { v0: p.duplicate_of_number }),
  UNMARKED_AS_DUPLICATE: () => tr('đã bỏ đánh dấu trùng lặp'),
  CROSS_REFERENCED: (p) => tr('đã nhắc tới ticket này ở {v0}#{v1} {v2}', { v0: p.source_project ?? '', v1: p.source_number ?? '', v2: p.source_title ?? '' }),
  MENTIONED: () => tr('đã nhắc tới bạn'),
  CONNECTED: (p) => tr('đã liên kết PR {v0}', { v0: p.pr_url }),
  DISCONNECTED: () => tr('đã bỏ liên kết PR'),
  REFERENCED: (p) => tr('đã tham chiếu ticket này trong commit {v0}', { v0: String(p.commit_sha ?? '').slice(0, 7) }),
  ADDED_TO_BOARD: (p) => tr('đã thêm vào board {v0}', { v0: p.board_name }),
  REMOVED_FROM_BOARD: (p) => tr('đã gỡ khỏi board {v0}', { v0: p.board_name }),
  BOARD_COLUMN_CHANGED: (p) => tr('đã chuyển từ {v0} sang {v1} trên board {v2}', { v0: p.from_column ?? '—', v1: p.to_column ?? '—', v2: p.board_name }),
  PRIORITY_CHANGED: (p) => tr('đã đổi ưu tiên {v0} → {v1}', { v0: p.from ?? '—', v1: p.to ?? '—' }),
  SLA_WARNING: () => tr('SLA sắp quá hạn'),
  SLA_BREACHED: () => tr('SLA đã quá hạn'),
  ESCALATED: () => tr('đã escalate lên Lead'),
  DELETED: () => tr('đã xóa ticket'),
};

/**
 * Tách chuỗi Query DSL thành từng token, giữ nguyên phần trong ngoặc kép:
 *   `is:open label:"cần gấp"` → ['is:open', 'label:"cần gấp"']
 * Dùng để vẽ dải chip qualifier bấm-để-bỏ trên màn hình tìm kiếm.
 */
export function dslTokens(query: string): string[] {
  return query.match(/(?:[^\s"]+|"[^"]*")+/g) ?? [];
}

/** Bỏ một token khỏi truy vấn (so khớp nguyên văn, chỉ bỏ lần xuất hiện đầu). */
export function removeDslToken(query: string, token: string): string {
  const rest = dslTokens(query);
  const at = rest.indexOf(token);
  if (at >= 0) rest.splice(at, 1);
  return rest.join(' ');
}

/**
 * Đặt lại một qualifier: gỡ hết token đang dùng `key` rồi thêm `key:value` ở cuối.
 * `value = null` nghĩa là chỉ gỡ. `keys` cho phép một bộ lọc quản nhiều tên
 * (ví dụ trạng thái nằm ở cả `is:` lẫn `state:`).
 */
export function setDslQualifier(
  query: string,
  keys: string[],
  value: string | null,
  matches?: (token: string) => boolean,
): string {
  const kept = dslTokens(query).filter((t) => {
    const bare = t.startsWith('-') ? t.slice(1) : t;
    const key = bare.slice(0, bare.indexOf(':'));
    if (!keys.includes(key)) return true;
    return matches ? !matches(bare) : false;
  });
  if (value) kept.push(value);
  return kept.join(' ');
}

/**
 * Chữ viết tắt cho avatar: chữ cái đầu của từ đầu và của từ cuối.
 *
 * Trước đây lấy thẳng hai ký tự đầu của cả chuỗi, nên với tên tiếng Việt nó chỉ ra chữ cái đầu
 * của **một** từ: "Nguyễn Bảo Long" → "Ng", "Lê Sơn Trường" → "Lê". Ngoài chuyện đọc không ra
 * hai chữ cái đầu, nó còn đụng nhau — "Nguyễn Bảo Long" và "Nguyễn Huy Kiên" cùng ra "Ng".
 *
 * Tách theo cả khoảng trắng lẫn dấu nối để login dạng `son-truong` hay `son.truong` cũng ra "ST".
 * Dùng Array.from để đếm theo ký tự Unicode chứ không theo đơn vị UTF-16.
 * Việc viết hoa do CSS (`text-transform: uppercase`) lo.
 */
export function initials(name: string): string {
  const words = name.trim().split(/[\s._-]+/).filter(Boolean);
  if (words.length === 0) return '?';
  if (words.length === 1) return Array.from(words[0]).slice(0, 2).join('');
  return Array.from(words[0])[0] + Array.from(words[words.length - 1])[0];
}

/**
 * Các qualifier mà backend hiểu (`SearchQueryCompiler`). Danh sách đóng, không đoán theo dấu
 * hai chấm — nếu không thì `http://x` hay `9:30` cũng bị coi là bộ lọc.
 */
export const FILTER_KEYS = [
  'assignee', 'author', 'closed', 'commenter', 'comments', 'created', 'has', 'in',
  'interactions', 'involves', 'is', 'label', 'linked', 'mentions', 'milestone', 'no',
  'priority', 'project', 'reactions', 'reason', 'repo', 'sla', 'sort', 'state', 'type',
  'updated',
] as const;

/** Một token có phải cú pháp lọc không: `key:value`, cho phép phủ định `-key:value`. */
export function isFilterToken(token: string): boolean {
  const bare = token.startsWith('-') ? token.slice(1) : token;
  const colon = bare.indexOf(':');
  if (colon <= 0 || colon === bare.length - 1) return false;
  return (FILTER_KEYS as readonly string[]).includes(bare.slice(0, colon).toLowerCase());
}

/**
 * Tách truy vấn thành phần lọc và phần chữ tự do.
 *
 * Toán tử của DSL (`AND`, `OR`, ngoặc) để nguyên trong phần chữ: bọc chúng thành chip sẽ làm
 * mất cấu trúc biểu thức, mà người dùng gõ được biểu thức thì cũng tự sửa được nó dưới dạng chữ.
 */
export function splitQuery(query: string): { filters: string[]; text: string } {
  const filters: string[] = [];
  const words: string[] = [];
  for (const token of dslTokens(query)) {
    if (isFilterToken(token)) filters.push(token);
    else words.push(token);
  }
  return { filters, text: words.join(' ') };
}

/**
 * Ghép ngược lại. Chữ tự do đứng trước rồi tới bộ lọc — backend nối tất cả bằng AND nên thứ tự
 * không đổi kết quả, đặt vậy chỉ để đọc cho thuận.
 */
export function buildQuery(filters: string[], text: string): string {
  return [text.trim(), ...filters].filter(Boolean).join(' ').trim();
}

/** Các khoảng thời gian cho bộ lọc "tạo trong". `null` nghĩa là không giới hạn. */
export const DATE_RANGES = [
  { value: 'all', label: 'Mọi lúc', days: null },
  { value: '1d', label: '1 ngày qua', days: 1 },
  { value: '1w', label: '1 tuần qua', days: 7 },
  { value: '1m', label: '1 tháng qua', days: 30 },
  { value: '6m', label: '6 tháng qua', days: 182 },
] as const;

export type DateRange = (typeof DATE_RANGES)[number]['value'];

/**
 * Mốc bắt đầu của một khoảng, dạng ISO, hoặc `undefined` khi không giới hạn.
 *
 * Tính lùi từ thời điểm hiện tại nên không đụng tới việc phân tích chuỗi ngày. Bản cũ dùng
 * `new Date(giá_trị_của_input_date).toISOString()`, mà chuỗi chỉ có ngày thì JavaScript hiểu là
 * **UTC**: ở giờ Việt Nam (UTC+7) mốc rơi vào 07:00 sáng, nên bộ lọc bỏ sót mọi bản ghi tạo từ
 * 00:00 đến 07:00 của chính ngày người dùng chọn.
 */
export function rangeStartIso(range: DateRange, now: Date = new Date()): string | undefined {
  const days = DATE_RANGES.find((r) => r.value === range)?.days;
  if (!days) return undefined;
  return new Date(now.getTime() - days * 24 * 60 * 60 * 1000).toISOString();
}
