'use client';

import { useRef, useState } from 'react';
import { API_BASE, session } from '@/lib/api';
import { tickets } from '@/lib/tickets';
import { Markdown } from '@/components/Markdown';
import { tr } from '@/lib/i18n';

/**
 * Ô soạn Markdown kiểu GitHub: tab Write/Preview, kéo-thả/dán tệp → presigned upload (BR-EV-05)
 * rồi chèn link Markdown, phím tắt Ctrl+Enter để gửi.
 */
export function MarkdownEditor({
  value, onChange, placeholder = tr('Nhập nội dung (Markdown)…'), project = 'support', onSubmit, minRows = 5, disabled,
}: {
  value: string; onChange: (v: string) => void; placeholder?: string; project?: string; onSubmit?: () => void; minRows?: number; disabled?: boolean;
}) {
  const [tab, setTab] = useState<'write' | 'preview'>('write');
  const [preview, setPreview] = useState<string>('');
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLTextAreaElement>(null);

  async function showPreview() {
    setTab('preview');
    try {
      const response = await fetch(`${API_BASE}/api/markdown/preview`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${session.token() ?? ''}` },
        body: JSON.stringify({ text: value, project }),
      });
      const payload = await response.json();
      setPreview(payload.html ?? '');
    } catch {
      setPreview(tr('<p><em>Không tải được bản xem trước.</em></p>'));
    }
  }

  async function uploadFiles(files: FileList | File[]) {
    setError(null);
    setUploading(true);
    try {
      for (const file of Array.from(files)) {
        const { url } = await tickets.upload(file, project);
        const isImage = file.type.startsWith('image/');
        const snippet = `${isImage ? '!' : ''}[${file.name}](${url})`;
        insert(snippet);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setUploading(false);
    }
  }

  function insert(snippet: string) {
    const el = ref.current;
    if (!el) { onChange(value + '\n' + snippet); return; }
    const start = el.selectionStart ?? value.length;
    const end = el.selectionEnd ?? value.length;
    const next = value.slice(0, start) + snippet + value.slice(end);
    onChange(next);
  }

  return (
    <div className="editor" onDrop={(e) => { if (e.dataTransfer.files.length) { e.preventDefault(); void uploadFiles(e.dataTransfer.files); } }} onDragOver={(e) => e.preventDefault()}>
      <div className="editor-tabs">
        <button type="button" className={tab === 'write' ? 'active' : ''} onClick={() => setTab('write')}>Write</button>
        <button type="button" className={tab === 'preview' ? 'active' : ''} onClick={showPreview}>Preview</button>
        {/* Bản mẫu để dòng gợi ý cú pháp ở mép phải hàng tab. Ở đây nó kiêm luôn nút chọn tệp
            để giữ tính năng đính kèm (BR-EV-05) mà không thêm một hàng chân ngoài bản vẽ. */}
        <label className="hint" title={tr('Đính kèm bằng kéo-thả, dán hoặc bấm để chọn tệp')}>
          {uploading ? tr('Đang tải tệp…') : 'Markdown · @mention · #ref'}
          <input type="file" multiple style={{ display: 'none' }} onChange={(e) => e.target.files && uploadFiles(e.target.files)} />
        </label>
      </div>
      {tab === 'write' ? (
        <textarea
          ref={ref}
          className="input"
          rows={minRows}
          value={value}
          placeholder={placeholder}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
          onPaste={(e) => { if (e.clipboardData.files.length) { e.preventDefault(); void uploadFiles(e.clipboardData.files); } }}
          onKeyDown={(e) => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && onSubmit) { e.preventDefault(); onSubmit(); } }}
        />
      ) : (
        <div className="preview"><Markdown html={preview || tr('<p><em>Không có gì để xem trước.</em></p>')} /></div>
      )}
      {error && <div className="alert error">{error}</div>}
    </div>
  );
}
