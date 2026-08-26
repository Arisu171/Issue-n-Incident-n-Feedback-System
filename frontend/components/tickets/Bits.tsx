'use client';

import Link from 'next/link';
import type { Label, Ticket, TicketRef, UserSummary } from '@/lib/tickets';
import { initials, labelTextColor, timeAgo } from '@/lib/util';
import { tr } from '@/lib/i18n';

/**
 * Icon nét theo bản vẽ Modernist: `fill=none`, `stroke=currentColor`, nét 2.
 * Không đặt `fill` trong globals.css — thuộc tính CSS sẽ thắng thuộc tính trình
 * bày trên thẻ svg và làm icon bị tô đặc.
 */
function Icon({ children, size = 16 }: { children: React.ReactNode; size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {children}
    </svg>
  );
}

export const Icons = {
  /* Đang mở — vòng tròn có chấm giữa (bản vẽ dùng màu accent cho trạng thái này). */
  issueOpened: <Icon><circle cx="12" cy="12" r="10" /><circle cx="12" cy="12" r="1" fill="currentColor" /></Icon>,
  /* Đã đóng (completed) — vòng tròn hở kèm dấu tích. */
  issueClosed: <Icon><path d="M21.8 10A10 10 0 1 1 17 3.34" /><path d="m9 11 3 3L22 4" /></Icon>,
  /* Đóng nhưng không xử lý / trùng lặp — vòng tròn gạch chéo. */
  skip: <Icon><circle cx="12" cy="12" r="10" /><path d="m4.9 4.9 14.2 14.2" /></Icon>,
  comment: <Icon><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" /></Icon>,
  tag: <Icon><path d="M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z" /><circle cx="7.5" cy="7.5" r=".5" fill="currentColor" /></Icon>,
  milestone: <Icon><path d="M12 13v8" /><path d="M12 3v3" /><path d="M4 6h13.5a1 1 0 0 1 .8 1.6L16 10l2.3 2.4a1 1 0 0 1-.8 1.6H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1z" /></Icon>,
  project: <Icon><rect width="18" height="18" x="3" y="3" rx="2" /><path d="M8 7v7" /><path d="M12 7v4" /><path d="M16 7v9" /></Icon>,
  bell: <Icon><path d="M10.268 21a2 2 0 0 0 3.464 0" /><path d="M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326" /></Icon>,
  gear: <Icon><circle cx="12" cy="12" r="3" /><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z" /></Icon>,
  lock: <Icon><rect width="18" height="11" x="3" y="11" rx="2" ry="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" /></Icon>,
  pin: <Icon><path d="M12 17v5" /><path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z" /></Icon>,
  search: <Icon><circle cx="11" cy="11" r="8" /><path d="m21 21-4.3-4.3" /></Icon>,
  inbox: <Icon><path d="M22 12h-6l-2 3h-4l-2-3H2" /><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z" /></Icon>,
  chevron: <Icon size={12}><path d="m6 9 6 6 6-6" /></Icon>,
  /* Xác nhận một thao tác đã điền xong — dùng cho nút Chuyển project. */
  check: <Icon><path d="M20 6 9 17l-5-5" /></Icon>,
  /* Reaction — mặt cười nét, cùng bộ với các icon còn lại. */
  smile: <Icon><circle cx="12" cy="12" r="10" /><path d="M8 14s1.5 2 4 2 4-2 4-2" /><path d="M9 9h.01" /><path d="M15 9h.01" /></Icon>,
};

/**
 * Ảnh đại diện. Có `user` thì bọc trong link tới hồ sơ — yêu cầu là bấm vào avatar hay tên của
 * bất kỳ ai cũng mở được hồ sơ người đó.
 *
 * `link={false}` cho những chỗ avatar đã nằm sẵn trong một link hoặc một nút khác: link lồng
 * link là HTML sai và trình duyệt xử lý mỗi nơi một kiểu.
 */
export function Avatar({ user, size = '', link = true }: {
  user: UserSummary | null | undefined;
  size?: '' | 'sm' | 'lg';
  link?: boolean;
}) {
  const text = user ? initials(user.displayName || user.login) : 'HT';
  const chip = (
    <span className={`gh-avatar ${size}`} title={user ? `${user.displayName} (@${user.login})` : tr('hệ thống')}>
      {text}
    </span>
  );
  return user && link ? <Link href={`/profiles/${user.login}`} className="avatar-link">{chip}</Link> : chip;
}

/**
 * Chỗ trống trong danh sách người được giao.
 *
 * Trước đây là một dòng chữ, nên ở bảng và ở thanh bên nó trông khác hẳn phần có người — mắt phải
 * đọc mới biết. Vẽ thành một ô cùng cỡ, cùng chỗ với avatar thì nhìn lướt là thấy.
 *
 * Hình vuông 34px không chứa nổi chữ `UNASSIGNED`, nên trong ô là `UN` còn chữ đầy đủ nằm ở
 * `title` và `aria-label` — trình đọc màn hình đọc được đủ, và ở chỗ có bề ngang thì gọi kèm
 * `withText` để hiện luôn.
 */
export function UnassignedAvatar({ size = '', withText = false }: { size?: '' | 'sm' | 'lg'; withText?: boolean }) {
  const label = 'UNASSIGNED';
  const chip = <span className={`gh-avatar none ${size}`} title={label} aria-label={label} role="img">UN</span>;
  if (!withText) return chip;
  return (
    <span className="unassigned-row">
      {chip}
      <span className="muted">{label}</span>
    </span>
  );
}

/**
 * Ticket chưa gắn nhãn nào.
 *
 * **Cố ý không phải một Label thật trong cơ sở dữ liệu.** Nếu tạo hàng thật thì: có người tạo
 * được nhãn trùng tên trước, mỗi lần thêm/bớt nhãn lại sinh một cặp sự kiện gắn/gỡ mà không ai
 * thực hiện, webhook bắn theo, và hai người cùng sửa nhãn một ticket sẽ đua nhau thêm-xoá nó.
 * Đổi lại không được gì: người dùng vẫn chỉ thấy đúng một chữ trên màn hình. Lọc thì đã có sẵn
 * `no:label` trong ô tìm kiếm.
 */
export function NoLabelChip() {
  return <span className="gh-label none" title={tr('Ticket này chưa gắn nhãn nào')}>non-label</span>;
}

/**
 * Chấm trạng thái hiện diện, quy ước như Discord: xanh đang hoạt động, vàng bận, xám ngoại tuyến.
 *
 * `invisible` chỉ tới được đây khi **chính chủ** xem hồ sơ mình — với người khác server đã trả
 * `offline`, nên màn hình không có gì để giấu và cũng không thể lỡ tay để lộ.
 */
export function PresenceDot({ status, withText = false }: { status: string; withText?: boolean }) {
  const label: Record<string, string> = {
    online: tr('đang hoạt động'),
    snooze: tr('bận'),
    invisible: tr('đang ẩn'),
    offline: tr('ngoại tuyến'),
  };
  const text = label[status] ?? label.offline;
  const dot = <span className={`presence-dot ${status}`} title={text} aria-label={text} role="img" />;
  return withText ? <span className="unassigned-row">{dot}<span className="muted">{text}</span></span> : dot;
}

export function StateIcon({ state, reason, className = '' }: { state: 'OPEN' | 'CLOSED'; reason?: string | null; className?: string }) {
  const kind = state === 'OPEN' ? 'open' : reason === 'NOT_PLANNED' || reason === 'DUPLICATE' ? 'not-planned' : 'closed';
  return <span className={`state-icon ${kind} ${className}`}>{kind === 'open' ? Icons.issueOpened : kind === 'closed' ? Icons.issueClosed : Icons.skip}</span>;
}

export function StateBadge({ ticket }: { ticket: Pick<Ticket, 'state' | 'stateReason'> }) {
  const open = ticket.state === 'OPEN';
  const notPlanned = !open && (ticket.stateReason === 'NOT_PLANNED' || ticket.stateReason === 'DUPLICATE');
  return (
    <span className={`state-badge ${open ? 'open' : notPlanned ? 'not-planned' : 'closed'}`}>
      {open ? Icons.issueOpened : notPlanned ? Icons.skip : Icons.issueClosed}
      {open ? 'Open' : notPlanned ? (ticket.stateReason === 'DUPLICATE' ? 'Closed as duplicate' : 'Closed as not planned') : 'Closed'}
    </span>
  );
}

export function LabelChip({ label, onClick, href }: { label: Pick<Label, 'name' | 'colorHex' | 'description'>; onClick?: () => void; href?: string }) {
  const style = { backgroundColor: `#${label.colorHex}`, color: labelTextColor(label.colorHex), borderColor: `#${label.colorHex}` };
  const inner = <span className={`gh-label ${onClick || href ? 'interactive' : ''}`} style={style} title={label.description ?? undefined} onClick={onClick}>{label.name}</span>;
  return href ? <Link href={href}>{inner}</Link> : inner;
}

/**
 * Loại ticket. Bản mẫu vẽ nó là `tag tag-outline` — chỉ viền accent, không chấm màu.
 * `color` do quản trị đặt cho từng loại nên vẫn nhận vào, dùng làm tooltip thay vì tô chấm.
 */
export function TypeChip({ name, color }: { name: string; color: string }) {
  return <span className="type-chip" title={tr('Loại {v0}', { v0: name })} data-color={color}>{name}</span>;
}

/** Ưu tiên: P0 nổi bật bằng accent, còn lại trung tính (đúng thứ bậc của bản vẽ). */
export function PriorityTag({ priority, note }: { priority: string | null; note?: string | null }) {
  if (!priority) return null;
  return (
    <span className={`tag ${priority === 'P0' ? 'tag-accent' : 'tag-neutral'}`} style={{ fontFamily: 'var(--font-heading)', fontWeight: 800 }}>
      {priority}{note ? ` · ${note}` : ''}
    </span>
  );
}

export function RefLink({ t }: { t: TicketRef }) {
  return (
    <Link href={`/projects/${t.projectSlug}/issues/${t.number}`} className="rel-item">
      <StateIcon state={t.state} reason={t.stateReason} />
      <span className="grow">{t.title}</span>
      <span className="muted">#{t.number}</span>
    </Link>
  );
}

/**
 * Một ticket trong danh sách, dạng thẻ hai dòng.
 *
 * Dòng 1: tiêu đề bên trái, nhãn bên phải.
 * Dòng 2: trạng thái và thông tin dòng thời gian bên trái; ưu tiên, người phụ trách,
 *         số bình luận và số reaction bên phải.
 */
export function IssueRow({ t }: { t: Ticket }) {
  const overdue = t.slaDueAt && !t.firstResponseAt && t.state === 'OPEN' && new Date(t.slaDueAt) < new Date();
  const sub = t.subIssuesSummary;
  const reactions = t.reactions?.total ?? 0;
  const extraAssignees = t.assignees.length - 3;

  return (
    <article className={`issue-row${t.state === 'CLOSED' ? ' closed' : ''}`}>
      <div className="line">
        <div className="left">
          <Link href={`/projects/${t.projectSlug}/issues/${t.number}`} className="title">{t.title}</Link>
        </div>
        <div className="right">
          <div className="labels">
            {t.labels.length === 0
              ? <NoLabelChip />
              : t.labels.map((l) => (
                <LabelChip key={l.id} label={l} href={`/projects/${t.projectSlug}/issues?q=${encodeURIComponent(`is:open label:"${l.name}"`)}`} />
              ))}
          </div>
        </div>
      </div>

      <div className="line">
        <div className="left">
          <StateIcon state={t.state} reason={t.stateReason} />
          <span className="meta">
            #{t.number} {t.state === 'OPEN' ? tr('mở') : tr('đóng')} {timeAgo(t.state === 'OPEN' ? t.createdAt : t.closedAt ?? t.updatedAt)} bởi <strong>{t.author.login}</strong>
            {t.milestone && <> · Milestone {t.milestone.title}</>}
            {t.type && <> · {t.type.name}</>}
            {sub.total > 0 && <> · {sub.total} sub-issue ({sub.completed}/{sub.total})</>}
            {t.pinned && <> · ghim</>}
          </span>
        </div>
        <div className="right">
          <PriorityTag priority={t.priority} note={overdue ? tr('SLA quá hạn') : null} />
          {t.assignees.length === 0
            ? <UnassignedAvatar size="sm" />
            : (
              <span className="who">
                {t.assignees.slice(0, 3).map((a) => <Avatar key={a.id} user={a} size="sm" />)}
                {extraAssignees > 0 && <span className="stat">+{extraAssignees}</span>}
              </span>
            )}
          <span className="stat" title={tr('{v0} bình luận', { v0: t.comments })}>{Icons.comment} {t.comments}</span>
          <span className="stat" title={`${reactions} reaction`}>{Icons.smile} {reactions}</span>
        </div>
      </div>
    </article>
  );
}
