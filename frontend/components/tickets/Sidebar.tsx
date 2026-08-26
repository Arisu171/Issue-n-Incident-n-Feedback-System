'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Avatar, Icons, LabelChip, NoLabelChip, RefLink, TypeChip, UnassignedAvatar } from '@/components/tickets/Bits';
import { session } from '@/lib/api';
import { Select } from '@/components/ui';
import { useDismiss } from '@/lib/useDismiss';
import { tickets, type IssueType, type Label, type Milestone, type Priority, type SubIssue, type Ticket, type TicketRef } from '@/lib/tickets';
import { tr } from '@/lib/i18n';

interface Props {
  ticket: Ticket;
  slug: string;
  etag: string | null;
  onTicket: (t: Ticket, etag: string | null) => void;
  onError: (e: unknown) => void;
}

/** Cột phải kiểu GitHub: Assignees · Labels · Type · Milestone · Priority · Relationships · Notifications · hành động. */
export function Sidebar({ ticket, slug, etag, onTicket, onError }: Props) {
  const canTriage = session.can('ticket.triage');
  const canWrite = session.can('ticket.write');
  const canDelete = session.can('ticket.delete');
  const me = session.user();

  async function patch(body: Parameters<typeof tickets.update>[2]) {
    try {
      const r = await tickets.update(slug, ticket.number, body, etag);
      onTicket(r.data, r.etag);
    } catch (e) { onError(e); }
  }

  return (
    <aside className="sidebar">
      <AssigneesSection ticket={ticket} slug={slug} canTriage={canTriage} meLogin={me?.login ?? null} onTicket={onTicket} onError={onError} />
      <LabelsSection ticket={ticket} slug={slug} canTriage={canTriage} onSave={(labels) => patch({ labels })} />
      <TypeSection ticket={ticket} canTriage={canTriage} onSave={(type) => patch({ type })} />
      <MilestoneSection ticket={ticket} slug={slug} canTriage={canTriage} onSave={(m) => patch({ milestone: m })} />
      <PrioritySection ticket={ticket} canTriage={canTriage} onSave={(p) => patch(p ? { priority: p } : { clearPriority: true })} />
      <RelationshipsSection ticket={ticket} slug={slug} canTriage={canTriage} onError={onError} />
      <NotificationsSection ticket={ticket} />
      {(canWrite || canDelete) && <ActionsSection ticket={ticket} slug={slug} canWrite={canWrite} canDelete={canDelete} onTicket={onTicket} onError={onError} />}
    </aside>
  );
}

function Section({
  title, onGear, gearLabel = tr('Sửa'), open = false, onDismiss, children,
}: {
  title: string;
  onGear?: () => void;
  gearLabel?: string;
  /** Có đang mở lớp chọn hay không — để bật/tắt việc lắng nghe bấm ra ngoài. */
  open?: boolean;
  onDismiss?: () => void;
  children: React.ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const dismiss = useCallback(() => onDismiss?.(), [onDismiss]);
  // Ref bao cả khối, không riêng lớp chọn: nút mở nằm trong khối. Nếu để ngoài thì bấm nút vừa
  // bị tính là "bấm ra ngoài" (đóng) vừa chạy onClick (mở lại), thành ra không đóng được.
  useDismiss(ref, dismiss, open && Boolean(onDismiss));

  return (
    <div className="sb-section" ref={ref}>
      <div className="sb-title">
        <span>{title}</span>
        {onGear && <button type="button" className="gear" onClick={onGear}>{gearLabel}</button>}
      </div>
      <div className="sb-body">{children}</div>
    </div>
  );
}

function AssigneesSection({ ticket, slug, canTriage, meLogin, onTicket, onError }: { ticket: Ticket; slug: string; canTriage: boolean; meLogin: string | null; onTicket: Props['onTicket']; onError: Props['onError'] }) {
  const [open, setOpen] = useState(false);
  const [users, setUsers] = useState<{ login: string; displayName: string }[]>([]);
  const [search, setSearch] = useState('');
  const assigned = new Set(ticket.assignees.map((a) => a.login));

  useEffect(() => {
    if (!open || !canTriage) return;
    tickets.assignableUsers(slug, search || undefined).then(setUsers).catch(onError);
  }, [open, search, canTriage, slug, onError]);

  async function toggle(login: string) {
    try {
      const t = assigned.has(login) ? await tickets.removeAssignees(slug, ticket.number, [login]) : await tickets.addAssignees(slug, ticket.number, [login]);
      onTicket(t, null);
    } catch (e) { onError(e); }
  }

  // Nhận việc cần `ticket.triage` (TicketService.ResolveUsersAsync). Không có nó thì đường "tự
  // nhận" chỉ dẫn tới 422, nên đừng bày ra.
  const canSelfAssign = canTriage && meLogin && !assigned.has(meLogin);
  return (
    <Section title="Assignees" open={open} onDismiss={() => setOpen(false)} onGear={canTriage ? () => setOpen((v) => !v) : undefined}>
      {ticket.assignees.length === 0 && (
        <span className="none">
          <UnassignedAvatar size="sm" withText />
          {canSelfAssign && <> — <a onClick={() => toggle(meLogin!)} style={{ cursor: 'pointer' }}>{tr('tự nhận')}</a></>}
        </span>
      )}
      {ticket.assignees.map((a) => <span key={a.id} className="row-item"><Avatar user={a} /> <Link href={`/profiles/${a.login}`}>{a.login}</Link>{canTriage && <button type="button" className="ghost small" onClick={() => toggle(a.login)}>×</button>}</span>)}
      {open && (
        <div className="sb-picker">
          <div className="head">{tr('Giao cho tối đa 10 người')} <button type="button" className="ghost small" onClick={() => setOpen(false)}>✕</button></div>
          <div className="search"><input placeholder={tr('Tìm người dùng')} value={search} onChange={(e) => setSearch(e.target.value)} /></div>
          {users.map((u) => (
            <label key={u.login} className="opt"><input type="checkbox" checked={assigned.has(u.login)} onChange={() => toggle(u.login)} /> <span>{u.displayName} <span className="muted">@{u.login}</span></span></label>
          ))}
        </div>
      )}
    </Section>
  );
}

function LabelsSection({ ticket, slug, canTriage, onSave }: { ticket: Ticket; slug: string; canTriage: boolean; onSave: (labels: string[]) => void }) {
  const [open, setOpen] = useState(false);
  const [all, setAll] = useState<Label[]>([]);
  const [search, setSearch] = useState('');
  useEffect(() => { if (open) tickets.labels(slug).then(setAll).catch(() => undefined); }, [open, slug]);
  const selected = new Set(ticket.labels.map((l) => l.name));
  function toggle(name: string) {
    const next = new Set(selected);
    if (next.has(name)) next.delete(name); else next.add(name);
    onSave(Array.from(next));
  }
  return (
    <Section title="Labels" open={open} onDismiss={() => setOpen(false)} onGear={canTriage ? () => setOpen((v) => !v) : undefined}>
      <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
        {ticket.labels.length === 0 ? <NoLabelChip /> : ticket.labels.map((l) => <LabelChip key={l.id} label={l} />)}
      </div>
      {open && (
        <div className="sb-picker">
          <div className="head">{tr('Gắn label')} <button type="button" className="ghost small" onClick={() => setOpen(false)}>✕</button></div>
          <div className="search"><input placeholder={tr('Lọc label')} value={search} onChange={(e) => setSearch(e.target.value)} /></div>
          {all.filter((l) => l.name.includes(search.toLowerCase())).map((l) => (
            <label key={l.id} className="opt"><input type="checkbox" checked={selected.has(l.name)} onChange={() => toggle(l.name)} /> <LabelChip label={l} /> <span className="muted">{l.description}</span></label>
          ))}
          <div style={{ padding: 8 }}><Link href={`/projects/${slug}/labels`}>{tr('Quản lý label')}</Link></div>
        </div>
      )}
    </Section>
  );
}

function TypeSection({ ticket, canTriage, onSave }: { ticket: Ticket; canTriage: boolean; onSave: (type: string) => void }) {
  const [open, setOpen] = useState(false);
  const [types, setTypes] = useState<IssueType[]>([]);
  useEffect(() => { if (open) tickets.issueTypes().then(setTypes).catch(() => undefined); }, [open]);
  return (
    <Section title="Type" open={open} onDismiss={() => setOpen(false)} onGear={canTriage ? () => setOpen((v) => !v) : undefined}>
      {ticket.type ? <TypeChip name={ticket.type.name} color={ticket.type.color} /> : <span className="none">{tr('Chưa có')}</span>}
      {open && (
        <div className="sb-picker">
          <div className="head">{tr('Chọn loại')} <button type="button" className="ghost small" onClick={() => setOpen(false)}>✕</button></div>
          <label className="opt"><input type="radio" checked={!ticket.type} onChange={() => { onSave(''); setOpen(false); }} /> {tr('Không có')}</label>
          {types.map((t) => <label key={t.id} className="opt"><input type="radio" checked={ticket.type?.id === t.id} onChange={() => { onSave(t.name); setOpen(false); }} /> <TypeChip name={t.name} color={t.color} /> <span className="muted">{t.description}</span></label>)}
        </div>
      )}
    </Section>
  );
}

function MilestoneSection({ ticket, slug, canTriage, onSave }: { ticket: Ticket; slug: string; canTriage: boolean; onSave: (number: number) => void }) {
  const [open, setOpen] = useState(false);
  const [list, setList] = useState<Milestone[]>([]);
  useEffect(() => { if (open) tickets.milestones(slug, 'open').then(setList).catch(() => undefined); }, [open, slug]);
  const m = ticket.milestone;
  const pct = m && m.openCount + m.closedCount > 0 ? Math.round((m.closedCount * 100) / (m.openCount + m.closedCount)) : 0;
  return (
    <Section title="Milestone" open={open} onDismiss={() => setOpen(false)} onGear={canTriage ? () => setOpen((v) => !v) : undefined}>
      {m ? (
        <>
          <div className="progress"><span style={{ width: `${pct}%` }} /></div>
          <Link href={`/projects/${slug}/milestones`}>{m.title}</Link>
          <span className="muted">{pct}% hoàn thành{m.dueOn ? tr(' · hạn {v0}', { v0: m.dueOn }) : ''}</span>
        </>
      ) : <span className="none">{tr('Chưa có')}</span>}
      {open && (
        <div className="sb-picker">
          <div className="head">{tr('Chọn milestone')} <button type="button" className="ghost small" onClick={() => setOpen(false)}>✕</button></div>
          <label className="opt"><input type="radio" checked={!m} onChange={() => { onSave(0); setOpen(false); }} /> {tr('Không có')}</label>
          {list.map((x) => <label key={x.id} className="opt"><input type="radio" checked={m?.id === x.id} onChange={() => { onSave(x.number); setOpen(false); }} /> {x.title} <span className="muted">{x.dueOn ?? ''}</span></label>)}
        </div>
      )}
    </Section>
  );
}

function PrioritySection({ ticket, canTriage, onSave }: { ticket: Ticket; canTriage: boolean; onSave: (p: Priority | null) => void }) {
  const [open, setOpen] = useState(false);
  const overdue = ticket.slaDueAt && !ticket.firstResponseAt && ticket.state === 'OPEN' && new Date(ticket.slaDueAt) < new Date();
  return (
    <Section title="Priority / SLA" open={open} onDismiss={() => setOpen(false)} onGear={canTriage ? () => setOpen((v) => !v) : undefined}>
      {ticket.priority ? (
        <div style={{ display: 'flex', gap: 'var(--space-2)', alignItems: 'center', flexWrap: 'wrap' }}>
          <span className={`tag ${ticket.priority === 'P0' ? 'tag-accent' : 'tag-neutral'}`} style={{ fontFamily: 'var(--font-heading)', fontWeight: 800 }}>{ticket.priority}</span>
          {ticket.slaDueAt && (
            <span style={{ fontSize: 13, color: overdue ? 'var(--color-accent-700)' : 'color-mix(in srgb, var(--color-text) 55%, transparent)' }}>
              {overdue ? tr('Quá hạn · ') : tr('Phản hồi trước ')}{new Date(ticket.slaDueAt).toLocaleString('vi-VN')}
            </span>
          )}
        </div>
      ) : <span className="none">{tr('Không có')}</span>}
      {open && (
        <div className="sb-picker">
          <div className="head">{tr('Ưu tiên')} <button type="button" className="ghost small" onClick={() => setOpen(false)}>✕</button></div>
          <label className="opt"><input type="radio" checked={!ticket.priority} onChange={() => { onSave(null); setOpen(false); }} /> {tr('Không có')}</label>
          {(['P0', 'P1', 'P2', 'P3'] as Priority[]).map((p) => <label key={p} className="opt"><input type="radio" checked={ticket.priority === p} onChange={() => { onSave(p); setOpen(false); }} /> {p}</label>)}
        </div>
      )}
    </Section>
  );
}

function RelationshipsSection({ ticket, slug, canTriage, onError }: { ticket: Ticket; slug: string; canTriage: boolean; onError: Props['onError'] }) {
  const [subs, setSubs] = useState<SubIssue[]>([]);
  const [blockedBy, setBlockedBy] = useState<TicketRef[]>([]);
  const [blocking, setBlocking] = useState<TicketRef[]>([]);
  const [input, setInput] = useState('');
  const [mode, setMode] = useState<'sub' | 'blocked' | null>(null);

  async function load() {
    try {
      const [s, b, g] = await Promise.all([tickets.subIssues(slug, ticket.number), tickets.blockedBy(slug, ticket.number), tickets.blocking(slug, ticket.number)]);
      setSubs(s); setBlockedBy(b); setBlocking(g);
    } catch (e) { onError(e); }
  }
  useEffect(() => { void load(); // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ticket.id, ticket.version]);

  async function add() {
    if (!input.trim()) return;
    try {
      if (mode === 'sub') await tickets.addSubIssue(slug, ticket.number, input.trim());
      else await tickets.addBlockedBy(slug, ticket.number, input.trim());
      setInput(''); setMode(null); await load();
    } catch (e) { onError(e); }
  }

  const s = ticket.subIssuesSummary;
  return (
    <Section title="Relationships">
      {ticket.parent && <div><span className="muted">Parent</span><RefLink t={ticket.parent} /></div>}
      <div>
        <span className="muted">Sub-issues {s.total > 0 && <>· {s.completed}/{s.total} ({s.percentCompleted}%)</>}</span>
        {s.total > 0 && <div className="progress" style={{ margin: '4px 0' }}><span style={{ width: `${s.percentCompleted}%` }} /></div>}
        {subs.map((x) => <div key={x.ticket.id} className="row-item"><RefLink t={x.ticket} />{canTriage && <button type="button" className="ghost small" onClick={() => tickets.removeSubIssue(slug, ticket.number, `#${x.ticket.number}`).then(load).catch(onError)}>×</button>}</div>)}
      </div>
      {blockedBy.length > 0 && <div><span className="blocked-chip">⛔ Blocked by</span>{blockedBy.map((x) => <div key={x.id} className="row-item"><RefLink t={x} />{canTriage && <button type="button" className="ghost small" onClick={() => tickets.removeBlockedBy(slug, ticket.number, `${x.projectSlug}#${x.number}`).then(load).catch(onError)}>×</button>}</div>)}</div>}
      {blocking.length > 0 && <div><span className="muted">Blocking</span>{blocking.map((x) => <RefLink key={x.id} t={x} />)}</div>}
      {canTriage && (
        <div style={{ display: 'flex', gap: 'var(--space-2)', flexWrap: 'wrap' }}>
          <button type="button" className="btn btn-secondary" style={{ fontSize: 12 }} onClick={() => setMode(mode === 'sub' ? null : 'sub')}>+ Sub-issue</button>
          <button type="button" className="btn btn-secondary" style={{ fontSize: 12 }} onClick={() => setMode(mode === 'blocked' ? null : 'blocked')}>+ Blocked by</button>
          <Link href={`/projects/${slug}/issues/new?parent=${ticket.number}`} className="btn btn-ghost" style={{ fontSize: 12 }}>{tr('Tạo sub-issue mới')}</Link>
        </div>
      )}
      {mode && (
        <div style={{ display: 'flex', gap: 4, marginTop: 4 }}>
          <input placeholder={tr('#N hoặc project#N')} value={input} onChange={(e) => setInput(e.target.value)} onKeyDown={(e) => e.key === 'Enter' && add()} />
          <button type="button" className="small" onClick={add}>{tr('Thêm')}</button>
        </div>
      )}
    </Section>
  );
}

function NotificationsSection({ ticket }: { ticket: Ticket }) {
  const [state, setState] = useState<{ subscribed: boolean; ignored: boolean } | null>(null);
  useEffect(() => { tickets.subscription(ticket.id).then((s) => setState({ subscribed: s.subscribed, ignored: s.ignored })).catch(() => undefined); }, [ticket.id]);
  async function toggle() {
    const next = !(state?.subscribed ?? false);
    const s = await tickets.setSubscription(ticket.id, next);
    setState({ subscribed: s.subscribed, ignored: s.ignored });
  }
  return (
    <Section title="Notifications">
      <button type="button" className="btn btn-secondary btn-block" onClick={toggle}>{state?.subscribed ? 'Unsubscribe' : 'Subscribe'}</button>
      <span className="muted">{state?.subscribed ? tr('Bạn đang nhận thông báo vì bạn theo dõi ticket này.') : tr('Bạn chưa nhận thông báo từ ticket này.')}</span>
    </Section>
  );
}

function ActionsSection({ ticket, slug, canWrite, canDelete, onTicket, onError }: { ticket: Ticket; slug: string; canWrite: boolean; canDelete: boolean; onTicket: Props['onTicket']; onError: Props['onError'] }) {
  const [projects, setProjects] = useState<{ slug: string; name: string }[]>([]);
  const [transferTo, setTransferTo] = useState('');
  // Đi bằng router chứ không gán thẳng địa chỉ trình duyệt: cách sau tải lại **cả tài liệu**,
  // trình duyệt vứt trang đang có, vẽ một khung trắng rồi mới dựng lại từ đầu — đúng cú nháy
  // trắng thấy được bằng mắt. Router của Next chuyển trang trong cùng tài liệu.
  const router = useRouter();
  async function act(fn: () => Promise<Ticket>) { try { onTicket(await fn(), null); } catch (e) { onError(e); } }
  return (
    <div className="sb-section sb-actions">
      {canWrite && (
        <>
          <button type="button" className="btn btn-secondary btn-block" onClick={() => act(() => ticket.pinned ? tickets.pin(slug, ticket.number, false) : tickets.pin(slug, ticket.number, true))}>{ticket.pinned ? tr('Bỏ ghim ticket') : 'Ghim ticket'}</button>
          <button type="button" onClick={() => {
            if (ticket.locked) return void act(() => tickets.unlock(slug, ticket.number));
            const reason = prompt(tr('Lý do khoá (off_topic / too_heated / resolved / spam) — để trống nếu không có:')) ?? '';
            const map: Record<string, 'OFF_TOPIC' | 'TOO_HEATED' | 'RESOLVED' | 'SPAM'> = { off_topic: 'OFF_TOPIC', too_heated: 'TOO_HEATED', resolved: 'RESOLVED', spam: 'SPAM' };
            void act(() => tickets.lock(slug, ticket.number, map[reason.trim().toLowerCase()] ?? null));
          }} className="btn btn-secondary btn-block">{ticket.locked ? tr('Mở khoá hội thoại') : tr('Khoá hội thoại')}</button>
          <button type="button" className="btn btn-secondary btn-block" onClick={async () => { if (projects.length === 0) setProjects(await tickets.projects()); setTransferTo(transferTo ? '' : (projects[0]?.slug ?? '')); }}>{tr('Chuyển project')}</button>
          {transferTo !== '' && (
            // Ô tên project chiếm phần còn lại và tự cắt chữ khi tên dài; nút xác nhận là ô vuông
            // cỡ chuẩn nằm sát phải. Trước đây ô không đặt `flex` nên bề ngang của cả hàng chạy
            // theo tên project dài nhất, và nút bị đẩy đi mỗi khi danh sách khác nhau.
            <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center' }}>
              <Select
                value={transferTo}
                onChange={setTransferTo}
                style={{ flex: 1, minWidth: 0 }}
                ariaLabel={tr('Chuyển project')}
                options={projects.filter((p) => p.slug !== slug).map((p) => ({ value: p.slug, label: p.name }))}
              />
              <button type="button" className="btn btn-primary btn-icon" title={tr('Chuyển')} aria-label={tr('Chuyển')}
                onClick={() => act(async () => { const t = await tickets.transfer(slug, ticket.number, transferTo); router.push(`/projects/${t.projectSlug}/issues/${t.number}`); return t; })}>
                {Icons.check}
              </button>
            </div>
          )}
        </>
      )}
      {canDelete && <button type="button" className="btn btn-secondary btn-block danger" onClick={() => { if (confirm(tr('Xóa vĩnh viễn ticket này? Số ticket sẽ không được cấp lại.'))) tickets.remove(slug, ticket.number).then(() => { router.push(`/projects/${slug}/issues`); }).catch(onError); }}>{tr('Xóa ticket')}</button>}
    </div>
  );
}
