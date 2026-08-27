'use client';

import { useCallback, useEffect, useState } from 'react';
import { api, session, type Permission, type Role } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ActionFeedback, ErrorBox, RequiredMark, Select, SubmitButton } from '@/components/ui';
import { tr } from '@/lib/i18n';

/**
 * UC-02 · UC-03 · UC-05 — đây là màn hình chứng minh GOAL-01: thêm hoặc bớt quyền của một vai
 * trò được thực hiện hoàn toàn bằng dữ liệu, không cần sửa và triển khai lại mã nguồn.
 */
export function RolesPanel() {
  const [roles, setRoles] = useState<Role[]>([]);
  const [permissions, setPermissions] = useState<Permission[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const load = useAction({ latest: true });
  const action = useAction();

  const [roleName, setRoleName] = useState('');
  const [roleDescription, setRoleDescription] = useState('');
  const [roleRank, setRoleRank] = useState(10);
  const [permissionCode, setPermissionCode] = useState('');
  const [permissionDescription, setPermissionDescription] = useState('');
  const [editingDescription, setEditingDescription] = useState<string | null>(null);
  const [editingPermissionId, setEditingPermissionId] = useState<string | null>(null);
  const [editingPermissionDescription, setEditingPermissionDescription] = useState('');

  const canWriteRole = session.can('role.write');
  const canAssign = session.can('role.permission.assign');
  const canReadPermission = session.can('permission.read');
  const canWritePermission = session.can('permission.write');

  const runLoad = useCallback(async () => {
    await load.run(async () => {
      const [roleList, permissionList] = await Promise.all([
        api.listRoles(),
        canReadPermission ? api.listPermissions() : Promise.resolve([] as Permission[]),
      ]);
      setRoles(roleList);
      setPermissions(permissionList);
      setSelected((current) => current ?? roleList[0]?.id ?? null);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [canReadPermission]);

  useEffect(() => {
    void runLoad();
  }, [runLoad]);

  async function run(operation: () => Promise<unknown>, message: string) {
    const ok = await action.run(operation, message);
    if (ok !== undefined) {
      await runLoad();
    }
  }

  const current = roles.find((r) => r.id === selected) ?? null;

  if (load.isProcessing && roles.length === 0) return <div className="empty">{tr('Đang tải…')}</div>;

  return (
    <>
      <ActionFeedback action={action} />
      {load.status === 'failed' && <ErrorBox error={load.error} />}

      <div className="grid-2">
        <div>
          <div className="card">
            <h5 style={{ margin: "0 0 var(--space-3)" }}>{tr('Phân quyền cho vai trò')}</h5>
            <p className="card-hint">
              Tick vào permission để gán hoặc gỡ ngay. Quyền trên API có hiệu lực từ request kế
              tiếp (ADR-004); menu giao diện tự đồng bộ khi điều hướng qua <code>/api/auth/me</code>.
            </p>

            <div className="field">
              <label htmlFor="role-select">{tr('Vai trò')}</label>
              <Select
                id="role-select"
                value={selected ?? ''}
                onChange={(next) => setSelected(next || null)}
                options={roles.map((role) => ({
                  value: role.id,
                  label: tr('{v0} · cấp {v1} ({v2} quyền)', { v0: role.name, v1: role.rank, v2: role.permissions.length }),
                }))}
              />
            </div>

            {current && (
              <>
                {editingDescription === null ? (
                  <p className="muted">
                    {current.description || tr('Chưa có mô tả')}
                    {canWriteRole && (
                      <>
                        {' · '}
                        <a
                          href="#"
                          onClick={(e) => {
                            e.preventDefault();
                            setEditingDescription(current.description ?? '');
                          }}
                        >
                          {tr('sửa mô tả')}
                        </a>
                      </>
                    )}
                  </p>
                ) : (
                  <form
                    className="row"
                    onSubmit={async (event) => {
                      event.preventDefault();
                      await run(
                        () => api.updateRole(current.id, { description: editingDescription }),
                        tr('Đã cập nhật mô tả vai trò.'),
                      );
                      setEditingDescription(null);
                    }}
                  >
                    <div className="field">
                      <label htmlFor="r-edit-desc">{tr('Mô tả vai trò')}</label>
                      <input
                        id="r-edit-desc"
                        maxLength={500}
                        value={editingDescription}
                        onChange={(e) => setEditingDescription(e.target.value)}
                      />
                    </div>
                    <SubmitButton action={action} processingLabel={tr('Đang lưu…')} className="btn btn-primary">
                      {tr('Lưu')}
                    </SubmitButton>
                    <button
                      type="button"
                      className="secondary"
                      onClick={() => setEditingDescription(null)}
                    >
                      {tr('Hủy')}
                    </button>
                  </form>
                )}

                {permissions.length === 0 ? (
                  <div className="empty">
                    {tr('Cần permission')} <code>permission.read</code> {tr('để hiển thị danh mục quyền.')}
                  </div>
                ) : (
                  <div className="perm-grid">
                    {permissions.map((permission) => {
                      const granted = current.permissions.includes(permission.code);
                      return (
                        <label key={permission.id} className="check" title={permission.description ?? ''}>
                          <input
                            type="checkbox"
                            checked={granted}
                            disabled={action.isProcessing || !canAssign}
                            onChange={() =>
                              run(
                                () =>
                                  granted
                                    ? api.removePermission(current.id, permission.id)
                                    : api.assignPermission(current.id, permission.id),
                                granted
                                  ? tr('Đã gỡ "{v0}" khỏi vai trò "{v1}".', { v0: permission.code, v1: current.name })
                                  : tr('Đã gán "{v0}" cho vai trò "{v1}".', { v0: permission.code, v1: current.name }),
                              )
                            }
                          />
                          <code>{permission.code}</code>
                        </label>
                      );
                    })}
                  </div>
                )}

                {canWriteRole && (
                  <div style={{ marginTop: 16 }}>
                    <button
                      type="button"
                      className="danger"
                      disabled={action.isProcessing}
                      onClick={() => {
                        if (confirm(tr('Xóa vai trò "{v0}"?', { v0: current.name }))) {
                          setSelected(null);
                          void run(() => api.deleteRole(current.id), tr('Đã xóa vai trò.'));
                        }
                      }}
                    >
                      {tr('Xóa vai trò này')}
                    </button>
                    <p className="muted" style={{ marginTop: 6 }}>
                      {tr('Không xóa được vai trò khi vẫn còn tài khoản đang mang nó. Gỡ khỏi những tài khoản đó trước.')}
                    </p>
                  </div>
                )}
              </>
            )}
          </div>
        </div>

        <div>
          {canWriteRole && (
            <div className="card">
              <h5 style={{ margin: "0 0 var(--space-3)" }}>{tr('Tạo vai trò')}</h5>
              <form
                onSubmit={async (event) => {
                  event.preventDefault();
                  await run(
                    () => api.createRole({
                      name: roleName,
                      description: roleDescription || undefined,
                      rank: roleRank,
                    }),
                    tr('Đã tạo vai trò.'),
                  );
                  setRoleName('');
                  setRoleDescription('');
                  setRoleRank(10);
                }}
              >
                <div className="field">
                  <label htmlFor="r-name">{tr('Tên vai trò')}<RequiredMark /></label>
                  <input
                    id="r-name"
                    required
                    maxLength={100}
                    placeholder="vd: mitigator"
                    value={roleName}
                    onChange={(e) => setRoleName(e.target.value)}
                  />
                </div>
                <div className="field">
                  <label htmlFor="r-desc">{tr('Mô tả')}</label>
                  <input
                    id="r-desc"
                    maxLength={500}
                    value={roleDescription}
                    onChange={(e) => setRoleDescription(e.target.value)}
                  />
                </div>
                <div className="field">
                  <label htmlFor="r-rank">{tr('Cấp')}<RequiredMark /></label>
                  <input
                    id="r-rank"
                    type="number"
                    min={1}
                    max={999}
                    required
                    value={roleRank}
                    onChange={(e) => setRoleRank(Number(e.target.value))}
                  />
                  <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>
                    {tr('Càng lớn càng cao. Thang dựng sẵn: admin 100 · responder 60 · support 40 · customer 10. Chỉ tạo được vai trò thấp hơn cấp của bạn.')}
                  </div>
                </div>
                <SubmitButton action={action} processingLabel={tr('Đang tạo…')} className="btn btn-primary">
                  {tr('Tạo vai trò')}
                </SubmitButton>
              </form>
            </div>
          )}

          {canWritePermission && (
            <div className="card">
              <h5 style={{ margin: "0 0 var(--space-3)" }}>{tr('Tạo permission')}</h5>
              <p className="card-hint">
                {tr('Bắt buộc theo mẫu')} <code>resource.action</code> {tr('(BR-02) — ràng buộc được kiểm cả ở DTO lẫn check constraint của PostgreSQL.')}
              </p>
              <form
                onSubmit={async (event) => {
                  event.preventDefault();
                  await run(
                    () =>
                      api.createPermission({
                        code: permissionCode,
                        description: permissionDescription || undefined,
                      }),
                    tr('Đã tạo permission.'),
                  );
                  setPermissionCode('');
                  setPermissionDescription('');
                }}
              >
                <div className="field">
                  <label htmlFor="p-code">{tr('Mã permission')}<RequiredMark /></label>
                  <input
                    id="p-code"
                    required
                    pattern="^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$"
                    placeholder="vd: incident.export"
                    value={permissionCode}
                    onChange={(e) => setPermissionCode(e.target.value)}
                  />
                </div>
                <div className="field">
                  <label htmlFor="p-desc">{tr('Mô tả')}</label>
                  <input
                    id="p-desc"
                    maxLength={500}
                    value={permissionDescription}
                    onChange={(e) => setPermissionDescription(e.target.value)}
                  />
                </div>
                <SubmitButton action={action} processingLabel={tr('Đang tạo…')} className="btn btn-primary">
                  {tr('Tạo permission')}
                </SubmitButton>
              </form>
            </div>
          )}

          <div className="card">
            <h5 style={{ margin: "0 0 var(--space-3)" }}>Danh mục permission ({permissions.length})</h5>
            {permissions.length === 0 ? (
              <div className="empty">{tr('Không có quyền đọc danh mục.')}</div>
            ) : (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>{tr('Mã')}</th>
                      <th>{tr('Ý nghĩa')}</th>
                      {canWritePermission && <th>{tr('Hành động')}</th>}
                    </tr>
                  </thead>
                  <tbody>
                    {permissions.map((permission) => (
                      <tr key={permission.id}>
                        <td>
                          <code>{permission.code}</code>
                        </td>
                        <td className="muted">
                          {editingPermissionId === permission.id ? (
                            <form
                              className="row"
                              onSubmit={async (event) => {
                                event.preventDefault();
                                await run(
                                  () =>
                                    api.updatePermission(permission.id, {
                                      description: editingPermissionDescription || undefined,
                                    }),
                                  tr('Đã cập nhật mô tả permission.'),
                                );
                                setEditingPermissionId(null);
                              }}
                            >
                              <input
                                maxLength={500}
                                value={editingPermissionDescription}
                                onChange={(e) => setEditingPermissionDescription(e.target.value)}
                                aria-label={tr('Mô tả {v0}', { v0: permission.code })}
                              />
                              <SubmitButton action={action} processingLabel={tr('Đang lưu…')} className="btn btn-primary">
                                {tr('Lưu')}
                              </SubmitButton>
                              <button
                                type="button"
                                className="secondary"
                                onClick={() => setEditingPermissionId(null)}
                              >
                                {tr('Hủy')}
                              </button>
                            </form>
                          ) : (
                            permission.description ?? '—'
                          )}
                        </td>
                        {canWritePermission && (
                          <td>
                            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                              {editingPermissionId !== permission.id && (
                                <button
                                  type="button"
                                  className="secondary"
                                  disabled={action.isProcessing}
                                  onClick={() => {
                                    setEditingPermissionId(permission.id);
                                    setEditingPermissionDescription(permission.description ?? '');
                                  }}
                                >
                                  {tr('Sửa')}
                                </button>
                              )}
                              <button
                                type="button"
                                className="secondary"
                                disabled={action.isProcessing}
                                onClick={() => {
                                  if (confirm(tr('Xóa permission "{v0}"?', { v0: permission.code }))) {
                                    void run(
                                      () => api.deletePermission(permission.id),
                                      tr('Đã xóa permission.'),
                                    );
                                  }
                                }}
                              >
                                {tr('Xóa')}
                              </button>
                            </div>
                          </td>
                        )}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </div>
      </div>
    </>
  );
}
