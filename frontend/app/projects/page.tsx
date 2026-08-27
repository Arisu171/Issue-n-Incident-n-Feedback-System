'use client';

import { useCallback, useEffect, useState } from 'react';
import { api, session, type CatalogProject } from '@/lib/api';
import { tickets } from '@/lib/tickets';
import { useAction } from '@/lib/useAction';
import { ActionFeedback, ErrorBox, Guard, PageHead, RequiredMark, Toggle } from '@/components/ui';
import { projectAccessChanged } from '@/lib/projectScope';
import { tr } from '@/lib/i18n';

/**
 * Một dòng trong bảng. Gộp hai nguồn vì chúng trả lời hai câu khác nhau và đều cần thấy:
 * `GET /api/projects` là "project tôi vào được", `GET /api/project-catalog` là "project tôi
 * **được phép** tham gia" — khách hàng có thể được phép mà chưa tham gia, và ngược lại nhân viên
 * vào được mà không có gì để tick.
 */
interface Row {
  slug: string;
  name: string;
  description: string | null;
  isArchived: boolean;
  /** Có mặt trong danh mục tự chọn — chỉ khách hàng mới có. */
  joinable: boolean;
  joined: boolean;
}

function merge(access: { slug: string; name: string; description: string | null; isArchived: boolean }[], catalog: CatalogProject[]): Row[] {
  const rows = new Map<string, Row>();
  for (const p of access) {
    rows.set(p.slug, { slug: p.slug, name: p.name, description: p.description, isArchived: p.isArchived, joinable: false, joined: false });
  }
  for (const p of catalog) {
    const existing = rows.get(p.slug);
    rows.set(p.slug, {
      slug: p.slug,
      name: p.name,
      description: p.description,
      isArchived: existing?.isArchived ?? false,
      joinable: true,
      joined: p.joined,
    });
  }
  return [...rows.values()].sort((a, b) => a.name.localeCompare(b.name));
}

/**
 * Tab Projects.
 *
 * Mở cho **mọi tài khoản**: ai cũng cần biết mình đang ở trong những dự án nào. Hai phần thêm
 * bám theo vai trò — khách hàng có công tắc tự tham gia, admin có thêm tạo / sửa / xoá.
 *
 * Công tắc tham gia không cấp thêm quyền cho ai: nó chỉ bật vế thứ hai của một quyền mà quản trị
 * đã mở sẵn cho vai trò khách hàng, nên dự án chưa mở thì không xuất hiện để mà tick.
 */
function ProjectList() {
  const [rows, setRows] = useState<Row[] | null>(null);
  const load = useAction<Row[]>({ latest: true });
  const toggle = useAction<null>();
  const manage = useAction<unknown>();

  const isAdmin = session.user()?.roles.includes('admin') ?? false;

  const [editing, setEditing] = useState<Row | 'new' | null>(null);
  const [form, setForm] = useState({ slug: '', name: '', description: '', isArchived: false });

  const runLoad = useCallback(async () => {
    const result = await load.run(async () => {
      // Admin phải thấy cả dự án đã lưu trữ, nếu không thì không có đường nào bỏ lưu trữ hay xoá.
      const access = await tickets.projects(isAdmin).catch(() => []);
      const catalog = await api.projectCatalog().catch(() => []);
      return merge(access, catalog);
    });
    if (result) setRows(result);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAdmin]);

  useEffect(() => { void runLoad(); }, [runLoad]);

  async function setJoined(row: Row, joined: boolean) {
    const done = await toggle.run(
      () => (joined ? api.joinProject(row.slug) : api.leaveProject(row.slug)),
      joined
        ? tr('Đã tham gia {v0}. Từ giờ bạn gửi được sự cố và phản hồi cho dự án này.', { v0: row.name })
        : tr('Đã rời {v0}. Dữ liệu bạn đã gửi vẫn còn, tham gia lại là thấy lại.', { v0: row.name }),
    );
    if (done !== undefined) {
      await runLoad();
      // Ô chọn project ở thanh trên cùng nằm ngoài cây component này nên không tự biết danh sách
      // vừa đổi — trước đó phải chuyển trang hoặc tải lại mới thấy.
      projectAccessChanged();
    }
  }

  function startCreate() {
    setEditing('new');
    setForm({ slug: '', name: '', description: '', isArchived: false });
  }

  function startEdit(row: Row) {
    setEditing(row);
    setForm({ slug: row.slug, name: row.name, description: row.description ?? '', isArchived: row.isArchived });
  }

  // Tạo và sửa là hai thao tác, mỗi cái một thông báo riêng — gộp vào một lời gọi thì câu báo
  // thành công phải viết dạng điều kiện, và người dùng nhận một câu chung chung cho hai việc khác nhau.
  async function saveProject() {
    if (editing === 'new') {
      const created = await manage.run(
        () => tickets.createProject({
          slug: form.slug.trim().toLowerCase(),
          name: form.name.trim(),
          description: form.description || undefined,
          isArchived: form.isArchived,
        }),
        tr('Đã tạo dự án.'),
      );
      if (created === undefined) return;
    } else {
      const saved = await manage.run(
        () => tickets.updateProject(form.slug, {
          name: form.name.trim(),
          description: form.description,
          isArchived: form.isArchived,
        }),
        tr('Đã lưu thay đổi.'),
      );
      if (saved === undefined) return;
    }

    setEditing(null);
    await runLoad();
    projectAccessChanged();
  }

  async function removeProject(row: Row) {
    // Máy chủ chỉ cho xoá dự án rỗng và trả 409 kèm số liệu nếu còn dữ liệu, nên lời cảnh báo ở
    // đây không phải là hàng rào duy nhất.
    if (!confirm(tr('Xoá dự án "{v0}"? Chỉ dự án rỗng mới xoá được.', { v0: row.name }))) return;
    const done = await manage.run(() => tickets.deleteProject(row.slug), tr('Đã xoá dự án.'));
    if (done !== undefined) {
      await runLoad();
      projectAccessChanged();
    }
  }

  const anyJoinable = rows?.some((r) => r.joinable) ?? false;

  return (
    <>
      <PageHead
        kicker={anyJoinable ? tr('Dịch vụ của tôi') : tr('Dự án của tôi')}
        title="Projects"
        hint={anyJoinable
          ? tr('Chọn những dự án bạn đang dùng dịch vụ. Bạn chỉ gửi được sự cố và phản hồi cho dự án mình đã tham gia.')
          : tr('Những dự án bạn đang tham gia.')}
        actions={isAdmin ? (
          <button type="button" className="btn btn-primary" onClick={startCreate}>{tr('Dự án mới')}</button>
        ) : undefined}
      />

      <ActionFeedback action={toggle} processingLabel={tr('Đang cập nhật…')} />
      <ActionFeedback action={manage} processingLabel={tr('Đang lưu…')} />
      {load.status === 'failed' && <ErrorBox error={load.error} />}

      {editing && (
        <div className="card" style={{ marginBottom: 'var(--space-3)' }}>
          <div className="field">
            <label htmlFor="project-slug">{tr('Mã dự án')}<RequiredMark /></label>
            {/* Mã là khoá trong mọi đường dẫn và mọi khoá tệp đã sinh ra, nên không sửa được sau
                khi tạo — đổi nó là làm hỏng mọi liên kết đang có. */}
            <input
              id="project-slug"
              value={form.slug}
              disabled={editing !== 'new'}
              onChange={(e) => setForm({ ...form, slug: e.target.value })}
              placeholder="payments-core"
              maxLength={60}
            />
          </div>
          <div className="field">
            <label htmlFor="project-name">{tr('Tên dự án')}<RequiredMark /></label>
            <input id="project-name" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} maxLength={200} />
          </div>
          <div className="field">
            <label htmlFor="project-desc">{tr('Mô tả')}</label>
            <textarea id="project-desc" rows={2} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </div>
          {/* Có ở cả hai biểu mẫu: tạo và sửa bày ra cùng một tập lựa chọn, không phải nhớ rằng
              lưu trữ là thứ chỉ đặt được sau khi tạo xong. */}
          <div className="field">
            <label>{tr('Lưu trữ')}</label>
            <Toggle
              checked={form.isArchived}
              ariaLabel={tr('Lưu trữ')}
              onChange={(v) => setForm({ ...form, isArchived: v })}
            />
            <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>
              {tr('Dự án lưu trữ biến khỏi các danh sách nhưng dữ liệu vẫn còn nguyên.')}
            </div>
          </div>
          <div style={{ display: 'flex', gap: 'var(--space-2)', justifyContent: 'flex-end' }}>
            <button type="button" className="btn btn-secondary" onClick={() => setEditing(null)}>{tr('Hủy')}</button>
            <button
              type="button"
              className="btn btn-primary"
              disabled={!form.name.trim() || (editing === 'new' && !form.slug.trim()) || manage.isProcessing}
              onClick={saveProject}
            >
              {editing === 'new' ? tr('Tạo') : tr('Lưu')}
            </button>
          </div>
        </div>
      )}

      <div className="card">
        {load.isProcessing && !rows && <div className="empty">{tr('Đang tải danh sách dự án…')}</div>}

        {rows && rows.length === 0 && (
          <div className="empty">
            {tr('Chưa có dự án nào mở cho bạn. Hãy liên hệ đội hỗ trợ nếu bạn đang dùng một dịch vụ chưa thấy ở đây.')}
          </div>
        )}

        {rows && rows.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>{tr('Mã dự án')}</th>
                  <th>{tr('Tên dự án')}</th>
                  {anyJoinable && <th>{tr('Tham gia')}</th>}
                  {isAdmin && <th>{tr('Hành động')}</th>}
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.slug}>
                    <td className="muted">{row.slug}</td>
                    <td>
                      {row.name}
                      {row.isArchived && <span className="count" style={{ marginLeft: 6 }}>{tr('đã lưu trữ')}</span>}
                    </td>
                    {anyJoinable && (
                      <td>
                        {row.joinable ? (
                          <Toggle
                            checked={row.joined}
                            disabled={toggle.isProcessing}
                            ariaLabel={tr('Tham gia {v0}', { v0: row.name })}
                            onChange={(joined) => setJoined(row, joined)}
                          />
                        ) : <span className="muted">—</span>}
                      </td>
                    )}
                    {isAdmin && (
                      <td style={{ whiteSpace: 'nowrap' }}>
                        <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} onClick={() => startEdit(row)}>Edit</button>
                        <button type="button" className="btn btn-ghost" style={{ fontSize: 12 }} disabled={manage.isProcessing} onClick={() => removeProject(row)}>{tr('Xóa')}</button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  );
}

export default function ProjectsPage() {
  // Không đòi permission: đây là màn hình để **vào** project, nên đòi một quyền trong project nào
  // đó là khoá đúng người cần dùng nó. Máy chủ chỉ trả về phần dữ liệu của chính người gọi.
  return (
    <Guard>
      <ProjectList />
    </Guard>
  );
}
