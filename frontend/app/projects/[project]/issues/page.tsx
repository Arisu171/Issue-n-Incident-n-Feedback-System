'use client';

import Link from 'next/link';
import { useParams, useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useCallback, useEffect, useRef, useState } from 'react';
import { ActionFeedback, Guard, PageHead } from '@/components/ui';
import { Icons, IssueRow } from '@/components/tickets/Bits';
import { useAction } from '@/lib/useAction';
import { session } from '@/lib/api';
import { tickets, type CursorPage, type Label, type Milestone, type Ticket } from '@/lib/tickets';
import { stateFromDsl } from '@/lib/util';
import { tr } from '@/lib/i18n';
import { useDismiss } from '@/lib/useDismiss';
import { QueryInput } from '@/components/QueryInput';

/**
 * Nút mở menu lọc — nút phụ kèm mũi tên, đúng như bản vẽ.
 *
 * `<details>` gốc chỉ đóng khi bấm lại vào `<summary>`; ở đây thêm đóng khi bấm ra ngoài và
 * khi bấm Escape, cho giống mọi menu khác trong ứng dụng.
 */
function FilterMenu({ label, children }: { label: string; children: React.ReactNode }) {
  const ref = useRef<HTMLDetailsElement>(null);
  const [open, setOpen] = useState(false);
  const close = useCallback(() => {
    if (ref.current) ref.current.open = false;
    setOpen(false);
  }, []);
  useDismiss(ref, close, open);

  return (
    <details className="filter-menu" ref={ref} onToggle={(e) => setOpen(e.currentTarget.open)}>
      <summary className="btn btn-secondary">{label}{Icons.chevron}</summary>
      {/* Chọn xong thì đóng luôn — người dùng không phải bấm lại vào nút. */}
      <div className="gh-dropdown" onClick={close}>{children}</div>
    </details>
  );
}

function IssuesList() {
  const { project } = useParams<{ project: string }>();
  const router = useRouter();
  const search = useSearchParams();
  const q = search.get('q') ?? 'is:open';
  const cursor = search.get('cursor');
  const state = stateFromDsl(q);

  const [page, setPage] = useState<CursorPage<Ticket> | null>(null);
  const [labels, setLabels] = useState<Label[]>([]);
  const [milestones, setMilestones] = useState<Milestone[]>([]);
  const load = useAction<CursorPage<Ticket>>({ latest: true });

  const run = useCallback(async () => {
    const r = await load.run(() => tickets.list(project, { state: 'all', q, cursor: cursor ?? undefined, per_page: 25 }));
    if (r) setPage(r);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project, q, cursor]);

  useEffect(() => { void run(); }, [run]);
  useEffect(() => {
    tickets.labels(project).then(setLabels).catch(() => undefined);
    tickets.milestones(project, 'open').then(setMilestones).catch(() => undefined);
  }, [project]);

  function go(next: string) { router.push(`/projects/${project}/issues?q=${encodeURIComponent(next.trim())}`); }
  function withState(s: 'open' | 'closed') { go(q.replace(/\b(is|state):(open|closed)\b/g, '').trim() + ` is:${s}`); }
  function addQualifier(qualifier: string) { go(q.includes(qualifier) ? q : `${q} ${qualifier}`); }

  const counts = page?.totalCount ?? 0;
  return (
    <>
      <PageHead
        kicker={project}
        title="Issues"
        actions={<>
          <Link href={`/projects/${project}/labels`} className="btn btn-secondary">Labels</Link>
          <Link href={`/projects/${project}/labels`} className="btn btn-secondary">Milestones</Link>
          {session.can('ticket.create') && <Link href={`/projects/${project}/issues/new`} className="btn btn-primary">New issue</Link>}
        </>}
      />

      <div className="issues-toolbar">
        <QueryInput
          className="search"
          value={q}
          onSubmit={go}
          prefix={<span className="filters-btn">DSL</span>}
          placeholder="is:open label:incident sort:updated-desc"
          ariaLabel="Query DSL"
        />
        <FilterMenu label="Label">
          {labels.length === 0 && <div className="head">{tr('Chưa có label')}</div>}
          {labels.map((l) => <button key={l.id} type="button" className="item" onClick={() => addQualifier(/\s/.test(l.name) ? `label:"${l.name}"` : `label:${l.name}`)}>{l.name}</button>)}
        </FilterMenu>
        <FilterMenu label="Milestone">
          <button type="button" className="item" onClick={() => addQualifier('no:milestone')}>{tr('Không có milestone')}</button>
          {milestones.map((m) => <button key={m.id} type="button" className="item" onClick={() => addQualifier(/\s/.test(m.title) ? `milestone:"${m.title}"` : `milestone:${m.title}`)}>{m.title}</button>)}
        </FilterMenu>
        <FilterMenu label="Assignee">
          <button type="button" className="item" onClick={() => addQualifier('assignee:@me')}>{tr('Giao cho tôi')}</button>
          <button type="button" className="item" onClick={() => addQualifier('no:assignee')}>{tr('Chưa giao')}</button>
          <button type="button" className="item" onClick={() => addQualifier('author:@me')}>{tr('Do tôi tạo')}</button>
        </FilterMenu>
        <FilterMenu label="Sort">
          {[['created-desc', tr('Mới nhất')], ['created-asc', tr('Cũ nhất')], ['updated-desc', tr('Cập nhật gần đây')], ['comments-desc', tr('Nhiều bình luận')], ['reactions-desc', tr('Nhiều reaction')]].map(([v, l]) => (
            <button key={v} type="button" className="item" onClick={() => go(q.replace(/\bsort:\S+/g, '').trim() + ` sort:${v}`)}>{l}</button>
          ))}
        </FilterMenu>
      </div>

      <ActionFeedback action={load} processingLabel={tr('Đang tải danh sách…')} />

      <div className="issue-list">
        <div className="issue-list-head">
          <button type="button" className={`tab ${state === 'open' ? 'active' : ''}`} onClick={() => withState('open')}>
            {Icons.issueOpened} {state === 'open' ? counts : ''} Open
          </button>
          <button type="button" className={`tab ${state === 'closed' ? 'active' : ''}`} onClick={() => withState('closed')}>
            {Icons.issueClosed} {state === 'closed' ? counts : ''} Closed
          </button>
        </div>
        {page && page.items.length === 0 && <div className="empty">{Icons.issueOpened}<div>{tr('Không có ticket nào khớp bộ lọc.')}</div></div>}
        {page?.items.map((t) => <IssueRow key={t.id} t={t} />)}
      </div>

      <div className="pager">
        {page?.nextCursor && (
          <button type="button" className="btn btn-secondary" onClick={() => router.push(`/projects/${project}/issues?q=${encodeURIComponent(q)}&cursor=${encodeURIComponent(page.nextCursor!)}`)}>Trang sau →</button>
        )}
        <span>Cursor pagination · 25 mỗi trang{page?.totalCount != null ? tr(' · {v0} kết quả', { v0: page.totalCount }) : ''}</span>
      </div>

      <p className="muted" style={{ fontSize: 12 }}>
        {tr('Cú pháp:')} <code>is:open label:bug assignee:@me</code>, <code>-label:wontfix</code>, <code>created:&gt;2026-01-01</code>, <code>(a OR b)</code>.
      </p>
    </>
  );
}

export default function IssuesPage() {
  return (
    <Guard permission="ticket.read">
      <Suspense fallback={<div className="empty">{tr('Đang tải…')}</div>}><IssuesList /></Suspense>
    </Guard>
  );
}
