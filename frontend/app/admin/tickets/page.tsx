'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { ErrorBox, Guard, PageHead, Select } from '@/components/ui';
import { TypeChip } from '@/components/tickets/Bits';
import { session } from '@/lib/api';
import { tickets, type IssueType, type Priority, type Project, type SlaPolicy, type Ticket } from '@/lib/tickets';
import { tr } from '@/lib/i18n';

const COLORS = ['red', 'blue', 'yellow', 'green', 'orange', 'purple', 'pink', 'gray'];

/** Quản trị toàn hệ thống: Issue Types (org-level như GitHub), SLA policy (Group B), Projects. */
function AdminTickets() {
  const [tab, setTab] = useState<'types' | 'sla' | 'projects'>('types');
  return (
    <>
      <PageHead kicker="Administration" title="SLA & escalation" />
      <div className="settings-nav">
        <button type="button" className={tab === 'types' ? 'active' : ''} onClick={() => setTab('types')}>Issue types</button>
        <button type="button" className={tab === 'sla' ? 'active' : ''} onClick={() => setTab('sla')}>SLA policies</button>
        <button type="button" className={tab === 'projects' ? 'active' : ''} onClick={() => setTab('projects')}>Projects</button>
      </div>
      {tab === 'types' && <Types />}
      {tab === 'sla' && <Sla />}
      {tab === 'projects' && <Projects />}
    </>
  );
}

function Types() {
  const [list, setList] = useState<IssueType[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [form, setForm] = useState({ name: '', color: 'gray', description: '' });
  const can = session.can('issue_type.manage');
  const load = useCallback(() => tickets.issueTypes(true).then(setList).catch(setError), []);
  useEffect(() => { void load(); }, [load]);
  return (
    <>
      {error ? <ErrorBox error={error} /> : null}
      <div className="gh-table">
        {can && (
          <div className="inline-form">
            <div className="field"><label>{tr('Tên')}</label><input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} maxLength={50} /></div>
            <div className="field"><label>{tr('Màu')}</label><Select value={form.color} onChange={(color) => setForm({ ...form, color })} ariaLabel={tr('Màu')} options={COLORS.map((c) => ({ value: c, label: c }))} /></div>
            <div className="field" style={{ flex: 1 }}><label>{tr('Mô tả')}</label><input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} /></div>
            <button type="button" className="primary" disabled={!form.name.trim()} onClick={() => tickets.createIssueType({ name: form.name, color: form.color, description: form.description || undefined }).then(() => { setForm({ name: '', color: 'gray', description: '' }); void load(); }).catch(setError)}>Create</button>
          </div>
        )}
        <div className="gh-table-head"><span>{list.length} issue types (tối đa 25)</span></div>
        {list.map((t) => (
          <div key={t.id} className="gh-table-row">
            <div style={{ width: 160 }}><TypeChip name={t.name} color={t.color} /></div>
            <div className="grow desc">{t.description}{!t.isEnabled && tr(' · đã tắt')}</div>
            {can && <button type="button" className="btn btn-ghost" onClick={() => tickets.updateIssueType(t.id, { name: t.name, color: t.color, description: t.description ?? undefined, isEnabled: !t.isEnabled }).then(load).catch(setError)}>{t.isEnabled ? tr('Tắt') : tr('Bật')}</button>}
          </div>
        ))}
      </div>
    </>
  );
}

/** Còn bao nhiêu phút tới hạn SLA; số âm nghĩa là đã quá hạn. */
function minutesLeft(dueAt: string): number {
  return Math.round((new Date(dueAt).getTime() - Date.now()) / 60000);
}

function humanMinutes(m: number): string {
  const abs = Math.abs(m);
  if (abs < 60) return `${abs}m`;
  const h = Math.floor(abs / 60);
  return abs % 60 === 0 ? `${h}h` : `${h}h ${abs % 60}m`;
}

function Sla() {
  const [list, setList] = useState<SlaPolicy[]>([]);
  const [open, setOpen] = useState<Ticket[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const can = session.can('sla.manage');
  const load = useCallback(() => tickets.slaPolicies().then(setList).catch(setError), []);
  useEffect(() => { void load(); }, [load]);

  // Danh sách "sắp quá hạn" lấy từ chính chỉ mục tìm kiếm: ticket đang mở, có hạn SLA,
  // chưa có phản hồi đầu tiên. Mọi con số dưới đây đếm trên tập này, không ước lượng.
  useEffect(() => {
    tickets.search({ q: 'is:open sort:created-desc', per_page: 100 })
      .then((r) => setOpen(r.items.filter((t) => t.slaDueAt !== null)))
      .catch(() => setOpen([]));
  }, []);

  const tracked = open ?? [];
  const pending = tracked.filter((t) => t.firstResponseAt === null);
  const breached = pending.filter((t) => minutesLeft(t.slaDueAt!) < 0);
  const within = tracked.length === 0 ? null : Math.round(((tracked.length - breached.length) * 1000) / tracked.length) / 10;
  const atRisk = [...pending].sort((a, b) => minutesLeft(a.slaDueAt!) - minutesLeft(b.slaDueAt!)).slice(0, 8);

  const rows: Priority[] = ['P0', 'P1', 'P2', 'P3'];
  return (
    <>
      {error ? <ErrorBox error={error} /> : null}

      {/* Bốn thẻ số liệu của bản mẫu, đọc trên tối đa 100 ticket đang mở có hạn SLA. */}
      <div className="stat-grid">
        <div className="stat-card">
          <div className="value">{within === null ? '—' : `${within}%`}</div>
          <div className="label">{tr('Trong hạn SLA')}</div>
        </div>
        <div className="stat-card">
          <div className="value accent">{breached.length}</div>
          <div className="label">{tr('Đã quá hạn')}</div>
        </div>
        <div className="stat-card">
          <div className="value">{pending.length}</div>
          <div className="label">{tr('Chờ phản hồi đầu')}</div>
        </div>
        <div className="stat-card">
          <div className="value">{tracked.length}</div>
          <div className="label">{tr('Ticket có hạn SLA')}</div>
        </div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
        <h5 style={{ margin: 'var(--space-4) 0 0' }}>At risk now</h5>
        {open === null && <span className="muted" style={{ fontSize: 13 }}>{tr('Đang tải…')}</span>}
        {open !== null && atRisk.length === 0 && <span className="muted" style={{ fontSize: 13 }}>{tr('Không có ticket nào đang chờ phản hồi đầu.')}</span>}
        {atRisk.map((t) => {
          const left = minutesLeft(t.slaDueAt!);
          const tone = left < 0 ? 'tag-accent' : left < 60 ? 'tag-outline' : 'tag-neutral';
          return (
            <div key={t.id} className="risk-row">
              <span className={`tag ${tone}`} style={{ fontFamily: 'var(--font-heading)', fontWeight: 800 }}>
                {left < 0 ? tr('Quá hạn {v0}', { v0: humanMinutes(left) }) : tr('Còn {v0}', { v0: humanMinutes(left) })}
              </span>
              <strong>{t.projectSlug} #{t.number} {t.title}</strong>
              <span className="who">{t.priority ?? tr('chưa đặt')} · {t.assignees.length > 0 ? t.assignees.map((a) => a.login).join(', ') : tr('chưa giao')}</span>
              <Link href={`/projects/${t.projectSlug}/issues/${t.number}`} className="btn btn-secondary" style={{ fontSize: 12 }}>{tr('Mở')}</Link>
            </div>
          );
        })}
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-3)', flexWrap: 'wrap' }}>
        <h5 style={{ margin: 'var(--space-4) 0 0' }}>Policies</h5>
      </div>
      <div className="table-wrap"><table>
        <thead><tr><th>Priority</th><th>{tr('Phản hồi đầu (phút)')}</th><th>{tr('Giải quyết (phút)')}</th><th>{tr('Leo thang sau (phút)')}</th><th>Active</th><th>{tr('Hành động')}</th></tr></thead>
        <tbody>
          {rows.map((p) => {
            const cur = list.find((x) => x.priority === p);
            return <SlaRow key={p} priority={p} policy={cur} can={can} onSave={(body) => tickets.putSlaPolicy(p, body).then(load).catch(setError)} />;
          })}
        </tbody>
      </table></div>
      <div style={{ fontSize: 12, color: 'color-mix(in srgb, var(--color-text) 60%, transparent)' }}>
        {tr('Bộ lập lịch chạy mỗi phút và idempotent — khởi động lại không gửi lại cảnh báo cũ.')}
      </div>
    </>
  );
}

function SlaRow({ priority, policy, can, onSave }: { priority: Priority; policy?: SlaPolicy; can: boolean; onSave: (b: { responseTimeMinutes: number; resolutionTimeMinutes: number; escalateAfterMinutes: number; isActive: boolean }) => void }) {
  const [f, setF] = useState({ responseTimeMinutes: policy?.responseTimeMinutes ?? 60, resolutionTimeMinutes: policy?.resolutionTimeMinutes ?? 480, escalateAfterMinutes: policy?.escalateAfterMinutes ?? 30, isActive: policy?.isActive ?? true });
  useEffect(() => { if (policy) setF({ responseTimeMinutes: policy.responseTimeMinutes, resolutionTimeMinutes: policy.resolutionTimeMinutes, escalateAfterMinutes: policy.escalateAfterMinutes, isActive: policy.isActive }); }, [policy]);
  const num = (k: 'responseTimeMinutes' | 'resolutionTimeMinutes' | 'escalateAfterMinutes') => <td><input type="number" min={1} value={f[k]} disabled={!can} onChange={(e) => setF({ ...f, [k]: Number(e.target.value) })} style={{ width: 100 }} /></td>;
  return (
    <tr>
      <td><span className={`tag ${priority === 'P0' ? 'tag-accent' : 'tag-neutral'}`}>{priority}</span></td>
      {num('responseTimeMinutes')}{num('resolutionTimeMinutes')}{num('escalateAfterMinutes')}
      <td><input type="checkbox" checked={f.isActive} disabled={!can} onChange={(e) => setF({ ...f, isActive: e.target.checked })} style={{ width: 'auto' }} /></td>
      <td>{can && <button type="button" className="btn btn-secondary" onClick={() => onSave(f)}>{tr('Lưu')}</button>}</td>
    </tr>
  );
}

function Projects() {
  const [list, setList] = useState<Project[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [form, setForm] = useState({ slug: '', name: '', description: '' });
  const can = session.can('project.manage');
  const load = useCallback(() => tickets.projects().then(setList).catch(setError), []);
  useEffect(() => { void load(); }, [load]);
  return (
    <>
      {error ? <ErrorBox error={error} /> : null}
      <div className="gh-table">
        {can && (
          <div className="inline-form">
            <div className="field"><label>Slug</label><input value={form.slug} onChange={(e) => setForm({ ...form, slug: e.target.value })} placeholder="billing" /></div>
            <div className="field"><label>{tr('Tên')}</label><input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></div>
            <div className="field" style={{ flex: 1 }}><label>{tr('Mô tả')}</label><input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} /></div>
            <button type="button" className="primary" disabled={!form.slug.trim() || !form.name.trim()} onClick={() => tickets.createProject({ slug: form.slug, name: form.name, description: form.description || undefined }).then(() => { setForm({ slug: '', name: '', description: '' }); void load(); }).catch(setError)}>Create</button>
          </div>
        )}
        <div className="gh-table-head"><span>{list.length} project</span></div>
        {list.map((p) => (
          <div key={p.id} className="gh-table-row">
            <div className="grow"><a href={`/projects/${p.slug}/issues`}><strong>{p.name}</strong></a> <code>{p.slug}</code>{p.isArchived && <span className="pill-count" style={{ marginLeft: 6 }}>archived</span>}<div className="desc">{p.description} · {p.openTickets} mở · {p.closedTickets} đóng</div></div>
            {can && <a href={`/projects/${p.slug}/settings`} className="btn btn-ghost">Settings</a>}
          </div>
        ))}
      </div>
    </>
  );
}

export default function AdminTicketsPage() { return <Guard permission="ticket.read"><AdminTickets /></Guard>; }
