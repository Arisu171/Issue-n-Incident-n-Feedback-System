'use client';

import { useState } from 'react';
import { RequiredMark } from '@/components/ui';
import { tr } from '@/lib/i18n';
import type { Template, TemplateElement } from '@/lib/tickets';

/**
 * Trình soạn Issue Template có giao diện, thay cho ô JSON thô.
 *
 * Ba lý do bỏ ô JSON:
 *   1. Người dùng phải tự nhớ schema và tự giữ JSON hợp lệ; sai một dấu phẩy là hỏng cả biểu mẫu.
 *   2. Ô JSON cũ điền sẵn mẫu dùng `titlePrefix` và `defaults`, trong khi API nhận `title`,
 *      `labels`, `type`. System.Text.Json bỏ qua trường lạ, nên template tạo ra **im lặng** mất
 *      tiền tố tiêu đề và nhãn mặc định. Dựng theo cấu trúc thì không có chỗ cho lỗi đó.
 *   3. Ràng buộc của backend (id duy nhất, dropdown phải có options…) nay hiện ngay lúc soạn
 *      chứ không đợi bấm Lưu mới trả 400.
 */

export type ElementType = TemplateElement['type'];

export const ELEMENT_TYPES: { value: ElementType; label: string; hint: string }[] = [
  { value: 'markdown', label: 'Đoạn hướng dẫn', hint: 'Chỉ hiển thị cho người điền, không thu thập câu trả lời.' },
  { value: 'input', label: 'Ô nhập một dòng', hint: 'Phiên bản, mã đơn hàng, đường dẫn…' },
  { value: 'textarea', label: 'Ô nhập nhiều dòng', hint: 'Mô tả hiện tượng, các bước tái hiện…' },
  { value: 'dropdown', label: 'Danh sách chọn', hint: 'Chọn đúng một mục trong danh sách có sẵn.' },
  { value: 'checkboxes', label: 'Hộp kiểm', hint: 'Chọn được nhiều mục.' },
];

/** Chuyển nhãn thành id: bỏ dấu, chỉ giữ chữ thường và gạch nối. */
export function slugify(label: string): string {
  return label.normalize('NFD').replace(/[̀-ͯ]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D')
    .toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')
    .slice(0, 40);
}

export function optionLabel(option: string | { label: string; required?: boolean }): string {
  return typeof option === 'string' ? option : option.label;
}

/** Một phần tử rỗng đúng kiểu, sẵn sàng cho người dùng điền định nghĩa. */
export function emptyElement(type: ElementType): TemplateElement {
  if (type === 'markdown') return { type, attributes: { value: '' } };
  return {
    type,
    id: '',
    attributes: { label: '', ...(type === 'dropdown' || type === 'checkboxes' ? { options: [''] } : {}) },
  };
}

/**
 * Lỗi khiến backend từ chối. Trả về danh sách để hiện ngay tại chỗ soạn, cùng bộ quy tắc với
 * `TemplateService.ValidateSchema`.
 */
export function validateTemplate(name: string, body: TemplateElement[]): string[] {
  const errors: string[] = [];
  if (!name.trim()) errors.push(tr('Template cần tên.'));
  errors.push(...validateElements(body));
  return errors;
}

/** Phần kiểm riêng cho danh sách phần tử — dùng được cả khi chưa có tên template. */
export function validateElements(body: TemplateElement[]): string[] {
  const errors: string[] = [];
  const seen = new Set<string>();
  body.forEach((el, i) => {
    const at = `#${i + 1}`;
    if (el.type === 'markdown') {
      if (!el.attributes.value?.trim()) errors.push(tr('Phần tử {v0}: đoạn hướng dẫn cần nội dung.', { v0: at }));
      return;
    }
    if (!el.attributes.label?.trim()) errors.push(tr('Phần tử {v0}: cần nhãn.', { v0: at }));
    const id = el.id?.trim() ?? '';
    if (!id) errors.push(tr('Phần tử {v0}: cần mã định danh.', { v0: at }));
    else if (seen.has(id)) errors.push(tr('Mã định danh "{v0}" bị trùng.', { v0: id }));
    else seen.add(id);

    if ((el.type === 'dropdown' || el.type === 'checkboxes')
      && (el.attributes.options ?? []).filter((o) => optionLabel(o).trim()).length === 0) {
      errors.push(tr('Phần tử {v0}: cần ít nhất một lựa chọn.', { v0: at }));
    }
  });
  return errors;
}

/** Dựng payload đúng hình dạng `TemplateRequest` của backend. */
export function toRequest(draft: TemplateDraft) {
  return {
    name: draft.name.trim(),
    description: draft.description.trim() || undefined,
    // API nhận `title`, không phải `titlePrefix` — đây chính là chỗ ô JSON cũ gửi sai.
    title: draft.titlePrefix.trim() || undefined,
    labels: draft.labels.split(',').map((x) => x.trim()).filter(Boolean),
    type: draft.type.trim() || undefined,
    isEnabled: draft.isEnabled,
    position: draft.position,
    body: draft.body.map((el) => (el.type === 'markdown'
      ? { type: el.type, attributes: { value: el.attributes.value ?? '' } }
      : {
        type: el.type,
        id: el.id?.trim(),
        attributes: {
          label: el.attributes.label ?? '',
          ...(el.attributes.description?.trim() ? { description: el.attributes.description.trim() } : {}),
          ...(el.attributes.placeholder?.trim() ? { placeholder: el.attributes.placeholder.trim() } : {}),
          ...(el.type === 'dropdown' || el.type === 'checkboxes'
            ? { options: (el.attributes.options ?? []).map(optionLabel).filter((o) => o.trim()) }
            : {}),
          ...(el.type === 'dropdown' && el.attributes.multiple ? { multiple: true } : {}),
        },
        ...(el.validations?.required ? { validations: { required: true } } : {}),
      })),
  };
}

export interface TemplateDraft {
  name: string;
  description: string;
  titlePrefix: string;
  labels: string;
  type: string;
  isEnabled: boolean;
  position: number;
  body: TemplateElement[];
}

export function draftFrom(template?: Template): TemplateDraft {
  return {
    name: template?.name ?? '',
    description: template?.description ?? '',
    titlePrefix: template?.titlePrefix ?? '',
    labels: (template?.defaults.labels ?? []).join(', '),
    type: template?.defaults.type ?? '',
    isEnabled: template?.isEnabled ?? true,
    position: template?.position ?? 0,
    body: template?.body ?? [],
  };
}

/**
 * Phần soạn **định nghĩa** của một phần tử: nhãn, mã định danh, lựa chọn, bắt buộc hay không.
 *
 * Tách riêng vì hai màn hình cùng cần: trình soạn template ở Settings, và ô thêm phần tử tại chỗ
 * ở màn hình New issue. Chúng khác nhau ở khung bao ngoài, không khác ở đây.
 */
export function ElementFields({
  el, fieldId, onChange,
}: {
  el: TemplateElement;
  /** Hậu tố cho `id` của các ô nhập, để nhiều bản trên cùng trang không đụng nhau. */
  fieldId: string;
  onChange: (next: TemplateElement) => void;
}) {
  if (el.type === 'markdown') {
    return (
      <div className="element-fields">
        <div className="field">
          <label htmlFor={`value-${fieldId}`}>{tr('Nội dung hướng dẫn')}<RequiredMark /></label>
          <textarea id={`value-${fieldId}`} className="input" value={el.attributes.value ?? ''}
            placeholder={tr('Hỗ trợ Markdown. Đoạn này chỉ để đọc.')}
            onChange={(e) => onChange({ ...el, attributes: { ...el.attributes, value: e.target.value } })} />
        </div>
      </div>
    );
  }

  return (
    <div className="element-fields">
      <div className="field">
        <label htmlFor={`label-${fieldId}`}>{tr('Nhãn')}<RequiredMark /></label>
        <input id={`label-${fieldId}`} className="input" value={el.attributes.label ?? ''}
          placeholder={tr('Câu hỏi hiện cho người điền')}
          onChange={(e) => {
            const label = e.target.value;
            // Mã định danh tự sinh theo nhãn cho tới khi người dùng tự sửa nó.
            const auto = !el.id || el.id === slugify(el.attributes.label ?? '');
            onChange({ ...el, id: auto ? slugify(label) : el.id, attributes: { ...el.attributes, label } });
          }} />
      </div>

      <div className="field">
        <label htmlFor={`id-${fieldId}`}>{tr('Mã định danh')}<RequiredMark /></label>
        <input id={`id-${fieldId}`} className="input" value={el.id ?? ''}
          placeholder="what-happened"
          onChange={(e) => onChange({ ...el, id: e.target.value })} />
        <span className="hint">{tr('Dùng để lưu câu trả lời. Không trùng nhau trong cùng một template.')}</span>
      </div>

      <div className="field">
        <label htmlFor={`desc-${fieldId}`}>{tr('Chú thích (tùy chọn)')}</label>
        <input id={`desc-${fieldId}`} className="input" value={el.attributes.description ?? ''}
          onChange={(e) => onChange({ ...el, attributes: { ...el.attributes, description: e.target.value } })} />
      </div>

      {(el.type === 'input' || el.type === 'textarea') && (
        <div className="field">
          <label htmlFor={`ph-${fieldId}`}>{tr('Gợi ý trong ô (tùy chọn)')}</label>
          <input id={`ph-${fieldId}`} className="input" value={el.attributes.placeholder ?? ''}
            onChange={(e) => onChange({ ...el, attributes: { ...el.attributes, placeholder: e.target.value } })} />
        </div>
      )}

      {(el.type === 'dropdown' || el.type === 'checkboxes') && (
        <div className="field">
          <label>{tr('Các lựa chọn')}<RequiredMark /></label>
          {(el.attributes.options ?? []).map((opt, oi) => (
            <div key={oi} style={{ display: 'flex', gap: 'var(--space-2)', marginBottom: 6 }}>
              <input className="input" value={optionLabel(opt)}
                aria-label={tr('Lựa chọn {v0}', { v0: oi + 1 })}
                onChange={(e) => {
                  const options = [...(el.attributes.options ?? [])];
                  options[oi] = e.target.value;
                  onChange({ ...el, attributes: { ...el.attributes, options } });
                }} />
              <button type="button" className="btn btn-ghost" title={tr('Xóa lựa chọn')}
                onClick={() => onChange({
                  ...el,
                  attributes: { ...el.attributes, options: (el.attributes.options ?? []).filter((_, k) => k !== oi) },
                })}>×</button>
            </div>
          ))}
          <button type="button" className="btn btn-secondary"
            onClick={() => onChange({
              ...el,
              attributes: { ...el.attributes, options: [...(el.attributes.options ?? []), ''] },
            })}>{tr('+ Thêm lựa chọn')}</button>
        </div>
      )}

      <label className="radio">
        <input type="checkbox" checked={el.validations?.required ?? false}
          onChange={(e) => onChange({ ...el, validations: { required: e.target.checked } })} />
        <span className="dot" />
        {tr('Bắt buộc điền')}
      </label>
    </div>
  );
}

/** Hàng nút thêm phần tử — dùng chung cho trình soạn template và ô thêm tại chỗ. */
export function AddElementRow({ onAdd }: { onAdd: (type: ElementType) => void }) {
  return (
    <div className="tpl-add">
      <span className="muted" style={{ fontSize: 13 }}>{tr('Thêm phần tử:')}</span>
      {ELEMENT_TYPES.map((t) => (
        <button key={t.value} type="button" className="btn btn-secondary"
          title={tr(t.hint)} onClick={() => onAdd(t.value)}>
          {tr(t.label)}
        </button>
      ))}
    </div>
  );
}

export function TemplateEditor({
  draft, onChange,
}: {
  draft: TemplateDraft;
  onChange: (next: TemplateDraft) => void;
}) {
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  const set = (patch: Partial<TemplateDraft>) => onChange({ ...draft, ...patch });

  function setElement(index: number, next: TemplateElement) {
    set({ body: draft.body.map((el, i) => (i === index ? next : el)) });
  }

  function addElement(type: ElementType) {
    set({ body: [...draft.body, emptyElement(type)] });
    setOpenIndex(draft.body.length);
  }

  function move(index: number, step: -1 | 1) {
    const to = index + step;
    if (to < 0 || to >= draft.body.length) return;
    const next = [...draft.body];
    [next[index], next[to]] = [next[to], next[index]];
    set({ body: next });
    setOpenIndex(to);
  }

  const errors = validateTemplate(draft.name, draft.body);

  return (
    <div className="tpl-editor">
      <div className="settings-grid">
        <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
          <div className="field">
            <label htmlFor="tpl-name">{tr('Tên template')}<RequiredMark /></label>
            <input id="tpl-name" className="input" value={draft.name}
              placeholder={tr('Ví dụ: Báo lỗi')}
              onChange={(e) => set({ name: e.target.value })} />
          </div>
          <div className="field">
            <label htmlFor="tpl-desc">{tr('Mô tả ngắn (tùy chọn)')}</label>
            <input id="tpl-desc" className="input" value={draft.description}
              placeholder={tr('Hiện dưới tên template ở màn hình chọn mẫu')}
              onChange={(e) => set({ description: e.target.value })} />
          </div>
          <div className="field">
            <label htmlFor="tpl-prefix">{tr('Tiền tố tiêu đề (tùy chọn)')}</label>
            <input id="tpl-prefix" className="input" value={draft.titlePrefix}
              placeholder="[Bug]: "
              onChange={(e) => set({ titlePrefix: e.target.value })} />
          </div>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-3)' }}>
          <div className="field">
            <label htmlFor="tpl-labels">{tr('Nhãn gắn sẵn (cách nhau bằng dấu phẩy)')}</label>
            <input id="tpl-labels" className="input" value={draft.labels}
              placeholder="bug, needs-triage"
              onChange={(e) => set({ labels: e.target.value })} />
          </div>
          <div className="field">
            <label htmlFor="tpl-type">{tr('Loại ticket gắn sẵn (tùy chọn)')}</label>
            <input id="tpl-type" className="input" value={draft.type}
              placeholder="Bug"
              onChange={(e) => set({ type: e.target.value })} />
          </div>
          <label className="radio">
            <input type="checkbox" checked={draft.isEnabled}
              onChange={(e) => set({ isEnabled: e.target.checked })} />
            <span className="dot" />
            {tr('Cho người dùng chọn template này')}
          </label>
        </div>
      </div>

      <h5 style={{ margin: 'var(--space-4) 0 0' }}>
        {tr('Các phần tử của biểu mẫu ({v0})', { v0: draft.body.length })}
      </h5>
      <div style={{ fontSize: 12, color: 'color-mix(in srgb, var(--color-text) 60%, transparent)' }}>
        {tr('Người tạo ticket sẽ điền lần lượt theo thứ tự dưới đây.')}
      </div>

      {draft.body.length === 0 && (
        <div className="empty">{tr('Chưa có phần tử nào. Thêm một phần tử ở bên dưới.')}</div>
      )}

      {draft.body.map((el, i) => {
        const meta = ELEMENT_TYPES.find((t) => t.value === el.type)!;
        const open = openIndex === i;
        return (
          <div key={i} className={`tpl-element ${open ? 'open' : ''}`.trim()}>
            <div className="head">
              <button type="button" className="btn btn-ghost grow"
                aria-expanded={open}
                onClick={() => setOpenIndex(open ? null : i)}>
                <span className="tag tag-neutral">{tr(meta.label)}</span>
                <span className="name">
                  {el.type === 'markdown'
                    ? (el.attributes.value?.slice(0, 48) || tr('(chưa có nội dung)'))
                    : (el.attributes.label || tr('(chưa có nhãn)'))}
                </span>
              </button>
              <button type="button" className="btn btn-ghost" title={tr('Lên')}
                disabled={i === 0} onClick={() => move(i, -1)}>↑</button>
              <button type="button" className="btn btn-ghost" title={tr('Xuống')}
                disabled={i === draft.body.length - 1} onClick={() => move(i, 1)}>↓</button>
              <button type="button" className="btn btn-ghost" title={tr('Xóa')}
                onClick={() => { set({ body: draft.body.filter((_, k) => k !== i) }); setOpenIndex(null); }}>×</button>
            </div>

            {open && (
              <ElementFields el={el} fieldId={`tpl-${i}`} onChange={(next) => setElement(i, next)} />
            )}
          </div>
        );
      })}

      <AddElementRow onAdd={addElement} />

      {errors.length > 0 && (
        <ul className="tpl-errors">
          {errors.map((e) => <li key={e}>{e}</li>)}
        </ul>
      )}
    </div>
  );
}
