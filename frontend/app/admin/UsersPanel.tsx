'use client';

import { useCallback, useEffect, useState } from 'react';
import { api, formatTime, session, type AppUser, type Paged, type Role } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ActionFeedback, ErrorBox, Pager, PasswordInput, Refreshing, RequiredMark, Select, SubmitButton } from '@/components/ui';
import { tr } from '@/lib/i18n';

/** UC-01 · UC-04 — tạo tài khoản, bật/tắt hoạt động và gán role. */
export function UsersPanel() {
  const [data, setData] = useState<Paged<AppUser> | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  // latest: gõ tìm kiếm nhanh thì kết quả của từ khóa mới nhất thắng, không bị nuốt.
  const load = useAction({ latest: true });
  const action = useAction();
  const [creating, setCreating] = useState(false);

  // Debounce 300ms để không bắn một request cho mỗi phím gõ.
  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(searchInput);
      setPage(1);
    }, 300);
    return () => clearTimeout(timer);
  }, [searchInput]);

  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [initialRoles, setInitialRoles] = useState<string[]>([]);

  // Cấp của chính mình = rank cao nhất trong các role đang mang. Suy từ danh mục role đã
  // tải sẵn, không cần thêm endpoint. Không đọc được danh mục thì coi như cấp 0 và mọi nút
  // quản trị tắt — an toàn mặc định, API vẫn là nơi chốt chặn thật.
  const me = session.user();
  const myLevel = roles
    .filter((r) => me?.roles.includes(r.name))
    .reduce((max, r) => Math.max(max, r.rank), 0);

  /** BR-SEC-08 — không thao tác lên chính mình, cũng không lên người ngang hoặc trên cấp. */
  const canManage = (user: AppUser) => user.id !== me?.id && user.level < myLevel;

  const canCreate = session.can('user.create');
  const canUpdate = session.can('user.update');
  const canDelete = session.can('user.delete');
  const canAssignRole = session.can('user.role.assign');
  const canReadRoles = session.can('role.read');

  const runLoad = useCallback(async () => {
    await load.run(async () => {
      const [users, roleList] = await Promise.all([
        api.listUsers({ search: search || undefined, page, pageSize: 20 }),
        canReadRoles ? api.listRoles() : Promise.resolve([] as Role[]),
      ]);
      setData(users);
      setRoles(roleList);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, page, canReadRoles]);

  useEffect(() => {
    void runLoad();
  }, [runLoad]);

  async function run(operation: () => Promise<unknown>, message: string) {
    const ok = await action.run(operation, message);
    if (ok !== undefined) {
      await runLoad();
    }
  }

  async function createUser(event: React.FormEvent) {
    event.preventDefault();
    await run(
      () => api.createUser({ email, displayName, password, roles: initialRoles }),
      tr('Đã tạo tài khoản.'),
    );
    setEmail('');
    setDisplayName('');
    setPassword('');
    setInitialRoles([]);
    setCreating(false);
  }

  return (
    <>
      <ActionFeedback action={action} />
      {load.status === 'failed' && <ErrorBox error={load.error} />}

      {canCreate && (
        <div className="card">
          <div className="page-head" style={{ marginBottom: creating ? 16 : 0 }}>
            <div>
              <h5 style={{ margin: 0 }}>{tr('Tài khoản')}</h5>
              <p className="card-hint" style={{ margin: 0 }}>
                {tr('Mật khẩu tối thiểu 8 ký tự, được băm bằng ASP.NET Core PasswordHasher trước khi lưu.')}
              </p>
            </div>
            <button type="button" className="btn btn-primary" onClick={() => setCreating((v) => !v)}>
              {creating ? tr('Hủy') : tr('Tạo tài khoản')}
            </button>
          </div>

          {creating && (
            <form onSubmit={createUser}>
              <div className="row">
                <div className="field">
                  <label htmlFor="u-email">Email<RequiredMark /></label>
                  <input
                    id="u-email"
                    type="email"
                    required
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                  />
                </div>
                <div className="field">
                  <label htmlFor="u-name">{tr('Tên hiển thị')}<RequiredMark /></label>
                  <input
                    id="u-name"
                    required
                    maxLength={150}
                    value={displayName}
                    onChange={(e) => setDisplayName(e.target.value)}
                  />
                </div>
                <div className="field">
                  <label htmlFor="u-pass">{tr('Mật khẩu')}<RequiredMark /></label>
                  <PasswordInput
                    id="u-pass"
                    required
                    minLength={8}
                    value={password}
                    onChange={setPassword}
                  />
                </div>
              </div>

              {roles.length > 0 && (
                <div className="field" style={{ marginTop: 14 }}>
                  <label>{tr('Role gán ngay khi tạo')}</label>
                  <div className="checks">
                    {roles.map((role) => (
                      <label key={role.id} className="check">
                        <input
                          type="checkbox"
                          checked={initialRoles.includes(role.name)}
                          onChange={(e) =>
                            setInitialRoles((prev) =>
                              e.target.checked
                                ? [...prev, role.name]
                                : prev.filter((n) => n !== role.name),
                            )
                          }
                        />
                        {role.name}
                      </label>
                    ))}
                  </div>
                </div>
              )}

              <SubmitButton action={action} processingLabel={tr('Đang tạo…')} className="btn btn-primary">
                {tr('Tạo')}
              </SubmitButton>
            </form>
          )}
        </div>
      )}

      <div className="list-section">
        <div className="row" style={{ marginBottom: 16 }}>
          <div className="field">
            <label htmlFor="search">{tr('Tìm theo email hoặc tên')}</label>
            <input
              id="search"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
            />
          </div>
        </div>

        {load.isProcessing && !data && <div className="empty">{tr('Đang tải…')}</div>}

        {data && (
          <Refreshing busy={load.isProcessing}>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Email</th>
                    <th>{tr('Tên hiển thị')}</th>
                    <th>Role</th>
                    <th>{tr('Trạng thái')}</th>
                    <th>{tr('Tạo lúc')}</th>
                    <th>{tr('Hành động')}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((user) => (
                    <tr key={user.id}>
                      <td>{user.email}</td>
                      <td>{user.displayName}</td>
                      <td>
                        <div className="chips">
                          {user.roles.map((name) => {
                            const role = roles.find((r) => r.name === name);
                            return (
                              <span key={name} className="chip">
                                {name}
                                {canAssignRole && role && canManage(user) && role.rank <= myLevel && (
                                  <button
                                    type="button"
                                    className="chip-x"
                                    title={tr('Gỡ role {v0}', { v0: name })}
                                    disabled={action.isProcessing}
                                    onClick={() =>
                                      run(
                                        () => api.removeRole(user.id, role.id),
                                        tr('Đã gỡ role "{v0}".', { v0: name }),
                                      )
                                    }
                                  >
                                    ×
                                  </button>
                                )}
                              </span>
                            );
                          })}
                          {user.roles.length === 0 && <span className="muted">{tr('Chưa có role')}</span>}
                          {canAssignRole && canManage(user) && roles.length > 0 && (
                          <Select
                            chip
                            value=""
                            placeholder="+"
                            ariaLabel={tr('Gán role')}
                            disabled={action.isProcessing}
                            onChange={(roleId) => {
                              const role = roles.find((r) => r.id === roleId);
                              if (role) {
                                void run(
                                  () => api.assignRole(user.id, roleId),
                                  tr('Đã gán role "{v0}".', { v0: role.name }),
                                );
                              }
                            }}
                            options={roles
                              .filter((r) => !user.roles.includes(r.name) && r.rank <= myLevel)
                              .map((r) => ({ value: r.id, label: r.name }))}
                          />
                          )}
                        </div>
                      </td>
                      <td>
                        <span className={`badge ${user.isActive ? 'Resolved' : 'sev-Critical'}`}>
                          {user.isActive ? tr('Đang hoạt động') : tr('Đã vô hiệu hóa')}
                        </span>
                      </td>
                      <td className="muted">{formatTime(user.createdAt)}</td>
                      {/* Cột hành động đứng cuối, đúng chỗ mắt tìm nút bấm trong một bảng.
                          Không kèm dòng giải thích vì sao không bấm được: hàng nào thao tác được
                          thì có nút, hàng nào không thì trống — bảng tự nói ra điều đó. */}
                      <td>
                        {(canUpdate || canDelete) && canManage(user) && (
                          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                            {canUpdate && (
                              <button
                                type="button"
                                className="secondary"
                                disabled={action.isProcessing}
                                onClick={() =>
                                  run(
                                    () => api.updateUser(user.id, { isActive: !user.isActive }),
                                    user.isActive
                                      ? tr('Đã vô hiệu hóa tài khoản.')
                                      : tr('Đã kích hoạt lại tài khoản.'),
                                  )
                                }
                              >
                                {user.isActive ? tr('Vô hiệu hóa') : tr('Kích hoạt')}
                              </button>
                            )}
                            {canDelete && (
                              <button
                                type="button"
                                className="danger"
                                disabled={action.isProcessing}
                                onClick={() => {
                                  if (
                                    confirm(
                                      tr('Xóa tài khoản "{v0}"? Chỉ thành công khi chưa phát sinh dữ liệu nghiệp vụ.', { v0: user.email }),
                                    )
                                  ) {
                                    void run(
                                      () => api.deleteUser(user.id),
                                      tr('Đã xóa tài khoản.'),
                                    );
                                  }
                                }}
                              >
                                {tr('Xóa')}
                              </button>
                            )}
                          </div>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager
              page={data.page}
              pageSize={data.pageSize}
              totalCount={data.totalCount}
              onChange={setPage}
            />
          </Refreshing>
        )}
      </div>
    </>
  );
}
