'use client';

import { useParams } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import { ActionFeedback, Combobox, ErrorBox, Guard, PageHead, RequiredMark, Select } from '@/components/ui';
import { TemplateEditor, draftFrom, toRequest, validateTemplate, type TemplateDraft } from '@/components/TemplateEditor';
import { session } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { tickets, type ContactLink, type Project, type ProjectAccess, type Template, type UserSummary, type Webhook, type WebhookDelivery } from '@/lib/tickets';
import { formatDate } from '@/lib/util';
import { tr } from '@/lib/i18n';

const WEBHOOK_EVENTS = ['issues', 'issue_comment', 'label', 'milestone', 'ping'];

/** Settings của project: General (cờ chính sách), Templates (Issue Forms), Webhooks (+ deliveries). */
function Settings() {
  const { project } = useParams<{ project: string }>();
  const [tab, setTab] = useState<'general' | 'members' | 'templates' | 'webhooks' | 'notifications'>('general');
  return (
    <>
      <PageHead kicker={project} title="Project settings" />
      <div className="settings-nav">
        {(['general', 'members', 'templates', 'webhooks', 'notifications'] as const).map((t) => <button key={t} type="button" className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>{{ general: 'General', members: tr('Thành viên'), templates: 'Issue templates', webhooks: 'Webhooks', notifications: tr('Thông báo') }[t]}</button>)}
      </div>
      {tab === 'general' && <General slug={project} />}
      {tab === 'members' && <Members slug={project} />}
      {tab === 'templates' && <Templates slug={project} />}
      {tab === 'webhooks' && <Webhooks slug={project} />}
      {tab === 'notifications' && <Watch slug={project} />}
    </>
  );
}

function General({ slug }: { slug: string }) {
  const [p, setP] = useState<Project | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const canManage = session.can('project.manage');
  useEffect(() => { tickets.project(slug).then(setP).catch(setError); }, [slug]);
  if (!p) return <div className="empty">{tr('Đang tải…')}</div>;
  async function save(patch: Parameters<typeof tickets.updateProject>[1]) {
    setError(null); setSaved(false);
    try { setP(await tickets.updateProject(slug, patch)); setSaved(true); } catch (e) { setError(e); }
  }
  const flag = (key: 'blankIssuesEnabled' | 'strictClosePolicy' | 'autoReopenOnCustomerComment' | 'customersSeeOnlyOwn', label: string, hint: string) => (
    <label className="radio" style={{ alignItems: 'flex-start' }} title={hint}>
      <input type="checkbox" checked={p![key]} disabled={!canManage} onChange={(e) => save({ [key]: e.target.checked })} />
      <span className="dot" style={{ marginTop: 2 }} />
      <span>{label}</span>
    </label>
  );
  return (
    <>
      {error ? <ErrorBox error={error} /> : null}
      {saved && <div className="alert success">{tr('Đã lưu.')}</div>}
      {/* Bản mẫu chia hai cột: chính sách issue bên trái, contact link bên phải. */}
      <div className="settings-grid">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
          <h5 style={{ margin: 0 }}>Issue policy</h5>
          {flag('blankIssuesEnabled', 'Allow blank issues', tr('Tắt thì người gửi bắt buộc chọn một mẫu, không gửi được ticket trống.'))}
          {flag('strictClosePolicy', tr('Strict close policy — chặn đóng khi còn sub-issue mở'), tr('Mặc định tắt: chỉ hiện cảnh báo. Bật thì không đóng được ticket khi còn việc con chưa xong.'))}
          {flag('autoReopenOnCustomerComment', tr('Tự mở lại khi khách hàng bình luận'), tr('Mặc định tắt: ticket đã đóng vẫn đóng, kể cả khi khách hàng bình luận thêm.'))}
          {flag('customersSeeOnlyOwn', tr('Khách hàng chỉ thấy ticket của mình'), tr('Bật thì mỗi khách hàng chỉ thấy ticket do chính mình gửi.'))}
          <div className="field" style={{ marginTop: 'var(--space-2)' }}><label htmlFor="st-slug">Slug</label><input className="input" id="st-slug" defaultValue={p.slug} disabled /></div>
          <div className="field"><label htmlFor="st-name">Display name</label><input className="input" id="st-name" defaultValue={p.name} disabled={!canManage} onBlur={(e) => e.target.value !== p.name && save({ name: e.target.value })} /></div>
          <div className="field"><label htmlFor="st-desc">{tr('Mô tả')}</label><input className="input" id="st-desc" defaultValue={p.description ?? ''} disabled={!canManage} onBlur={(e) => e.target.value !== (p.description ?? '') && save({ description: e.target.value })} /></div>
          <div className="muted" style={{ fontSize: 12 }}>{p.openTickets} mở · {p.closedTickets} đóng · tạo {formatDate(p.createdAt)}</div>
        </div>

        <ContactLinks
          links={p.contactLinks}
          canManage={canManage}
          onSave={(contactLinks) => save({ contactLinks })}
        />
      </div>
    </>
  );
}

/**
 * Danh sách contact link, sửa được ngay tại chỗ.
 *
 * API nhận **cả mảng** và ghi đè, nên mọi thao tác đều gửi lại toàn bộ danh sách. Bản nháp giữ
 * riêng trong state để người dùng sửa vài dòng rồi mới lưu một lần, thay vì mỗi lần gõ một chữ
 * là một request.
 */
function ContactLinks({
  links, canManage, onSave,
}: {
  links: ContactLink[];
  canManage: boolean;
  onSave: (next: ContactLink[]) => void;
}) {
  const [draft, setDraft] = useState<ContactLink[]>(links);
  const [dirty, setDirty] = useState(false);

  // Nguồn sự thật là dữ liệu từ máy chủ; mỗi lần nó đổi thì bỏ bản nháp cũ đi.
  useEffect(() => { setDraft(links); setDirty(false); }, [links]);

  function update(index: number, patch: Partial<ContactLink>) {
    setDraft((d) => d.map((c, i) => (i === index ? { ...c, ...patch } : c)));
    setDirty(true);
  }

  // Chỉ những dòng có đủ tên và url mới được gửi đi; dòng trống là dòng người dùng đang bỏ dở.
  const usable = draft.filter((c) => c.name.trim() !== '' && c.url.trim() !== '');
  const invalid = usable.filter((c) => !/^https?:\/\/\S+$/i.test(c.url.trim()));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
      <h5 style={{ margin: 0 }}>Contact links</h5>
      <div style={{ fontSize: 12, color: 'color-mix(in srgb, var(--color-text) 60%, transparent)' }}>
        {tr('Hiện ở màn hình chọn template thay cho việc tạo ticket.')}
      </div>

      {draft.length === 0 && (
        <span className="muted" style={{ fontSize: 13 }}>{tr('Chưa có contact link nào.')}</span>
      )}

      {draft.map((c, i) => (
        <div key={i} className="contact-link-row">
          {canManage ? (
            <>
              <div className="field">
                <label htmlFor={`cl-name-${i}`}>{tr('Tên hiển thị')}<RequiredMark /></label>
                <input
                  id={`cl-name-${i}`}
                  className="input"
                  value={c.name}
                  placeholder={tr('Ví dụ: Hỗ trợ qua chat')}
                  onChange={(e) => update(i, { name: e.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor={`cl-url-${i}`}>{tr('Đường dẫn')}<RequiredMark /></label>
                <input
                  id={`cl-url-${i}`}
                  className="input"
                  value={c.url}
                  placeholder="https://chat.example.com"
                  onChange={(e) => update(i, { url: e.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor={`cl-about-${i}`}>{tr('Mô tả ngắn (tùy chọn)')}</label>
                <input
                  id={`cl-about-${i}`}
                  className="input"
                  value={c.about ?? ''}
                  placeholder={tr('Dùng khi nào thì hợp')}
                  onChange={(e) => update(i, { about: e.target.value })}
                />
              </div>
              <button
                type="button"
                className="btn btn-ghost"
                onClick={() => { setDraft((d) => d.filter((_, k) => k !== i)); setDirty(true); }}
              >
                {tr('Xóa')}
              </button>
            </>
          ) : (
            <>
              <strong>{c.name}</strong>
              <span className="muted">{c.url}{c.about ? ` — ${c.about}` : ''}</span>
            </>
          )}
        </div>
      ))}

      {canManage && (
        <>
          <button
            type="button"
            className="btn btn-secondary btn-block"
            onClick={() => { setDraft((d) => [...d, { name: '', url: '', about: '' }]); setDirty(true); }}
          >
            {tr('+ Thêm contact link')}
          </button>

          {invalid.length > 0 && (
            <span className="muted" style={{ fontSize: 12, color: 'var(--color-accent)' }}>
              {tr('Đường dẫn phải bắt đầu bằng http:// hoặc https://')}
            </span>
          )}

          {dirty && (
            <div style={{ display: 'flex', gap: 'var(--space-2)', justifyContent: 'flex-end' }}>
              <button
                type="button"
                className="btn btn-secondary"
                onClick={() => { setDraft(links); setDirty(false); }}
              >
                {tr('Hủy')}
              </button>
              <button
                type="button"
                className="btn btn-primary"
                disabled={invalid.length > 0}
                onClick={() => onSave(usable.map((c) => ({
                  name: c.name.trim(),
                  url: c.url.trim(),
                  ...(c.about?.trim() ? { about: c.about.trim() } : {}),
                })))}
              >
                {tr('Lưu')}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

/**
 * Ai làm ở project này.
 *
 * Hai đường cấp quyền, cố ý tách rời vì chúng trả lời hai câu khác nhau:
 *
 * - **Thành viên** — cấp cho từng người. Dùng cho nhân viên: mỗi người một vai trò ở mỗi project.
 * - **Vai trò với tới** — cấp một lần cho cả vai trò. Dùng cho khách hàng: với hàng nghìn khách
 *   thì cấp từng người là việc không ai làm nổi.
 *
 * Chỉ hiện cho ai có `project.member.manage` — admin và manager của chính project này.
 */
function Members({ slug }: { slug: string }) {
  const [access, setAccess] = useState<ProjectAccess | null>(null);
  const [candidates, setCandidates] = useState<UserSummary[]>([]);
  const [search, setSearch] = useState('');
  const [login, setLogin] = useState('');
  const [role, setRole] = useState('support');
  const act = useAction<unknown>();
  const canManage = session.can('project.member.manage');

  const load = useCallback(
    () => tickets.members(slug).then(setAccess).catch(() => setAccess(null)),
    [slug],
  );
  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    if (!canManage) return;
    tickets.memberCandidates(slug, search || undefined).then(setCandidates).catch(() => setCandidates([]));
  }, [slug, search, canManage]);

  async function run(fn: () => Promise<unknown>, message: string) {
    if (await act.run(fn, message) !== undefined) await load();
  }

  if (!canManage) {
    return <div className="alert info">{tr('Cần permission')} <code>project.member.manage</code>.</div>;
  }
  if (!access) return <div className="empty">{tr('Đang tải…')}</div>;

  return (
    <>
      <ActionFeedback action={act} processingLabel={tr('Đang lưu…')} />

      <h5 style={{ margin: 0 }}>{tr('Thành viên ({v0})', { v0: access.members.length })}</h5>
      <div style={{ fontSize: 12, color: 'color-mix(in srgb, var(--color-text) 60%, transparent)' }}>
        {tr('Nhân viên được cấp quyền theo từng người. Không có tên ở đây thì không vào được project này.')}
      </div>

      {access.members.length === 0 && (
        <div className="empty">{tr('Chưa có ai. Project này hiện chỉ quản trị viên vào được.')}</div>
      )}

      <div className="list-section">
        {access.members.map((m) => (
          <div key={m.userId} className="list-row">
            <strong>{m.displayName}</strong>
            <span className="muted">@{m.login}</span>
            <span className="desc">
              {m.roles.map((r) => <span key={r} className="tag tag-outline" style={{ marginRight: 4 }}>{r}</span>)}
            </span>
            {m.roles.map((r) => (
              <button key={r} type="button" className="btn btn-ghost" style={{ fontSize: 12 }}
                title={tr('Gỡ vai trò "{v0}"', { v0: r })}
                onClick={() => void run(() => tickets.removeMember(slug, m.login, r),
                  tr('Đã gỡ "{v0}" khỏi {v1}.', { v0: r, v1: m.login }))}>
                {tr('Gỡ {v0}', { v0: r })}
              </button>
            ))}
          </div>
        ))}
      </div>

      {/* Trái: hai dòng xếp dọc. Phải: đúng một nút, thẳng hàng với dòng trên. */}
      <div className="settings-grid">
        <div>
          <div className="field">
            <label htmlFor="mem-search">{tr('Thêm người')}</label>
            <Combobox
              id="mem-search"
              text={search}
              onTextChange={(next) => { setSearch(next); setLogin(''); }}
              onChange={setLogin}
              placeholder={tr('Tìm người')}
              ariaLabel={tr('Thêm người')}
              emptyLabel={tr('Không tìm thấy ai')}
              options={candidates.map((u) => ({ value: u.login, label: `${u.displayName} (@${u.login})` }))}
            />
          </div>
          <div className="field" style={{ marginBottom: 0 }}>
            <label htmlFor="mem-role">{tr('Vai trò trong project')}</label>
            <Select id="mem-role" value={role} onChange={setRole} ariaLabel={tr('Vai trò trong project')}
              options={['support', 'responder', 'manager'].map((r) => ({ value: r, label: r }))} />
          </div>
        </div>
        <div className="field">
          {/* Nhãn rỗng nhưng vẫn chiếm chỗ: nút thẳng hàng với ô bên trái mà không phải đoán
              chiều cao của nhãn bằng một con số. */}
          <label className="label-spacer" aria-hidden="true">&nbsp;</label>
          <button type="button" className="btn btn-primary" disabled={!login || act.isProcessing}
            onClick={() => void run(() => tickets.addMember(slug, login, role),
              tr('Đã thêm {v0} với vai trò {v1}.', { v0: login, v1: role }))}>
            {tr('Thêm vào project')}
          </button>
        </div>
      </div>

      <h5 style={{ margin: 'var(--space-4) 0 0' }}>{tr('Vai trò được cấp cho cả nhóm')}</h5>
      <div style={{ fontSize: 12, color: 'color-mix(in srgb, var(--color-text) 60%, transparent)' }}>
        {tr('Dùng để quyết định Khách hàng có thể truy cập vào dụ án này không')}
      </div>
      <label className="radio">
        <input type="checkbox" checked={access.roleAccess.includes('customer')}
          onChange={(e) => void run(() => tickets.setRoleAccess(slug, 'customer', e.target.checked),
            e.target.checked ? tr('Đã mở cho khách hàng.') : tr('Đã đóng với khách hàng.'))} />
        <span className="dot" />
        {tr('Khách hàng gửi và theo dõi ticket ở project này')}
      </label>
    </>
  );
}

function Templates({ slug }: { slug: string }) {
  const [list, setList] = useState<Template[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [editing, setEditing] = useState<{ id?: string; draft: TemplateDraft } | null>(null);
  // API tạo/sửa/xóa template đòi `ticket.write` (TemplateService.cs:270), không phải
  // `project.manage`. Khoá theo `project.manage` thì responder — nhóm đúng ra được sửa mẫu — chỉ
  // xem được mà không sửa được.
  const canManage = session.can('ticket.write');
  const load = useCallback(() => tickets.templates(slug, true).then(setList).catch(setError), [slug]);
  useEffect(() => { void load(); }, [load]);

  async function save() {
    if (!editing) return;
    setError(null);
    try {
      const body = toRequest(editing.draft);
      if (editing.id) await tickets.updateTemplate(slug, editing.id, body);
      else await tickets.createTemplate(slug, body);
      setEditing(null);
      await load();
    } catch (e) { setError(e); }
  }

  const problems = editing ? validateTemplate(editing.draft.name, editing.draft.body) : [];

  return (
    <>
      {error ? <ErrorBox error={error} /> : null}

      <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-3)', flexWrap: 'wrap' }}>
        <h5 style={{ margin: 0 }}>{tr('{v0} template', { v0: list.length })}</h5>
        {canManage && !editing && (
          <button type="button" className="btn btn-primary" style={{ marginLeft: 'auto' }}
            onClick={() => setEditing({ draft: draftFrom() })}>
            {tr('Template mới')}
          </button>
        )}
      </div>

      {list.length === 0 && !editing && (
        <div className="empty">{tr('Chưa có template nào. Người dùng sẽ mở ticket trắng.')}</div>
      )}

      <div className="list-section">
        {list.map((t) => (
          <div key={t.id} className="list-row">
            <strong>{t.name}</strong>
            {!t.isEnabled && <span className="tag tag-neutral">{tr('tắt')}</span>}
            <span className="desc">
              {t.description}
              {t.description ? ' · ' : ''}
              {tr('{v0} phần tử', { v0: t.body.length })}
              {t.titlePrefix ? tr(' · tiền tố “{v0}”', { v0: t.titlePrefix }) : ''}
            </span>
            {canManage && (
              <>
                <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }}
                  onClick={() => setEditing({ id: t.id, draft: draftFrom(t) })}>{tr('Sửa')}</button>
                <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }}
                  onClick={() => { if (confirm(tr('Xóa template?'))) tickets.deleteTemplate(slug, t.id).then(load).catch(setError); }}>{tr('Xóa')}</button>
              </>
            )}
          </div>
        ))}
      </div>

      {editing && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-3)', borderTop: '1px solid var(--color-divider)', paddingTop: 'var(--space-4)', marginTop: 'var(--space-4)' }}>
          <h5 style={{ margin: 0 }}>{editing.id ? tr('Sửa template') : tr('Template mới')}</h5>
          <TemplateEditor draft={editing.draft} onChange={(draft) => setEditing({ ...editing, draft })} />
          <div className="form-actions">
            <button type="button" className="btn btn-secondary" onClick={() => setEditing(null)}>{tr('Hủy')}</button>
            <button type="button" className="btn btn-primary" disabled={problems.length > 0} onClick={save}>
              {editing.id ? tr('Lưu') : tr('Tạo')}
            </button>
          </div>
        </div>
      )}
    </>
  );
}

function Webhooks({ slug }: { slug: string }) {
  const [list, setList] = useState<Webhook[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [form, setForm] = useState({ targetUrl: '', secret: '', events: ['issues', 'issue_comment'] as string[] });
  const [created, setCreated] = useState<Webhook | null>(null);
  const [deliveries, setDeliveries] = useState<{ id: string; items: WebhookDelivery[] } | null>(null);
  const canManage = session.can('webhook.manage');
  const load = useCallback(() => tickets.webhooks(slug).then(setList).catch(setError), [slug]);
  useEffect(() => { void load(); }, [load]);

  async function create() {
    setError(null);
    try { const w = await tickets.createWebhook(slug, { targetUrl: form.targetUrl, events: form.events, secret: form.secret || undefined }); setCreated(w); setForm({ targetUrl: '', secret: '', events: ['issues', 'issue_comment'] }); await load(); } catch (e) { setError(e); }
  }
  async function showDeliveries(id: string) { try { setDeliveries({ id, items: await tickets.deliveries(slug, id) }); } catch (e) { setError(e); } }

  if (!canManage) return <div className="alert info">{tr('Cần permission')} <code>webhook.manage</code>.</div>;
  return (
    <>
      {error ? <ErrorBox error={error} /> : null}
      {created?.secret && <div className="alert success">{tr('Webhook đã tạo. Secret chỉ hiện một lần:')} <code>{created.secret}</code> {tr('— dùng để kiểm tra chữ ký')} <code>X-Hub-Signature-256</code>.</div>}
      <div className="card">
        <div className="field"><label>Payload URL</label><input value={form.targetUrl} onChange={(e) => setForm({ ...form, targetUrl: e.target.value })} placeholder="https://example.com/hooks" /></div>
        <div className="field"><label>{tr('Secret (tùy chọn, để trống = tự sinh)')}</label><input value={form.secret} onChange={(e) => setForm({ ...form, secret: e.target.value })} /></div>
        <div className="field"><label>{tr('Sự kiện')}</label>
          <div className="chips">{WEBHOOK_EVENTS.map((ev) => (
            <label key={ev} className="chip-check">
              <input type="checkbox" checked={form.events.includes(ev)}
                onChange={(e) => setForm({ ...form, events: e.target.checked ? [...form.events, ev] : form.events.filter((x) => x !== ev) })} />
              {ev}
            </label>
          ))}</div>
        </div>
        <button type="button" className="primary" disabled={!form.targetUrl} onClick={create}>Add webhook</button>
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-3)', flexWrap: 'wrap', marginTop: 'var(--space-4)' }}>
        <h5 style={{ margin: 0 }}>Webhooks</h5>
        <span className="muted" style={{ fontSize: 12 }}>{list.length} webhook</span>
      </div>
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>Target URL</th><th>Events</th><th>{tr('Trạng thái')}</th><th>{tr('Lỗi liên tiếp')}</th><th>{tr('Hành động')}</th></tr></thead>
          <tbody>
            {list.map((w) => (
              <tr key={w.id}>
                <td><code>{w.targetUrl}</code></td>
                <td><span className="chips">{w.events.map((ev) => <span key={ev} className="chip">{ev}</span>)}</span></td>
                <td style={w.isActive ? undefined : { color: 'var(--color-accent-700)' }}>{w.isActive ? 'active' : tr('tắt')}</td>
                <td>{w.consecutiveFailures}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <span className="cell-link" onClick={() => tickets.ping(slug, w.id).then(() => showDeliveries(w.id)).catch(setError)}>Ping</span>{' · '}
                  <span className="cell-link" onClick={() => showDeliveries(w.id)}>Deliveries</span>{' · '}
                  <span className="cell-link" onClick={() => tickets.updateWebhook(slug, w.id, { targetUrl: w.targetUrl, events: w.events, isActive: !w.isActive }).then(load).catch(setError)}>{w.isActive ? tr('Tắt') : tr('Bật')}</span>{' · '}
                  <span className="cell-link" onClick={() => { if (confirm(tr('Xóa webhook?'))) tickets.deleteWebhook(slug, w.id).then(load).catch(setError); }}>{tr('Xóa')}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div style={{ fontSize: 12, color: 'color-mix(in srgb, var(--color-text) 60%, transparent)', marginTop: 'var(--space-2)' }}>
        {tr('Ký bằng')} <code>X-Hub-Signature-256</code>{tr(' · hết giờ sau 10 giây · thử lại sau 1m / 5m / 30m / 2h / 6h, rồi chuyển sang hàng đợi lỗi.')}
      </div>
      {deliveries && (
        <div className="gh-table" style={{ marginTop: 16 }}>
          <div className="gh-table-head"><span>Recent deliveries</span><button type="button" className="ghost small" onClick={() => setDeliveries(null)}>{tr('Đóng')}</button></div>
          {deliveries.items.length === 0 && <div className="empty">{tr('Chưa có delivery.')}</div>}
          {deliveries.items.map((d) => (
            <details key={d.id} className="delivery">
              <summary>
                <span className={d.httpStatus && d.httpStatus < 300 ? 'status-ok' : 'status-bad'}>{d.httpStatus ?? '—'}</span>
                <code>{d.event}.{d.action}</code>
                <span className="muted">{formatDate(d.createdAt)} · lần {d.attempt}{d.isRedelivery ? ' (redelivery)' : ''}{d.durationMs !== null ? ` · ${d.durationMs}ms` : ''}</span>
                {d.error && <span className="status-bad">{d.error}</span>}
                <span style={{ flex: 1 }} />
                <button type="button" className="btn btn-ghost" onClick={(e) => { e.preventDefault(); tickets.redeliver(slug, deliveries.id, d.id).then(() => showDeliveries(deliveries.id)).catch(setError); }}>Redeliver</button>
              </summary>
              <strong>Request</strong><pre>{d.requestBody}</pre>
              <strong>Response</strong><pre>{d.responseBody ?? tr('(trống)')}</pre>
            </details>
          ))}
        </div>
      )}
    </>
  );
}

function Watch({ slug }: { slug: string }) {
  const [level, setLevel] = useState<string>('PARTICIPATING');
  const [error, setError] = useState<unknown>(null);
  useEffect(() => { tickets.projectWatch(slug).then((w) => setLevel(w.level)).catch(setError); }, [slug]);
  return (
    <div className="card">
      {error ? <ErrorBox error={error} /> : null}
      <h5 style={{ margin: '0 0 var(--space-3)' }}>{tr('Theo dõi project (Watch)')}</h5>
      {([['PARTICIPATING', 'Participating & @mentions', tr('Chỉ nhận thông báo khi bạn tham gia hoặc được nhắc.')], ['ALL', 'All activity', tr('Nhận thông báo cho mọi ticket mới trong project.')], ['IGNORE', 'Ignore', tr('Không bao giờ nhận thông báo từ project này.')]] as const).map(([v, l, h]) => (
        <label key={v} className="radio" style={{ alignItems: 'flex-start', marginBottom: 8 }}>
          <input type="radio" checked={level === v} onChange={() => tickets.setProjectWatch(slug, v).then((r) => setLevel(r.level)).catch(setError)} />
          <span className="dot" style={{ marginTop: 2 }} />
          <span><strong>{l}</strong><div className="muted" style={{ fontSize: 12 }}>{h}</div></span>
        </label>
      ))}
    </div>
  );
}

export default function SettingsPage() { return <Guard permission="ticket.write"><Settings /></Guard>; }
