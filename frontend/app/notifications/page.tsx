'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { ErrorBox, Guard, PageHead } from '@/components/ui';
import { QueryInput } from '@/components/QueryInput';
import { Icons } from '@/components/tickets/Bits';
import { useNotificationRealtime } from '@/lib/realtime';
import { tickets, type NotificationThread } from '@/lib/tickets';
import { timeAgo } from '@/lib/util';
import { tr } from '@/lib/i18n';

const REASON_TEXT: Record<string, string> = {
  ASSIGN: 'Bạn được giao', AUTHOR: 'Bạn tạo ticket', COMMENT: 'Bạn đã bình luận', MANUAL: 'Đăng ký thủ công',
  MENTION: 'Bạn được nhắc', STATE_CHANGE: 'Thay đổi trạng thái', SUBSCRIBED: 'Bạn theo dõi',
  TEAM_MENTION: 'Nhóm được nhắc', SLA_BREACH: 'SLA quá hạn',
};

/** Inbox thông báo (≈ github.com/notifications): Inbox / Saved / Done, unread dot, Mark all as read, real-time. */
function Inbox() {
  const [tab, setTab] = useState<'inbox' | 'saved' | 'done'>('inbox');
  const [page, setPage] = useState<{ items: NotificationThread[]; nextCursor: string | null } | null>(null);
  const [error, setError] = useState<unknown>(null);
  // Chữ tìm gửi lên server: hộp thư cuộn theo con trỏ, lọc trên mảng đã tải chỉ lọc được phần đã
  // cuộn tới và trang sau sẽ nhảy qua đúng những dòng vừa bị loại.
  const [q, setQ] = useState('');

  const load = useCallback((cursor?: string) => tickets.notifications({ all: tab === 'inbox', saved: tab === 'saved' || undefined, done: tab === 'done' || undefined, q: q || undefined, cursor })
    .then((r) => setPage((p) => cursor && p ? { items: [...p.items, ...r.items], nextCursor: r.nextCursor } : r)).catch(setError), [tab, q]);
  useEffect(() => { void load(); }, [load]);
  useNotificationRealtime(() => { void load(); });

  async function patch(n: NotificationThread, body: { unread?: boolean; saved?: boolean; done?: boolean }) {
    try { const u = await tickets.patchNotification(n.id, body); setPage((p) => p && { ...p, items: p.items.map((x) => (x.id === n.id ? u : x)) }); } catch (e) { setError(e); }
  }

  return (
    <>
      <PageHead
        kicker={page ? tr('{v0} chưa đọc', { v0: page.items.filter((n) => n.unread).length }) : 'Inbox'}
        title="Inbox"
        actions={<button type="button" className="btn btn-secondary" onClick={() => tickets.markAllRead().then(() => load()).catch(setError)}>Mark all read</button>}
      />
      {error ? <ErrorBox error={error} /> : null}

      <div className="issues-toolbar" style={{ marginBottom: 16 }}>
        <QueryInput
          className="search"
          value={q}
          onSubmit={(next) => setQ(next.trim())}
          prefix={Icons.search}
          placeholder={tr('Tìm ticket')}
          ariaLabel={tr('Tìm thông báo')}
        />
      </div>

      {/* Hàng tab của bản mẫu: kẻ trên và dưới, chú thích "một luồng mỗi ticket" dạt phải. */}
      <div className="inbox-tabs">
        <button type="button" className={tab === 'inbox' ? 'active' : ''} onClick={() => setTab('inbox')}>Unread</button>
        <button type="button" className={tab === 'saved' ? 'active' : ''} onClick={() => setTab('saved')}>Saved</button>
        <button type="button" className={tab === 'done' ? 'active' : ''} onClick={() => setTab('done')}>Done</button>
        <span className="hint">One thread per issue</span>
      </div>

      <div>
        {page && page.items.length === 0 && <div className="empty">{Icons.inbox}<div>{tr('Không có thông báo.')}</div></div>}
        {page?.items.map((n) => (
          <div key={n.id} className={`notif-row ${n.unread ? 'unread' : ''}`}>
            <span className="dot" />
            <div className="grow">
              <div className="kicker-row">{tr(REASON_TEXT[n.reason] ?? n.reason.toLowerCase())}</div>
              <Link href={`/projects/${n.ticket.projectSlug}/issues/${n.ticket.number}`} onClick={() => n.unread && patch(n, { unread: false })}>{n.ticket.title}</Link>
              <div className="reason">
                {n.ticket.projectSlug} #{n.ticket.number} · {n.lastEventType?.toLowerCase().replace(/_/g, ' ')}
                {n.lastActor && <> bởi {n.lastActor.login}</>} · {timeAgo(n.updatedAt)}
              </div>
            </div>
            <div className="notif-actions">
              <button type="button" className="btn btn-secondary" style={{ fontSize: 12 }} onClick={() => patch(n, { done: !n.isDone })}>{n.isDone ? 'Undone' : 'Done'}</button>
              <button type="button" className="btn btn-secondary" style={{ fontSize: 12 }} onClick={() => patch(n, { saved: !n.isSaved })}>{n.isSaved ? 'Unsave' : 'Save'}</button>
              <button type="button" className="btn btn-secondary" style={{ fontSize: 12 }} onClick={() => patch(n, { unread: !n.unread })}>{n.unread ? tr('Đã đọc') : tr('Chưa đọc')}</button>
            </div>
          </div>
        ))}
      </div>
      {page?.nextCursor && <div className="pager"><button type="button" className="btn btn-secondary" onClick={() => load(page.nextCursor!)}>{tr('Tải thêm')}</button></div>}
    </>
  );
}

export default function NotificationsPage() { return <Guard permission="ticket.read"><Inbox /></Guard>; }
