'use client';
import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState } from 'react';
import { api, session, type CurrentUser } from '@/lib/api';
import { tickets, type Project } from '@/lib/tickets';
import { useNotificationRealtime, disconnectRealtime } from '@/lib/realtime';
import { UNCATEGORIZED, UNCATEGORIZED_SECTIONS, activeProject, isUncategorized, defaultSection, projectFromPath, resolveActiveProject, setActiveProject, supportsUncategorized, useActiveProject, useProjectAccessChanged } from '@/lib/projectScope';
import { ThemeToggle } from '@/components/ThemeToggle';
import { LocaleToggle } from '@/components/LocaleToggle';
import { Avatar, Icons } from '@/components/tickets/Bits';
import { Select } from '@/components/ui';
import { useDismiss } from '@/lib/useDismiss';
import { useSessionKeepAlive } from '@/lib/useSessionKeepAlive';
import { QueryInput } from '@/components/QueryInput';
import { tr } from '@/lib/i18n';

/**
 * Header theo bản export "X.OS — Ticket System": ba nhóm trên một hàng không xuống dòng.
 * Trái là wordmark + bộ chọn project, giữa là ô Query DSL rộng tối đa 520px, phải là chủ đề,
 * chuông thông báo có badge đếm real-time và avatar. Mục hiển thị theo permission; API vẫn
 * kiểm quyền độc lập.
 */
export function TopBar() {
  const pathname = usePathname();
  const router = useRouter();
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [unread, setUnread] = useState(0);
  const [menu, setMenu] = useState(false);
  const [query, setQuery] = useState('');
  const menuRef = useRef<HTMLSpanElement>(null);
  useDismiss(menuRef, useCallback(() => setMenu(false), []), menu);

  // Project đang chọn hiển thị **xuyên suốt**, kể cả trên màn hình không thuộc project nào
  // (Search, Inbox, Boards, Projects). `routeProject` là chuyện khác: nó trả lời "trang này có
  // nằm trong một project không", và những hành vi phụ thuộc vị trí vẫn phải hỏi nó.
  const projectSlug = useActiveProject();
  const routeProject = projectFromPath(pathname);

  /**
   * `uncategorized` là project **ảo**: nó không có hàng nào trong bảng `projects` nên không bao
   * giờ xuất hiện trong `GET /api/projects`. Nó phải được ghép vào đây thì mới có đường vào mục
   * chưa phân loại — trước đó chỉ gõ tay URL mới tới được.
   *
   * Điều kiện hiện dòng này lặp lại đúng `VirtualProject.CanSee` ở server. Ghép thừa thì người
   * dùng bấm vào và nhận 403; ghép thiếu thì người có quyền không có đường vào.
   */
  const canClassify = (user?.permissions.includes('project.member.manage')) ?? false;
  const projectOptions = [
    ...projects.map((p) => ({ value: p.slug, label: p.slug })),
    ...(canClassify ? [{ value: UNCATEGORIZED, label: tr('chưa phân loại') }] : []),
  ];

  const loadProjects = useCallback(async () => {
    if (!session.token()) return;
    try {
      const list = await tickets.projects();
      setProjects(list);
      // Vừa mất quyền vào project đang chọn (khách tự rời) thì phải bỏ chọn, nếu không ô chọn
      // giữ một slug không còn trong danh sách và hàng tab bên dưới trỏ vào chỗ trả 403.
      //
      // Quy tắc nằm trong `resolveActiveProject` chứ không viết thẳng ở đây: project ảo cố ý
      // không có trong danh sách trả về, nên phép kiểm ngây thơ "còn trong danh sách không" đá
      // luôn cả nó — xem chú thích của hàm đó.
      const active = activeProject();
      const next = resolveActiveProject(
        active,
        list.map((p) => p.slug),
        session.can('project.member.manage'),
      );
      if (next !== active) setActiveProject(next);
    } catch {
      /* chưa có quyền đọc danh sách project */
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    async function refresh() {
      if (!session.token()) { setUser(null); return; }
      setUser(session.user());
      try { const fresh = await api.me(); if (!cancelled) setUser(fresh); } catch { if (!cancelled) setUser(session.user()); }
      if (!cancelled) await loadProjects();
      try { const c = await tickets.unreadCount(); if (!cancelled) setUnread(c.unreadCount); } catch { /* bỏ qua */ }
    }
    void refresh();
    setMenu(false);
    return () => { cancelled = true; };
  }, [pathname, loadProjects]);

  // Khách hàng bật/tắt một dự án ở tab Projects: cập nhật ngay, không chờ chuyển trang.
  useProjectAccessChanged(loadProjects);

  useNotificationRealtime((n) => setUnread(n));
  useSessionKeepAlive();

  if (pathname === '/login' || pathname === '/register') return null;

  const can = (p: string) => user?.permissions.includes(p) ?? false;

  function signOut() {
    disconnectRealtime();
    session.clear();
    router.push('/login');
  }

  /**
   * Đổi project **không kéo người dùng đi đâu cả**.
   *
   * Đang ở một màn hình thuộc project (Issues, Labels, Settings…) thì ở nguyên loại màn hình đó,
   * chỉ đổi project trong đường dẫn. Đang ở màn hình chung (Search, Inbox, Boards) thì chỉ đổi
   * lựa chọn — đá người ta về Issues giữa lúc đang đọc thông báo là mất chỗ đang xem.
   */
  function selectProject(slug: string) {
    if (!slug) return;
    setActiveProject(slug);

    // Giữ đúng phần "loại màn hình" và bỏ phần định danh phía sau: `/projects/a/issues/5` sang
    // project b thì về `/projects/b/issues`, vì số ticket đánh riêng cho từng project nên #5 ở
    // đó là một ticket hoàn toàn khác.
    const section = pathname.match(/^\/projects\/[^/]+\/([^/]+)/)?.[1];
    if (!routeProject || !section) return;

    // Mục chưa phân loại chỉ có sự cố và phản hồi. Giữ nguyên section khi chuyển sang nó sẽ
    // dẫn tới một màn hình không thể tồn tại, và API trả 404 — đưa về màn hình có thật.
    if (isUncategorized(slug) && !supportsUncategorized(section)) {
      router.push(`/projects/${slug}/${UNCATEGORIZED_SECTIONS[0]}`);
      return;
    }

    router.push(`/projects/${slug}/${section}`);
  }

  function runSearch(next: string) {
    setQuery(next);
    const q = next.trim();
    if (!q) return;
    // Trang Search riêng đã bỏ: mỗi màn hình danh sách tự có ô tìm của nó, tìm đúng thứ đang
    // bày ra ở đó. Ô này đưa về danh sách issue của project đang chọn — nơi cú pháp DSL gõ vào
    // đây có nghĩa. Chưa chọn project nào thì không có chỗ để đưa tới.
    //
    // Mục chưa phân loại cũng không có chỗ để đưa tới: cú pháp DSL này tìm trong issue, mà
    // issue thì bắt buộc thuộc một project nên ở đây không có cái nào. Đưa sang đó là dẫn
    // thẳng vào màn hình không tồn tại.
    const target = routeProject ?? projectSlug;
    if (target && !isUncategorized(target)) {
      router.push(`/projects/${target}/issues?q=${encodeURIComponent(q)}`);
    }
  }

  return (
    <header className="gh-header">
      <div className="gh-header-inner">
        <div className="gh-header-left">
          <Link href={projectSlug ? `/projects/${projectSlug}/${defaultSection(projectSlug)}` : '/'} className="gh-logo">X.OS</Link>

          {user && projectOptions.length > 0 && (
            <div className="gh-crumbs">
              <span className="sep">/</span>
              <Select
                inline
                ariaLabel={tr('Chọn project')}
                placeholder={tr('— chọn project —')}
                value={projectSlug ?? ''}
                onChange={selectProject}
                options={projectOptions}
              />
            </div>
          )}
        </div>

        {user && (
          <QueryInput
            className="gh-search"
            value={query}
            onSubmit={runSearch}
            prefix={Icons.search}
            placeholder="is:open label:incident assignee:@me"
            ariaLabel={tr('Tìm kiếm ticket')}
          />
        )}

        <div className="gh-actions">
          <LocaleToggle />
          <ThemeToggle />
          {user && (
            <>
              <span style={{ position: 'relative', display: 'inline-flex' }}>
                <Link href="/notifications" className="btn btn-secondary btn-icon gh-iconbtn" // Hai khoá tách rời, không phải một khoá với phần đuôi ghép vào. Ghép chuỗi thì phần đuôi
                  // là literal tiếng Việt nằm ngoài `tr()`, và bản English cho ra "Notifications (3 chưa đọc)".
                  aria-label={unread > 0 ? tr('Thông báo ({v0} chưa đọc)', { v0: String(unread) }) : tr('Thông báo')}>
                  {Icons.inbox}
                </Link>
                {unread > 0 && <span className="gh-badge">{unread > 99 ? '99+' : unread}</span>}
              </span>

              <span className="gh-menu" ref={menuRef}>
                <button type="button" className="btn gh-iconbtn" style={{ padding: 0, border: 0 }} onClick={() => setMenu((v) => !v)} aria-label={tr('Menu người dùng')} aria-expanded={menu}>
                  <Avatar user={{ id: user.id, login: user.login, displayName: user.displayName }} link={false} />
                </button>
                {menu && (
                  <div className="gh-dropdown">
                    <div className="head">{user.displayName} · {user.roles.join(', ') || tr('chưa có role')}</div>
                    <div className="divider" />
                    <Link href={`/profiles/${user.login}`}>Account</Link>
                    <Link href="/notifications">{tr('Thông báo')}</Link>
                    <Link href="/boards">Boards</Link>
                    <Link href="/projects">Projects</Link>
                    {projectSlug && !isUncategorized(projectSlug) && can('ticket.write') && <Link href={`/projects/${projectSlug}/settings`}>{tr('Cài đặt project')}</Link>}
                    {can('user.read') && <Link href="/admin">{tr('Quản trị RBAC')}</Link>}
                    {can('issue_type.manage') && <Link href="/admin/tickets">{tr('Quản trị Tickets')}</Link>}
                    <div className="divider" />
                    <div className="head">Release 1</div>
                    {can('incident.read') && projectSlug && <Link href={`/projects/${projectSlug}/incidents`}>Incident</Link>}
                    {can('feedback.read') && projectSlug && <Link href={`/projects/${projectSlug}/feedbacks`}>Feedback</Link>}
                    <div className="divider" />
                    <button type="button" className="item" onClick={signOut}>{tr('Đăng xuất')}</button>
                  </div>
                )}
              </span>
            </>
          )}
        </div>
      </div>
    </header>
  );
}
