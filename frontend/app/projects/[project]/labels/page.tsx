'use client';

import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import { ErrorBox, Guard, PageHead } from '@/components/ui';
import { LabelChip } from '@/components/tickets/Bits';
import { session } from '@/lib/api';
import { tickets, type Label, type Milestone } from '@/lib/tickets';
import { tr } from '@/lib/i18n';

const PALETTE = ['d73a4a', '0075ca', 'cfd3d7', 'a2eeef', '7057ff', '008672', 'e4e669', 'd876e3', 'ffffff', 'bfd4f2', 'fbca04', '0e8a16', 'b60205', '5319e7', '1d76db'];

/**
 * Trang Labels & milestones (≈ github.com/{repo}/labels): danh sách, tạo, sửa tại chỗ, xóa —
 * cho **cả hai** loại.
 *
 * Milestone trước đây có một trang riêng, và nút "New milestone" ở đây chỉ dẫn sang trang đó để
 * người dùng bấm nút thứ hai mới thấy biểu mẫu. Hai lần bấm cho một việc, trong khi danh sách
 * milestone đã nằm ngay trên màn hình này. Trang riêng đã bỏ; mọi thao tác làm tại chỗ.
 */
function Labels() {
  const { project } = useParams<{ project: string }>();
  const [labels, setLabels] = useState<Label[]>([]);
  const [milestones, setMilestones] = useState<Milestone[]>([]);
  // Cùng quy tắc với hàng lọc label: không tick gì = xem tất cả. Hai ô độc lập chứ không phải
  // hai tab loại trừ nhau, nên xem chung mở lẫn đóng cũng là một lựa chọn hợp lệ.
  const [milestoneStates, setMilestoneStates] = useState({ open: false, closed: false });
  const [error, setError] = useState<unknown>(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const [form, setForm] = useState({ name: '', color: PALETTE[0], description: '' });

  // Lọc label theo nguồn gốc. Không tick gì = xem tất cả, nên trạng thái mặc định không giấu mất
  // hàng nào — người dùng phải thấy đủ trước khi tự thu hẹp.
  const [labelKinds, setLabelKinds] = useState({ builtin: false, added: false });

  const [milestoneEditing, setMilestoneEditing] = useState<Milestone | 'new' | null>(null);
  const [milestoneForm, setMilestoneForm] = useState({ title: '', description: '', dueOn: '' });

  const canWrite = session.can('label.write');
  // Một màn hình, một mức quyền: ai không sửa được label thì cũng không sửa được milestone. Chỉ
  // xét `milestone.write` ở đây sẽ bày ra một màn hình nửa sửa được nửa không.
  const canWriteMilestone = canWrite && session.can('milestone.write');

  const load = useCallback(() => tickets.labels(project).then(setLabels).catch(setError), [project]);
  useEffect(() => { void load(); }, [load]);

  // Chỉ tick đúng một ô mới thu hẹp; không tick gì, hoặc tick cả hai, đều là "tất cả".
  const milestoneQuery: 'open' | 'closed' | 'all' =
    milestoneStates.open === milestoneStates.closed ? 'all' : milestoneStates.open ? 'open' : 'closed';

  const loadMilestones = useCallback(
    () => tickets.milestones(project, milestoneQuery).then(setMilestones).catch(setError),
    [project, milestoneQuery],
  );
  useEffect(() => { void loadMilestones(); }, [loadMilestones]);

  function startEdit(l: Label) { setEditing(l.name); setForm({ name: l.name, color: l.colorHex, description: l.description ?? '' }); setCreating(false); }
  function startCreate() { setCreating(true); setEditing(null); setForm({ name: '', color: PALETTE[Math.floor(Math.random() * PALETTE.length)], description: '' }); }

  async function save() {
    setError(null);
    try {
      if (editing) await tickets.updateLabel(project, editing, { newName: form.name !== editing ? form.name : undefined, color: form.color, description: form.description });
      else await tickets.createLabel(project, { name: form.name, color: form.color, description: form.description || undefined });
      setCreating(false); setEditing(null); await load();
    } catch (e) { setError(e); }
  }

  function startCreateMilestone() {
    setMilestoneEditing('new');
    setMilestoneForm({ title: '', description: '', dueOn: '' });
  }

  function startEditMilestone(m: Milestone) {
    setMilestoneEditing(m);
    setMilestoneForm({ title: m.title, description: m.description ?? '', dueOn: m.dueOn ?? '' });
  }

  async function saveMilestone() {
    setError(null);
    try {
      if (milestoneEditing === 'new') {
        await tickets.createMilestone(project, {
          title: milestoneForm.title,
          description: milestoneForm.description || undefined,
          dueOn: milestoneForm.dueOn || undefined,
        });
      } else if (milestoneEditing) {
        await tickets.updateMilestone(project, milestoneEditing.number, {
          title: milestoneForm.title,
          description: milestoneForm.description,
          dueOn: milestoneForm.dueOn || undefined,
          clearDueOn: !milestoneForm.dueOn,
        });
      }
      setMilestoneEditing(null); await loadMilestones();
    } catch (e) { setError(e); }
  }

  const noKindPicked = !labelKinds.builtin && !labelKinds.added;
  const visibleLabels = labels.filter((l) =>
    noKindPicked || (labelKinds.builtin && l.isDefault) || (labelKinds.added && !l.isDefault));

  const formEl = (
    <div className="inline-form">
      <div className="field" style={{ flex: 1 }}><label>{tr('Tên')}</label><input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="bug" maxLength={50} /></div>
      <div className="field" style={{ flex: 2 }}><label>{tr('Mô tả')}</label><input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder={tr('Tùy chọn')} maxLength={100} /></div>
      <div className="field"><label>{tr('Màu')}</label>
        <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
          <input value={form.color} onChange={(e) => setForm({ ...form, color: e.target.value.replace('#', '') })} maxLength={6} style={{ width: 90, fontFamily: 'monospace' }} />
          <button type="button" className="btn btn-secondary" onClick={() => setForm({ ...form, color: PALETTE[Math.floor(Math.random() * PALETTE.length)] })}>🎲</button>
        </div>
      </div>
      <div style={{ display: 'flex', gap: 4, marginBottom: 4 }}>
        <LabelChip label={{ name: form.name || tr('Xem trước'), colorHex: /^[0-9a-fA-F]{6}$/.test(form.color) ? form.color : 'ededed', description: null }} />
      </div>
      <div style={{ display: 'flex', gap: 8 }}>
        <button type="button" className="secondary" onClick={() => { setCreating(false); setEditing(null); }}>{tr('Hủy')}</button>
        <button type="button" className="primary" disabled={!form.name.trim() || !/^[0-9a-fA-F]{6}$/.test(form.color)} onClick={save}>{editing ? tr('Lưu') : tr('Tạo label')}</button>
      </div>
    </div>
  );

  const milestoneFormEl = (
    <div className="card" style={{ marginBottom: 'var(--space-3)' }}>
      <div className="field"><label>{tr('Tiêu đề')}</label><input value={milestoneForm.title} onChange={(e) => setMilestoneForm({ ...milestoneForm, title: e.target.value })} maxLength={200} /></div>
      <div className="field"><label>{tr('Hạn (tùy chọn)')}</label><input type="date" value={milestoneForm.dueOn} onChange={(e) => setMilestoneForm({ ...milestoneForm, dueOn: e.target.value })} /></div>
      <div className="field"><label>{tr('Mô tả')}</label><textarea rows={3} value={milestoneForm.description} onChange={(e) => setMilestoneForm({ ...milestoneForm, description: e.target.value })} /></div>
      <div style={{ display: 'flex', gap: 'var(--space-2)', justifyContent: 'flex-end' }}>
        <button type="button" className="btn btn-secondary" onClick={() => setMilestoneEditing(null)}>{tr('Hủy')}</button>
        <button type="button" className="btn btn-primary" disabled={!milestoneForm.title.trim()} onClick={saveMilestone}>
          {milestoneEditing === 'new' ? tr('Tạo milestone') : tr('Lưu')}
        </button>
      </div>
    </div>
  );

  return (
    <>
      <PageHead
        kicker={project}
        title="Labels & milestones"
        actions={canWrite ? (
          <>
            <button type="button" className="btn btn-secondary" onClick={startCreate}>New label</button>
            {canWriteMilestone && <button type="button" className="btn btn-primary" onClick={startCreateMilestone}>New milestone</button>}
          </>
        ) : undefined}
      />
      {error ? <ErrorBox error={error} /> : null}
      {creating && formEl}

      {/* Bản mẫu xếp hai cột: danh sách label bên trái, milestone bên phải. */}
      <div className="split-2">
        <div>
          <h5 style={{ margin: '0 0 var(--space-2)' }}>{visibleLabels.length} labels</h5>

          {/* Hàng lọc cân với hàng Open/Closed của milestone bên phải, và trả lời câu hỏi hay gặp
              nhất về label: cái nào có sẵn từ đầu, cái nào do người trong project thêm vào. */}
          <div className="inbox-tabs">
            <label className={`check ${labelKinds.builtin ? 'on' : ''}`}>
              <input type="checkbox" checked={labelKinds.builtin} onChange={(e) => setLabelKinds({ ...labelKinds, builtin: e.target.checked })} />
              {tr('Mặc định')}
            </label>
            <label className={`check ${labelKinds.added ? 'on' : ''}`}>
              <input type="checkbox" checked={labelKinds.added} onChange={(e) => setLabelKinds({ ...labelKinds, added: e.target.checked })} />
              {tr('Được thêm vào')}
            </label>
            <span className="hint">{noKindPicked ? tr('Đang xem tất cả') : tr('{v0}/{v1} label', { v0: visibleLabels.length, v1: labels.length })}</span>
          </div>

          <div>
            {visibleLabels.map((l) => (
              editing === l.name ? <div key={l.id}>{formEl}</div> : (
                <div key={l.id} className="list-row">
                  <LabelChip label={l} href={`/projects/${project}/issues?q=${encodeURIComponent(`is:open label:"${l.name}"`)}`} />
                  <span className="desc">{l.description}{l.isDefault && tr(' · mặc định')}</span>
                  {l.isArchived && <span className="count">{tr('đã lưu trữ')}</span>}
                  {canWrite && (
                    <>
                      <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={() => startEdit(l)}>Edit</button>
                      <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={() => { if (confirm(tr('Xóa label "{v0}"? Label sẽ bị gỡ khỏi mọi ticket.', { v0: l.name }))) tickets.deleteLabel(project, l.name).then(load).catch(setError); }}>{tr('Xóa')}</button>
                    </>
                  )}
                </div>
              )
            ))}
          </div>
        </div>

        <div>
          <h5 style={{ margin: '0 0 var(--space-2)' }}>
            {tr('{v0} milestone', { v0: milestones.length })}
          </h5>

          {/* Bộ lọc ngay trên khối milestone: trước đây phải sang trang riêng mới xem được
              milestone đã đóng. */}
          <div className="inbox-tabs">
            <label className={`check ${milestoneStates.open ? 'on' : ''}`}>
              <input type="checkbox" checked={milestoneStates.open} onChange={(e) => setMilestoneStates({ ...milestoneStates, open: e.target.checked })} />
              Open
            </label>
            <label className={`check ${milestoneStates.closed ? 'on' : ''}`}>
              <input type="checkbox" checked={milestoneStates.closed} onChange={(e) => setMilestoneStates({ ...milestoneStates, closed: e.target.checked })} />
              Closed
            </label>
            <span className="hint">{milestoneQuery === 'all' ? tr('Đang xem tất cả') : tr('Tiến độ = ticket đã đóng / tổng số')}</span>
          </div>

          {milestoneEditing && milestoneFormEl}

          <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
            {milestones.length === 0 && (
              <span className="muted" style={{ fontSize: 13 }}>
                {milestoneQuery === 'open'
                  ? tr('Chưa có milestone nào đang mở.')
                  : milestoneQuery === 'closed'
                    ? tr('Chưa có milestone nào đã đóng.')
                    : tr('Chưa có milestone nào.')}
              </span>
            )}
            {milestones.map((m) => {
              const total = m.openCount + m.closedCount;
              const pct = total > 0 ? Math.round((m.closedCount * 100) / total) : 0;
              const overdue = m.dueOn !== null && m.state === 'OPEN' && new Date(m.dueOn) < new Date();
              return (
                <div key={m.id} className="ms-row">
                  <div className="head">
                    <Link href={`/projects/${project}/issues?q=${encodeURIComponent(`is:open milestone:"${m.title}"`)}`}><strong>{m.title}</strong></Link>
                    <span style={{ fontSize: 12, color: overdue ? 'var(--color-accent)' : 'color-mix(in srgb, var(--color-text) 55%, transparent)' }}>
                      {m.dueOn ? tr('{v0}{v1}', { v0: overdue ? tr('quá hạn ') : 'due ', v1: m.dueOn }) : 'no due date'}
                    </span>
                    {canWriteMilestone && (
                      <span style={{ marginLeft: 'auto', display: 'flex', gap: 'var(--space-1)' }}>
                        <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={() => startEditMilestone(m)}>Edit</button>
                        <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={() => tickets.updateMilestone(project, m.number, { state: m.state === 'OPEN' ? 'closed' : 'open' }).then(loadMilestones).catch(setError)}>{m.state === 'OPEN' ? tr('Đóng') : tr('Mở lại')}</button>
                        <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={() => { if (confirm(tr('Xóa milestone? Ticket sẽ được gỡ khỏi milestone.'))) tickets.deleteMilestone(project, m.number).then(loadMilestones).catch(setError); }}>{tr('Xóa')}</button>
                      </span>
                    )}
                  </div>
                  <div className="progress"><span style={{ width: `${pct}%` }} /></div>
                  <div className="stats">{pct}% complete · {m.closedCount} closed · {m.openCount} open{m.description ? ` · ${m.description}` : ''}</div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </>
  );
}

export default function LabelsPage() { return <Guard permission="ticket.read"><Labels /></Guard>; }
