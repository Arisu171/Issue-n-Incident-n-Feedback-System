'use client';

import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Markdown } from '@/components/Markdown';
import { ActionFeedback, ErrorBox, Guard, isNotFound, NotFoundView, PageHead, Toggle } from '@/components/ui';
import { Avatar, Icons, StateBadge, TypeChip } from '@/components/tickets/Bits';
import { MarkdownEditor } from '@/components/tickets/MarkdownEditor';
import { isConversation, ReactionBar, Timeline } from '@/components/tickets/Timeline';
import { Sidebar } from '@/components/tickets/Sidebar';
import { ApiError, session } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { useTicketRealtime } from '@/lib/realtime';
import { useDismiss } from '@/lib/useDismiss';
import { tickets, type ReactionType, type Ticket, type TimelineEvent } from '@/lib/tickets';
import { formatDate, timeAgo } from '@/lib/util';
import { tr } from '@/lib/i18n';

/** Trang chi tiết issue — bố cục GitHub: tiêu đề + state, body, timeline, ô comment, sidebar; real-time qua SignalR. */
function IssueDetail() {
  const { project, number } = useParams<{ project: string; number: string }>();
  const n = Number(number);
  const [ticket, setTicket] = useState<Ticket | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [events, setEvents] = useState<TimelineEvent[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [showActivity, setShowActivity] = useState(true);
  const [comment, setComment] = useState('');
  const [internal, setInternal] = useState(false);
  const act = useAction<boolean>();
  const busy = act.isProcessing;
  const [editingTitle, setEditingTitle] = useState(false);
  const [title, setTitle] = useState('');
  const [editingBody, setEditingBody] = useState(false);
  const [bodyDraft, setBodyDraft] = useState('');
  const [closeMenu, setCloseMenu] = useState(false);
  const [warnings, setWarnings] = useState<string[]>([]);
  const closeMenuRef = useRef<HTMLSpanElement>(null);
  useDismiss(closeMenuRef, useCallback(() => setCloseMenu(false), []), closeMenu);

  const me = session.user();
  const canComment = session.can('ticket.comment');
  const canTriage = session.can('ticket.triage');
  const canWrite = session.can('ticket.write');
  const canInternal = session.can('ticket.internal_note');

  const load = useCallback(async () => {
    try {
      const r = await tickets.get(project, n);
      setTicket(r.data); setEtag(r.etag); setTitle(r.data.title); setBodyDraft(r.data.body);
      const tl = await tickets.timeline(project, n);
      let items = tl.items; let cursor = tl.nextCursor;
      while (cursor) { const more = await tickets.timeline(project, n, cursor); items = items.concat(more.items); cursor = more.nextCursor; }
      setEvents(items);
      tickets.markTicketRead(r.data.id).catch(() => undefined);
    } catch (e) { setError(e); }
  }, [project, n]);

  useEffect(() => { void load(); }, [load]);

  useTicketRealtime(ticket?.id ?? null, {
    onEvent: (e) => setEvents((prev) => (prev.some((x) => x.id === e.id) ? prev : [...prev, e])),
    onChanged: async (c) => {
      if (ticket && c.version > ticket.version) {
        const r = await tickets.get(project, n).catch(() => null);
        if (r) { setTicket(r.data); setEtag(r.etag); }
        if (['COMMENT_EDITED', 'COMMENT_DELETED', 'COMMENT_HIDDEN', 'COMMENT_UNHIDDEN', 'REACTED', 'UNREACTED'].includes(c.eventType)) {
          const tl = await tickets.timeline(project, n).catch(() => null);
          if (tl) setEvents(tl.items);
        }
      }
    },
  });

  function applyTicket(t: Ticket, tag: string | null) { setTicket(t); if (tag) setEtag(tag); setTitle(t.title); setBodyDraft(t.body); setWarnings(t.warnings ?? []); }

  /** PATCH có If-Match; 412 → tải lại phiên bản mới nhất rồi báo người dùng thử lại (BR-CONC-01). */
  async function patch(body: Parameters<typeof tickets.update>[2], message = tr('Đã cập nhật ticket.')) {
    setError(null);
    const ok = await act.run(
      async () => {
        try { const r = await tickets.update(project, n, body, etag); applyTicket(r.data, r.etag); return true; }
        catch (e) { if (e instanceof ApiError && e.status === 412) await load(); throw e; }
      },
      message,
    );
    return ok === true;
  }

  async function submitComment(then?: () => Promise<boolean>, message = tr('Đã gửi bình luận.')) {
    if (!ticket) return;
    setError(null);
    await act.run(
      async () => {
        if (comment.trim()) {
          if (internal) await tickets.internalNote(project, n, comment); else await tickets.comment(project, n, comment);
          setComment('');
        }
        if (then) await then();
        const tl = await tickets.timeline(project, n); setEvents(tl.items);
        const r = await tickets.get(project, n); applyTicket(r.data, r.etag);
        return true;
      },
      message,
    );
  }

  // Ticket không tồn tại là chuyện thường: `#12` trong một bình luận cũ trỏ tới ticket đã xoá,
  // hoặc ai đó gõ nhầm số. Nó không phải sự cố hệ thống nên không bày ra như một lỗi đỏ.
  if (isNotFound(error) && !ticket) {
    return (
      <NotFoundView
        title={tr('Không có ticket #{v0}', { v0: n })}
        hint={tr('Ticket này không tồn tại trong project “{v0}”, hoặc đã bị xoá.', { v0: project })}
        backHref={`/projects/${project}/issues`}
        backLabel={tr('Về danh sách issue')}
      />
    );
  }
  if (error && !ticket) return <ErrorBox error={error} />;
  if (!ticket) return <div className="empty">{tr('Đang tải…')}</div>;

  const canEditContent = canWrite || ticket.author.id === me?.id;
  const canChangeState = canTriage || ticket.author.id === me?.id;
  const commentAllowed = canComment && (!ticket.locked || canWrite);
  const openDup = ticket.duplicateOf;

  return (
    <>
      <div className="issue-head">
        {editingTitle ? (
          <div className="row">
            <input className="input" style={{ flex: 1, minWidth: 260 }} value={title} onChange={(e) => setTitle(e.target.value)} autoFocus />
            <div style={{ display: 'flex', gap: 'var(--space-2)' }}>
              <button type="button" className="btn btn-primary" disabled={busy} onClick={async () => { if (await patch({ title })) setEditingTitle(false); }}>{tr('Lưu')}</button>
              <button type="button" className="btn btn-secondary" onClick={() => { setEditingTitle(false); setTitle(ticket.title); }}>{tr('Hủy')}</button>
            </div>
          </div>
        ) : (
          /* Cùng `PageHead` với mọi tab lớn: một khuôn tiêu đề cho cả ứng dụng. */
          <PageHead
            kicker={project}
            title={<>{ticket.title} <span className="num">#{ticket.number}</span></>}
            actions={<>
              {canEditContent && <button type="button" className="btn btn-secondary" onClick={() => setEditingTitle(true)}>{tr('Sửa tiêu đề')}</button>}
              {session.can('ticket.create') && <Link href={`/projects/${project}/issues/new`} className="btn btn-primary">New issue</Link>}
            </>}
          />
        )}
        <div className="meta">
          <StateBadge ticket={ticket} />
          {ticket.type && <TypeChip name={ticket.type.name} color={ticket.type.color} />}
          {ticket.pinned && <span className="tag tag-outline">Pinned</span>}
          {ticket.locked && <span className="tag tag-outline">Locked{ticket.activeLockReason ? ` · ${ticket.activeLockReason.toLowerCase().replace('_', ' ')}` : ''}</span>}
          <span><strong>{ticket.author.login}</strong> đã mở {timeAgo(ticket.createdAt)} · {ticket.comments} bình luận</span>
          {openDup && <span>{tr('· trùng với')} <Link href={`/projects/${openDup.projectSlug}/issues/${openDup.number}`}>#{openDup.number}</Link></span>}
          {ticket.parent && <span>{tr('· sub-issue của')} <Link href={`/projects/${ticket.parent.projectSlug}/issues/${ticket.parent.number}`}>#{ticket.parent.number}</Link></span>}
          {/* Ẩn dấu vết hệ thống (gắn nhãn, đổi trạng thái…). Bình luận không bao giờ bị ẩn:
              đó là thứ người dùng tự đưa vào, giấu nó đi là giấu mất nội dung. */}
          <label className="check" style={{ marginLeft: 'auto' }}>
            {tr('Hoạt động')}
            <Toggle
              checked={showActivity}
              ariaLabel={tr('Hiện hoạt động hệ thống')}
              onChange={setShowActivity}
            />
          </label>
        </div>
      </div>

      {warnings.length > 0 && (
        <div className="flash warn warnings">
          {warnings.includes('OPEN_SUB_ISSUES') && <div>{tr('⚠ Ticket đã đóng nhưng còn sub-issue đang mở (GitHub cho phép; project không bật strict policy).')}</div>}
          {warnings.includes('BLOCKED_BY_OPEN') && <div>{tr('⚠ Ticket đã đóng dù vẫn đang bị ticket khác chặn.')}</div>}
        </div>
      )}
      {error ? <ErrorBox error={error} /> : null}
      <ActionFeedback action={act} />

      <div className="issue-layout">
        <div className="main-col">
          <div className="tl-comment">
            <div className="tl-comment-head">
              <Avatar user={ticket.author} />
              <Link href={`/profiles/${ticket.author.login}`}><strong>{ticket.author.login}</strong></Link>
              <span title={formatDate(ticket.createdAt)}>đã mở {timeAgo(ticket.createdAt)}</span>
              {ticket.updatedAt !== ticket.createdAt && ticket.body !== '' ? null : null}
              <span className="grow" />
              <span className="assoc">{ticket.authorAssociation.toLowerCase().replace(/_/g, ' ')}</span>
              {canEditContent && !editingBody && <button type="button" className="ghost small" onClick={() => setEditingBody(true)}>{tr('Sửa')}</button>}
            </div>
            <div className="tl-comment-body">
              {editingBody ? (
                <>
                  <MarkdownEditor value={bodyDraft} onChange={setBodyDraft} project={project} disabled={busy} onSubmit={async () => { if (await patch({ body: bodyDraft })) setEditingBody(false); }} />
                  <div className="editor-actions">
                    <button type="button" className="secondary" onClick={() => { setEditingBody(false); setBodyDraft(ticket.body); }}>{tr('Hủy')}</button>
                    <button type="button" className="primary" disabled={busy} onClick={async () => { if (await patch({ body: bodyDraft })) setEditingBody(false); }}>{tr('Cập nhật')}</button>
                  </div>
                </>
              ) : <Markdown html={ticket.bodyHtml} />}
            </div>
            <div className="tl-comment-foot">
              {/* Thả emoji **không** tải lại cả ticket nữa: một thao tác đổi một con số mà kéo
                  theo cả trang vẽ lại là nguồn của cú nháy. Dùng thẳng tổng kết mà endpoint trả
                  về, và trả nó cho hàng reaction để hoà lại phần hiển thị lạc quan. */}
              <ReactionBar
                counts={ticket.reactions}
                mine={ticket.viewerReactions}
                disabled={ticket.locked || !canComment}
                onToggle={async (r: ReactionType) => {
                  const summary = await tickets.toggleReaction(project, n, r);
                  // `setTicket` chứ không `applyTicket`: cái sau đặt lại cả tiêu đề và nội dung
                  // đang soạn, nên thả một emoji giữa lúc đang sửa bài là mất phần vừa gõ.
                  setTicket({ ...ticket, reactions: summary.counts, viewerReactions: summary.viewerReactions });
                  return summary;
                }}
              />
            </div>
          </div>

          <Timeline events={showActivity ? events : events.filter((e) => isConversation(e.eventType))} ticket={ticket} slug={project} canWrite={canWrite}
            onEventChanged={(id, p) => setEvents((prev) => prev.map((e) => (e.id === id ? { ...e, ...p } : e)))}
            onEventDeleted={(id) => setEvents((prev) => prev.filter((e) => e.id !== id))} />

          {ticket.locked && !canWrite ? (
            <div className="locked-note">{Icons.lock} Hội thoại đã bị khoá{ticket.activeLockReason ? ` (${ticket.activeLockReason.toLowerCase().replace('_', ' ')})` : ''}. Chỉ nhân viên có quyền Write mới bình luận được.</div>
          ) : commentAllowed && (
            <div className="comment-composer">
              <MarkdownEditor value={comment} onChange={setComment} project={project} placeholder={internal ? tr('Ghi chú nội bộ…') : 'Leave a comment'} disabled={busy} onSubmit={() => submitComment()} />
              {canInternal && (
                <label className="radio" style={{ fontSize: 13 }}>
                  <input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} /><span className="dot" />
                  {tr('Ghi chú nội bộ (khách hàng không nhìn thấy)')}
                </label>
              )}
              <div className="editor-actions">
                {canChangeState && ticket.state === 'OPEN' && (
                  <span className="gh-menu" ref={closeMenuRef}>
                    <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => setCloseMenu((v) => !v)}>
                      {comment.trim() ? 'Close with comment' : 'Close as completed'}
                      <span style={{ opacity: 0.6, marginLeft: 2, display: 'inline-flex' }}>{Icons.chevron}</span>
                    </button>
                    {closeMenu && (
                      <div className="gh-dropdown">
                        <button type="button" className="item" onClick={() => { setCloseMenu(false); void submitComment(() => patch({ state: 'closed', stateReason: 'completed' }, tr('Đã đóng ticket (completed).')), tr('Đã đóng ticket (completed).')); }}>Close as completed</button>
                        <button type="button" className="item" onClick={() => { setCloseMenu(false); void submitComment(() => patch({ state: 'closed', stateReason: 'not_planned' }, tr('Đã đóng ticket (not planned).')), tr('Đã đóng ticket (not planned).')); }}>Close as not planned</button>
                        {canTriage && <button type="button" className="item" onClick={() => { setCloseMenu(false); const d = prompt(tr('Ticket gốc (#N hoặc project#N):')); if (d) void submitComment(() => patch({ state: 'closed', stateReason: 'duplicate', duplicateOf: d }, tr('Đã đóng ticket (duplicate).')), tr('Đã đóng ticket (duplicate).')); }}>Close as duplicate</button>}
                      </div>
                    )}
                  </span>
                )}
                {canChangeState && ticket.state === 'CLOSED' && <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => submitComment(() => patch({ state: 'open' }, tr('Đã mở lại ticket.')), tr('Đã mở lại ticket.'))}>Reopen issue</button>}
                <button type="button" className="btn btn-primary" disabled={busy || !comment.trim()} onClick={() => submitComment()}>{busy ? tr('Đang gửi…') : 'Comment'}</button>
              </div>
            </div>
          )}
        </div>
        <Sidebar ticket={ticket} slug={project} etag={etag} onTicket={applyTicket} onError={setError} />
      </div>
    </>
  );
}

export default function IssuePage() {
  return <Guard permission="ticket.read"><IssueDetail /></Guard>;
}
