'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useEffect, useState } from 'react';
import { session } from '@/lib/api';
import { tickets, type Project } from '@/lib/tickets';
import { useActiveProject } from '@/lib/projectScope';
import { tr } from '@/lib/i18n';

/**
 * Menubar theo bản vẽ: tab chữ in hoa, nét chân trang màu accent cho tab đang
 * mở, cuộn ngang trên màn hẹp mà không lộ thanh cuộn. Hiện trên mọi màn hình
 * nên các link cấp project bám theo project đang xem.
 */
export function ProjectNav() {
  const pathname = usePathname();
  // Cùng một nguồn với ô chọn project ở thanh trên cùng. Trước đây mỗi bên giữ một bản riêng và
  // chúng lệch nhau ngay khi rời khỏi màn hình thuộc project.
  const slug = useActiveProject() ?? 'support';
  const [project, setProject] = useState<Project | null>(null);
  // `session.token()` đọc localStorage, thứ chỉ có ở trình duyệt. Gọi nó ngay trong thân render
  // thì máy chủ dựng ra "không có menu" còn lần render đầu ở trình duyệt dựng ra "có menu" —
  // hai cây khác nhau ở cùng một lần render, đúng định nghĩa hydration mismatch. Nên lần render
  // đầu luôn trả null ở cả hai phía, chỉ đọc phiên sau khi đã gắn.
  const [mounted, setMounted] = useState(false);
  useEffect(() => { setMounted(true); }, []);

  useEffect(() => {
    if (!session.token()) { setProject(null); return; }
    tickets.project(slug).then(setProject).catch(() => setProject(null));
  }, [slug, pathname]);

  if (!mounted || pathname === '/login' || pathname === '/register' || !session.token()) return null;

  const items = [
    { key: 'issues', href: `/projects/${slug}/issues`, label: 'Issues', count: project?.openTickets, match: (p: string) => p.startsWith(`/projects/${slug}/issues`) },
    ...(session.can('incident.read')
      ? [{ key: 'incidents', href: `/projects/${slug}/incidents`, label: 'Incident', match: (p: string) => p.startsWith(`/projects/${slug}/incidents`) }]
      : []),
    ...(session.can('feedback.read')
      ? [{ key: 'feedbacks', href: `/projects/${slug}/feedbacks`, label: 'Feedback', match: (p: string) => p.startsWith(`/projects/${slug}/feedbacks`) }]
      : []),
    { key: 'board', href: '/boards', label: 'Board', match: (p: string) => p.startsWith('/boards') },
    { key: 'inbox', href: '/notifications', label: 'Inbox', match: (p: string) => p === '/notifications' },
    { key: 'labels', href: `/projects/${slug}/labels`, label: 'Labels', match: (p: string) => p === `/projects/${slug}/labels` },
    // Mở cho mọi tài khoản: ai cũng cần biết mình đang ở trong những dự án nào. Phần thêm mới
    // bám theo vai trò — khách hàng có công tắc tự tham gia, admin có tạo/sửa/xoá.
    { key: 'projects', href: '/projects', label: 'Projects', match: (p: string) => p === '/projects' },
    ...(session.can('ticket.write')
      ? [{ key: 'settings', href: `/projects/${slug}/settings`, label: 'Settings', match: (p: string) => p === `/projects/${slug}/settings` }]
      : []),
    ...(session.can('user.read')
      ? [{ key: 'rbac', href: '/admin', label: 'RBAC', match: (p: string) => p === '/admin' }]
      : []),
    ...(session.can('sla.manage') || session.can('issue_type.manage')
      ? [{ key: 'sla', href: '/admin/tickets', label: 'SLA', match: (p: string) => p === '/admin/tickets' }]
      : []),
  ];

  return (
    <nav className="gh-subnav menubar" aria-label={tr('Điều hướng chính')}>
      <div className="gh-subnav-inner">
        {items.map((i) => (
          <Link key={i.key} href={i.href} aria-current={i.match(pathname) ? 'page' : undefined}>
            {i.label}
            {typeof i.count === 'number' && <span className="count">{i.count}</span>}
          </Link>
        ))}
      </div>
    </nav>
  );
}
