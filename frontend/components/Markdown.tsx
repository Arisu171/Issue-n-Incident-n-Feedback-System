'use client';

import DOMPurify from 'dompurify';
import { useMemo } from 'react';
import { tr } from '@/lib/i18n';

/**
 * BR-SEC-02 (Architecture v3.1): server đã render + sanitize `body_html`; client lọc lần nữa bằng
 * DOMPurify trước khi đưa vào DOM (double-layer ở đầu ra). Cho phép task list (input checkbox disabled).
 */
export function sanitizeHtml(html: string): string {
  return DOMPurify.sanitize(html, {
    USE_PROFILES: { html: true },
    ADD_ATTR: ['target', 'rel'],
    FORBID_TAGS: ['style', 'script', 'iframe', 'object', 'embed', 'form'],
    FORBID_ATTR: ['onerror', 'onload', 'style'],
  });
}

export function Markdown({ html, className }: { html: string | null | undefined; className?: string }) {
  const safe = useMemo(() => sanitizeHtml(html ?? ''), [html]);
  if (!html) return <p className="muted markdown-empty">{tr('Không có mô tả.')}</p>;
  return <div className={`markdown-body ${className ?? ''}`} dangerouslySetInnerHTML={{ __html: safe }} />;
}
