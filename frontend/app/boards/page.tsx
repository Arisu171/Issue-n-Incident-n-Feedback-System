'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { ErrorBox, Guard, PageHead } from '@/components/ui';
import { QueryInput } from '@/components/QueryInput';
import { Icons } from '@/components/tickets/Bits';
import { session } from '@/lib/api';
import { tickets, type Board } from '@/lib/tickets';
import { timeAgo } from '@/lib/util';
import { tr } from '@/lib/i18n';

function Boards() {
  const [list, setList] = useState<Board[]>([]);
  const [showClosed, setShowClosed] = useState(false);
  // Board không phân trang — `GET /api/boards` trả về trọn danh sách — nên lọc ngay tại chỗ là
  // đủ và đúng: không có trang sau nào để bỏ sót, và không phải đi thêm một vòng mạng.
  const [q, setQ] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [name, setName] = useState('');
  const canWrite = session.can('board.write');
  const load = useCallback(() => tickets.boards(showClosed).then(setList).catch(setError), [showClosed]);
  const visible = q ? list.filter((b) => b.name.toLowerCase().includes(q.toLowerCase())) : list;
  useEffect(() => { void load(); }, [load]);

  return (
    <>
      <PageHead
        kicker="Cross-project"
        title="Boards"
        hint={tr('Bảng Kanban: kéo-thả thẻ giữa cột, tự động chuyển cột khi đóng ticket.')}
        actions={canWrite ? (
          <form style={{ display: 'flex', gap: 'var(--space-2)' }} onSubmit={(e) => { e.preventDefault(); if (name.trim()) tickets.createBoard({ name }).then(() => { setName(''); void load(); }).catch(setError); }}>
            <input className="input" placeholder={tr('Tên board mới')} value={name} onChange={(e) => setName(e.target.value)} />
            <button type="submit" className="btn btn-primary">New board</button>
          </form>
        ) : undefined}
      />
      {error ? <ErrorBox error={error} /> : null}
      <div className="issues-toolbar" style={{ marginBottom: 16 }}>
        <QueryInput
          className="search"
          value={q}
          onSubmit={(next) => setQ(next.trim())}
          prefix={Icons.search}
          placeholder={tr('Tìm board')}
          ariaLabel={tr('Tìm board')}
        />
      </div>

      <div className="issue-list">
        <div className="issue-list-head">
          <button type="button" className={`tab ${!showClosed ? 'active' : ''}`} onClick={() => setShowClosed(false)}>Open</button>
          <button type="button" className={`tab ${showClosed ? 'active' : ''}`} onClick={() => setShowClosed(true)}>All</button>
        </div>
        {visible.length === 0 && (
          <div className="empty">{Icons.project}<div>{q ? tr('Không có board nào khớp.') : tr('Chưa có board.')}</div></div>
        )}
        {visible.map((b) => (
          <article key={b.id} className={`issue-row${b.isClosed ? ' closed' : ''}`}>
            <div className="line">
              <div className="left">
                <Link href={`/boards/${b.id}`} className="title">{b.name}</Link>
              </div>
              {b.isClosed && <div className="right"><span className="tag tag-neutral">{tr('đã đóng')}</span></div>}
            </div>
            <div className="line">
              <div className="left">
                <span className="state-icon">{Icons.project}</span>
                <span className="meta">
                  {b.description}{b.description ? ' · ' : ''}{b.visibility.toLowerCase()} · tạo {timeAgo(b.createdAt)}{b.createdBy && tr(' bởi {v0}', { v0: b.createdBy.login })}
                </span>
              </div>
              <div className="right">
                <span className="stat">{b.columns.length} cột</span>
                <span className="stat">{b.itemCount} thẻ</span>
              </div>
            </div>
          </article>
        ))}
      </div>
    </>
  );
}

export default function BoardsPage() { return <Guard permission="ticket.read"><Boards /></Guard>; }
