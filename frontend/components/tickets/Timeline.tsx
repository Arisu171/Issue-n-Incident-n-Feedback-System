'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Markdown } from '@/components/Markdown';
import { MarkdownEditor } from '@/components/tickets/MarkdownEditor';
import { Avatar, Icons } from '@/components/tickets/Bits';
import { session } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ActionFeedback } from '@/components/ui';
import { useDismiss } from '@/lib/useDismiss';
import { tickets, type HideReason, type ReactionType, type Ticket, type TimelineEvent } from '@/lib/tickets';
import { EVENT_TEXT, REACTION_EMOJI, formatDate, timeAgo } from '@/lib/util';
import { tr } from '@/lib/i18n';

const REACTIONS: ReactionType[] = ['THUMBS_UP', 'THUMBS_DOWN', 'LAUGH', 'HOORAY', 'CONFUSED', 'HEART', 'ROCKET', 'EYES'];
const HIDE_REASONS: HideReason[] = ['ABUSE', 'OFF_TOPIC', 'OUTDATED', 'RESOLVED', 'SPAM', 'DUPLICATE'];

/**
 * Hàng reaction, **cập nhật lạc quan**.
 *
 * Trước đây mỗi lần thả emoji là một vòng: gửi lên server, chờ, rồi mới vẽ lại — có nơi còn tải
 * lại cả ticket. Người dùng bấm xong thấy nút đứng im vài trăm mili giây rồi cả khối nháy một
 * cái. Với một thao tác nhỏ và gần như không bao giờ hỏng như thả emoji, đó là cái giá sai.
 *
 * Nên: đổi ngay trên màn hình (số ±1, nền sáng lên), gửi đi ở nền, và **hòa lại theo câu trả lời
 * của server** khi nó về — nếu người khác vừa thả cùng lúc thì con số cuối vẫn là con số thật.
 * Gửi hỏng thì trả về đúng trạng thái trước khi bấm.
 */
export function ReactionBar({ counts, mine, onToggle, disabled }: {
  counts: Record<string, number>;
  mine: ReactionType[];
  /** Trả về tổng kết mới từ server để hoà lại; trả `void` nếu nơi gọi tự lo phần đó. */
  onToggle: (r: ReactionType) => void | Promise<{ counts: Record<string, number>; viewerReactions: ReactionType[] } | void>;
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  // Bản nháp cục bộ: chỉ tồn tại từ lúc bấm tới lúc server trả lời.
  const [draft, setDraft] = useState<{ counts: Record<string, number>; mine: ReactionType[] } | null>(null);

  // Dữ liệu mới từ trên xuống (server đã trả lời, hoặc real-time đẩy về) thì bỏ bản nháp đi.
  useEffect(() => { setDraft(null); }, [counts, mine]);

  const shownCounts = draft?.counts ?? counts;
  const shownMine = draft?.mine ?? mine;
  const entries = Object.entries(shownCounts).filter(([k, v]) => k !== 'total' && v > 0);

  async function toggle(r: ReactionType) {
    const had = shownMine.includes(r);
    const next = { ...shownCounts, [r]: Math.max(0, (shownCounts[r] ?? 0) + (had ? -1 : 1)) };
    const before = { counts: shownCounts, mine: shownMine };
    setDraft({ counts: next, mine: had ? shownMine.filter((x) => x !== r) : [...shownMine, r] });

    try {
      const summary = await onToggle(r);
      if (summary) setDraft({ counts: summary.counts, mine: summary.viewerReactions });
    } catch {
      // Nơi gọi đã lo việc báo lỗi; ở đây chỉ trả nút về đúng chỗ cũ thay vì để một con số sai.
      setDraft(before);
    }
  }

  return (
    <div className="reaction-bar" style={{ position: 'relative' }}>
      {entries.map(([k, v]) => (
        <button key={k} type="button" className={`reaction ${shownMine.includes(k as ReactionType) ? 'mine' : ''}`} disabled={disabled} onClick={() => void toggle(k as ReactionType)}>
          {REACTION_EMOJI[k] ?? k} {v}
        </button>
      ))}
      <button type="button" className="reaction" title={tr('Thêm reaction')} disabled={disabled} onClick={() => setOpen((v) => !v)}>+</button>
      {open && (
        <div className="reaction-picker" onMouseLeave={() => setOpen(false)}>
          {REACTIONS.map((r) => <button key={r} type="button" title={r} onClick={() => { void toggle(r); setOpen(false); }}>{REACTION_EMOJI[r]}</button>)}
        </div>
      )}
    </div>
  );
}

function iconFor(type: string) {
  switch (type) {
    case 'CLOSED': return <span className="icon">{Icons.issueClosed}</span>;
    case 'REOPENED': return <span className="icon">{Icons.issueOpened}</span>;
    case 'LABELED': case 'UNLABELED': return <span className="icon">{Icons.tag}</span>;
    case 'MILESTONED': case 'DEMILESTONED': return <span className="icon">{Icons.milestone}</span>;
    case 'LOCKED': case 'UNLOCKED': return <span className="icon">{Icons.lock}</span>;
    case 'PINNED': case 'UNPINNED': return <span className="icon">{Icons.pin}</span>;
    case 'SLA_BREACHED': case 'ESCALATED': return <span className="icon">{Icons.bell}</span>;
    case 'ADDED_TO_BOARD': case 'BOARD_COLUMN_CHANGED': case 'REMOVED_FROM_BOARD': return <span className="icon">{Icons.project}</span>;
    default: return <span className="icon">{Icons.issueOpened}</span>;
  }
}

/** Sự kiện hệ thống đáng chú ý — bản vẽ đánh dấu bằng chấm viền accent (tlb4). */
const ACCENT_EVENTS = new Set(['SLA_WARNING', 'SLA_BREACHED', 'ESCALATED', 'CLOSED', 'REOPENED']);

export function EventRow({ e }: { e: TimelineEvent }) {
  const text = EVENT_TEXT[e.eventType]?.(e.payload) ?? e.eventType.toLowerCase().replace(/_/g, ' ');
  const p = e.payload as Record<string, string | number | undefined>;
  const link = e.eventType === 'CROSS_REFERENCED' && p.source_project && p.source_number
    ? `/projects/${p.source_project}/issues/${p.source_number}`
    : ['SUB_ISSUE_ADDED', 'PARENT_ISSUE_ADDED', 'BLOCKED_BY_ADDED', 'BLOCKING_ADDED', 'MARKED_AS_DUPLICATE'].includes(e.eventType) && p.project && p.number
      ? `/projects/${p.project}/issues/${p.number}` : null;
  return (
    <div className={`tl-event ${ACCENT_EVENTS.has(e.eventType) ? 'tlb4' : 'tlb1'}`}>
      {iconFor(e.eventType)}
      <div>
        <strong>{e.actor ? e.actor.login : tr('hệ thống')}</strong> {link ? <Link href={link}>{text}</Link> : text}
        {e.eventType === 'LABELED' || e.eventType === 'UNLABELED' ? null : null}
        {' '}<span className="when" title={formatDate(e.createdAt)}>{timeAgo(e.createdAt)}</span>
      </div>
    </div>
  );
}

export function CommentCard({
  e, ticket, slug, canWrite, onChanged, onDeleted,
}: { e: TimelineEvent; ticket: Ticket; slug: string; canWrite: boolean; onChanged: (updated: Partial<TimelineEvent>) => void; onDeleted: () => void }) {
  const me = session.user();
  const mine = e.actor?.id === me?.id;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(e.body ?? '');
  const [menu, setMenu] = useState(false);
  const menuRef = useRef<HTMLSpanElement>(null);
  useDismiss(menuRef, useCallback(() => setMenu(false), []), menu);
  const act = useAction<unknown>();
  const busy = act.isProcessing;
  const [expanded, setExpanded] = useState(false);
  const isInternal = e.eventType === 'INTERNAL_NOTE';

  /** Mọi thao tác trên bình luận đi qua cùng một useAction → luôn có đủ 3 trạng thái. */
  async function run(fn: () => Promise<unknown>, message?: string) {
    setMenu(false);
    await act.run(fn, message);
  }

  async function save() {
    await run(async () => {
      const c = await tickets.editComment(slug, ticket.number, e.id, draft);
      onChanged({ body: c.body, bodyHtml: c.bodyHtml, isEdited: true, editedAt: c.editedAt });
      setEditing(false);
    }, tr('Đã cập nhật bình luận.'));
  }

  /**
   * Không đi qua `run(...)`: nó bật trạng thái "đang xử lý" cho cả khối bình luận, khoá nút và
   * làm cả vùng nháy một cái cho một thao tác chỉ đổi một con số. Hàng reaction tự lo phần hiển
   * thị lạc quan; ở đây chỉ trả tổng kết thật về cho nó hoà lại.
   */
  async function toggleReaction(r: ReactionType) {
    const s = await tickets.toggleReaction(slug, ticket.number, r, e.id);
    onChanged({ reactions: s.counts, viewerReactions: s.viewerReactions });
    return s;
  }

  return (
    <div className={`tl-comment ${isInternal ? 'internal tlb3' : 'tlb2'} ${e.isHidden && !expanded ? 'hidden' : ''}`} id={`event-${e.id}`}>
      <div className="tl-comment-head">
        {isInternal
          ? <span className="assoc" style={{ fontFamily: 'var(--font-heading)', fontWeight: 800, color: 'inherit' }}>{tr('Ghi chú nội bộ — khách hàng không thấy')}</span>
          : <Avatar user={e.actor} />}
        <strong style={isInternal ? { marginLeft: 'auto' } : undefined}>
          {e.actor ? <Link href={`/profiles/${e.actor.login}`}>{e.actor.login}</Link> : tr('hệ thống')}
        </strong>
        <span title={formatDate(e.createdAt)}>{isInternal ? timeAgo(e.createdAt) : tr('bình luận {v0}', { v0: timeAgo(e.createdAt) })}</span>
        {e.isEdited && <span title={e.editedAt ? tr('sửa {v0}', { v0: formatDate(e.editedAt) }) : ''}>{tr('· đã sửa')}</span>}
        {e.isHidden && <span>· đã ẩn ({e.hiddenReason?.toLowerCase().replace('_', ' ')})</span>}
        <span className="grow" />
        {e.authorAssociation && e.authorAssociation !== 'NONE' && <span className="assoc">{e.authorAssociation.toLowerCase().replace(/_/g, ' ')}</span>}
        {e.isHidden && <button type="button" className="ghost small" onClick={() => setExpanded((v) => !v)}>{expanded ? tr('Thu gọn') : tr('Hiện')}</button>}
        {(mine || canWrite) && (
          <span className="gh-menu" ref={menuRef}>
            <button type="button" className="ghost small" onClick={() => setMenu((v) => !v)} aria-label={tr('Tùy chọn')}>···</button>
            {menu && (
              <div className="gh-dropdown">
                {(mine || canWrite) && <button type="button" className="item" onClick={() => { setEditing(true); setMenu(false); }}>{tr('Sửa')}</button>}
                {canWrite && !e.isHidden && (
                  <>
                    <div className="divider" /><div className="head">{tr('Ẩn với lý do')}</div>
                    {HIDE_REASONS.map((r) => <button key={r} type="button" className="item" onClick={() => run(async () => { await tickets.hideComment(slug, ticket.number, e.id, r); onChanged({ isHidden: true, hiddenReason: r }); }, tr('Đã ẩn bình luận.'))}>{r.toLowerCase().replace('_', ' ')}</button>)}
                  </>
                )}
                {canWrite && e.isHidden && <button type="button" className="item" onClick={() => run(async () => { await tickets.unhideComment(slug, ticket.number, e.id); onChanged({ isHidden: false, hiddenReason: null }); }, tr('Đã bỏ ẩn bình luận.'))}>{tr('Bỏ ẩn')}</button>}
                <div className="divider" />
                <button type="button" className="item" onClick={() => { if (confirm(tr('Xóa bình luận này?'))) void run(async () => { await tickets.deleteComment(slug, ticket.number, e.id); onDeleted(); }, tr('Đã xóa bình luận.')); }}>{tr('Xóa')}</button>
              </div>
            )}
          </span>
        )}
      </div>
      <div className="tl-comment-body">
        {editing ? (
          <>
            <MarkdownEditor value={draft} onChange={setDraft} project={slug} onSubmit={save} disabled={busy} />
            <div className="editor-actions">
              <button type="button" className="secondary" onClick={() => { setEditing(false); setDraft(e.body ?? ''); }}>{tr('Hủy')}</button>
              <button type="button" className="primary" disabled={busy || !draft.trim()} onClick={save}>{busy ? tr('Đang lưu…') : tr('Cập nhật')}</button>
            </div>
          </>
        ) : (
          <Markdown html={e.bodyHtml} />
        )}
        <ActionFeedback action={act} />
      </div>
      {!editing && !isInternal && (
        <div className="tl-comment-foot">
          <ReactionBar counts={e.reactions ?? {}} mine={e.viewerReactions ?? []} onToggle={toggleReaction} disabled={busy || ticket.locked} />
        </div>
      )}
    </div>
  );
}

/**
 * Dòng này có phải **trao đổi thật của con người** không.
 *
 * Phần còn lại là dấu vết hệ thống (gắn nhãn, đổi trạng thái…) — ẩn được. Định nghĩa nằm đúng
 * một chỗ để bộ lọc và phần vẽ không bao giờ hiểu khác nhau.
 */
export function isConversation(eventType: string): boolean {
  return eventType === 'COMMENTED' || eventType === 'INTERNAL_NOTE';
}

export function Timeline({ events, ticket, slug, canWrite, onEventChanged, onEventDeleted }: {
  events: TimelineEvent[]; ticket: Ticket; slug: string; canWrite: boolean;
  onEventChanged: (id: string, patch: Partial<TimelineEvent>) => void; onEventDeleted: (id: string) => void;
}) {
  return (
    <div className="tl">
      {events.map((e) => (
        isConversation(e.eventType)
          ? <CommentCard key={e.id} e={e} ticket={ticket} slug={slug} canWrite={canWrite} onChanged={(p) => onEventChanged(e.id, p)} onDeleted={() => onEventDeleted(e.id)} />
          : <EventRow key={e.id} e={e} />
      ))}
    </div>
  );
}
