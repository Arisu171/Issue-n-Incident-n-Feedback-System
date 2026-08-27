'use client';

import { useParams, useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useEffect, useState } from 'react';
import { ActionFeedback, Guard, PageHead, RequiredMark, Select, SubmitButton } from '@/components/ui';
import { MarkdownEditor } from '@/components/tickets/MarkdownEditor';
import { LabelChip } from '@/components/tickets/Bits';
import {
  AddElementRow, ElementFields, ELEMENT_TYPES, emptyElement, toRequest, validateElements,
} from '@/components/TemplateEditor';
import { useAction } from '@/lib/useAction';
import { session } from '@/lib/api';
import { mergeBody, missingRequired, renderFormBody, type FormAnswers } from '@/lib/templateBody';
import { tickets, type Label, type Project, type Template, type TemplateElement, type Ticket } from '@/lib/tickets';
import { tr } from '@/lib/i18n';

/** UC-17: chọn template (Issue Form) hoặc blank issue; form render theo schema mục 5.5. */
function NewIssue() {
  const { project } = useParams<{ project: string }>();
  const router = useRouter();
  const search = useSearchParams();
  const parent = search.get('parent');
  const preselect = search.get('template');

  const [info, setInfo] = useState<Project | null>(null);
  const [templates, setTemplates] = useState<Template[]>([]);
  const [labels, setLabels] = useState<Label[]>([]);
  const [template, setTemplate] = useState<Template | null | 'blank'>(null);
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [answers, setAnswers] = useState<Record<string, unknown>>({});
  const [chosenLabels, setChosenLabels] = useState<string[]>([]);
  const create = useAction<{ data: Ticket; etag: string | null }>();
  const canTriage = session.can('ticket.triage');

  // Biểu mẫu dựng tại chỗ cho ticket không theo mẫu. `ticket.write` là đúng quyền tạo template,
  // nên ai lưu được template thì mới thấy phần này — không mở cho khách.
  const canAuthorForm = session.can('ticket.write');
  const [adhoc, setAdhoc] = useState<TemplateElement[]>([]);
  const [adhocAnswers, setAdhocAnswers] = useState<FormAnswers>({});
  const [adhocNotice, setAdhocNotice] = useState<string | null>(null);
  // null = chưa bấm "Lưu thành template"; chuỗi = ô tên đang mở.
  const [tplName, setTplName] = useState<string | null>(null);
  const saveTpl = useAction<Template>();

  useEffect(() => {
    tickets.project(project).then(setInfo).catch(() => undefined);
    tickets.templates(project).then((t) => { setTemplates(t); if (preselect) setTemplate(t.find((x) => x.id === preselect) ?? null); }).catch(() => undefined);
    tickets.labels(project).then(setLabels).catch(() => undefined);
  }, [project, preselect]);

  useEffect(() => {
    if (templates.length === 0 && info && template === null) setTemplate('blank');
  }, [templates, info, template]);

  const tpl = template === 'blank' ? null : template;
  const usingAdhoc = !tpl && canAuthorForm && adhoc.length > 0;
  // Lỗi định nghĩa phần tử (thiếu nhãn, trùng mã…). Chặn cả gửi ticket lẫn lưu template, vì
  // `renderFormBody` tra câu trả lời theo mã định danh — mã rỗng hay trùng thì mô tả dựng ra sai.
  const adhocProblems = usingAdhoc ? validateElements(adhoc) : [];

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setAdhocNotice(null);

    let composed = body;
    if (usingAdhoc) {
      // Ô nhập có `required` được trình duyệt chặn sẵn; dropdown và hộp kiểm thì không, nên
      // kiểm lại ở đây thay vì để người dùng gửi đi một mô tả thiếu.
      const missing = missingRequired(adhoc, adhocAnswers);
      if (missing.length > 0) {
        setAdhocNotice(tr('Chưa điền: {v0}', { v0: missing.join(', ') }));
        return;
      }
      composed = mergeBody(renderFormBody(adhoc, adhocAnswers), body);
    }

    const r = await create.run(() => tickets.create(project, {
      title, body: composed || undefined,
      labels: canTriage && chosenLabels.length ? chosenLabels : undefined,
      templateId: template && template !== 'blank' ? template.id : undefined,
      formAnswers: template && template !== 'blank' ? answers : undefined,
      parentNumber: parent ? Number(parent) : undefined,
    }), tr('Đã tạo ticket.'));
    if (r) router.push(`/projects/${project}/issues/${r.data.number}`);
  }

  /** Lưu **các câu hỏi** thành template dùng lại. Không gửi ticket, không lưu câu trả lời. */
  async function saveTemplate() {
    const name = tplName?.trim();
    if (!name || adhocProblems.length > 0) return;
    const saved = await saveTpl.run(() => tickets.createTemplate(project, toRequest({
      name, description: '', titlePrefix: '', labels: '', type: '',
      isEnabled: true, position: templates.length, body: adhoc,
    })), tr('Đã lưu thành template. Ticket bạn đang soạn vẫn chưa gửi.'));
    if (saved) {
      setTemplates((list) => [...list, saved]);
      setTplName(null);
    }
  }

  const contactLinks = info?.contactLinks ?? [];

  /**
   * Bản mẫu đặt dải thẻ mẫu và biểu mẫu trên cùng một màn hình: thẻ đang chọn viền accent
   * và mang nhãn "Selected", các thẻ còn lại nhãn "Template", thẻ trắng viền đứt.
   */
  return (
    <form onSubmit={submit} style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-4)' }}>
      <PageHead kicker={project} title="New issue" hint={parent ? tr('Sub-issue của #{v0}', { v0: parent }) : undefined} />

      <div className="tpl-list" style={{ borderTop: '1px solid var(--color-divider)', paddingTop: 'var(--space-3)' }}>
        {templates.map((t) => {
          const on = tpl?.id === t.id;
          return (
            <button type="button" key={t.id} className={`tpl-row${on ? ' selected' : ''}`} aria-pressed={on}
              onClick={() => { setTemplate(t); setAnswers({}); }}>
              <div className="card-kicker">{on ? 'Selected' : 'Template'}</div>
              <strong>{t.name}</strong>
              <p className="desc">{t.description}</p>
            </button>
          );
        })}
        {(info?.blankIssuesEnabled || canTriage) && (
          <button type="button" className={`tpl-row blank${template === 'blank' ? ' selected' : ''}`} aria-pressed={template === 'blank'}
            onClick={() => { setTemplate('blank'); setAnswers({}); }}>
            <div className="card-kicker">{template === 'blank' ? 'Selected' : 'Blank'}</div>
            <strong>Open a blank issue</strong>
            <p className="desc">{tr('Tạo ticket từ đầu, không theo mẫu nào.')}</p>
          </button>
        )}
        {contactLinks.map((c) => (
          <a key={c.url} href={c.url} target="_blank" rel="noreferrer" className="tpl-row blank" style={{ color: 'inherit' }}>
            <div className="card-kicker">{tr('Cần hỗ trợ khác')}</div>
            <strong>{c.name}</strong>
            <p className="desc">{c.about}</p>
          </a>
        ))}
      </div>

      <div className="form-grid">
        <div className="form-main">
          <ActionFeedback action={create} processingLabel={tr('Đang tạo…')} />
          <div className="field">
            <label htmlFor="title">{tr('Tiêu đề')}<RequiredMark /></label>
            <input className="input" id="title" required maxLength={256} value={title} onChange={(e) => setTitle(e.target.value)} placeholder={tpl?.titlePrefix ? `${tpl.titlePrefix}…` : tr('Tiêu đề')} />
          </div>
          {tpl ? (
            <>
              {tpl.body.map((el, i) => <FormElement key={el.id ?? i} el={el} value={answers[el.id ?? '']} onChange={(v) => setAnswers((a) => ({ ...a, [el.id ?? '']: v }))} />)}
              <div className="field"><label>{tr('Thông tin thêm (tùy chọn)')}</label><MarkdownEditor value={body} onChange={setBody} project={project} minRows={4} /></div>
            </>
          ) : (
            <>
              {canAuthorForm && (
                <AdhocForm
                  elements={adhoc}
                  onElements={setAdhoc}
                  answers={adhocAnswers}
                  onAnswers={setAdhocAnswers}
                  problems={adhocProblems}
                />
              )}
              <div className="field">
                <label>{adhoc.length > 0 ? tr('Thông tin thêm (tùy chọn)') : tr('Mô tả')}</label>
                <MarkdownEditor value={body} onChange={setBody} project={project} minRows={adhoc.length > 0 ? 4 : 10} onSubmit={() => undefined} />
              </div>
            </>
          )}

          {adhocNotice && <div className="alert error" role="alert">{adhocNotice}</div>}
          <ActionFeedback action={saveTpl} processingLabel={tr('Đang lưu template…')} />

          <div className="form-actions">
            {usingAdhoc && (
              <div className="form-actions-lead">
                {tplName === null ? (
                  <button type="button" className="btn btn-secondary"
                    title={tr('Lưu lại các câu hỏi ở trên để lần sau chọn như một mẫu.')}
                    onClick={() => setTplName('')}>
                    {tr('Lưu câu hỏi thành template…')}
                  </button>
                ) : (
                  <>
                    <input className="input" style={{ width: 200 }} value={tplName} autoFocus
                      aria-label={tr('Tên template')} placeholder={tr('Tên template')}
                      onChange={(e) => setTplName(e.target.value)}
                      onKeyDown={(e) => {
                        // Enter trong ô này mà không chặn thì form gửi luôn ticket. Nút lưu
                        // template tuyệt đối không được kiêm việc gửi ticket.
                        if (e.key === 'Enter') { e.preventDefault(); void saveTemplate(); }
                      }} />
                    <button type="button" className="btn btn-secondary"
                      disabled={!tplName.trim() || adhocProblems.length > 0 || saveTpl.isProcessing}
                      onClick={() => void saveTemplate()}>{tr('Lưu template')}</button>
                    <button type="button" className="btn btn-ghost" onClick={() => setTplName(null)}>{tr('Bỏ')}</button>
                    <span className="hint">{tr('Chỉ lưu các câu hỏi, không lưu câu trả lời bạn vừa điền.')}</span>
                  </>
                )}
              </div>
            )}
            <button type="button" className="btn btn-secondary" onClick={() => router.push(`/projects/${project}/issues`)}>Cancel</button>
            <SubmitButton action={create} processingLabel={tr('Đang tạo…')} className="btn btn-primary" disabled={adhocProblems.length > 0}>Submit new issue</SubmitButton>
          </div>
        </div>

        <aside className="form-aside">
          {tpl && (tpl.defaults.labels?.length || tpl.defaults.type) ? (
            <div>
              <div className="sb-title" style={{ marginBottom: 6 }}>From template</div>
              <div style={{ display: 'flex', gap: 'var(--space-1)', flexWrap: 'wrap' }}>
                {tpl.defaults.labels?.map((l) => <span key={l} className="tag tag-accent">{l}</span>)}
                {tpl.defaults.type && <span className="tag tag-outline">{tpl.defaults.type}</span>}
              </div>
            </div>
          ) : null}
          {canTriage && labels.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <div className="sb-title">Labels</div>
              <div style={{ display: 'flex', gap: 'var(--space-1)', flexWrap: 'wrap' }}>
                {labels.map((l) => (
                  <span key={l.id} onClick={() => setChosenLabels((c) => c.includes(l.name) ? c.filter((x) => x !== l.name) : [...c, l.name])}
                    style={{ opacity: chosenLabels.includes(l.name) ? 1 : 0.45, cursor: 'pointer' }}>
                    <LabelChip label={l} />
                  </span>
                ))}
              </div>
            </div>
          )}
          {contactLinks.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <div className="sb-title">Need help instead?</div>
              {contactLinks.map((c) => <a key={c.url} href={c.url} target="_blank" rel="noreferrer">{c.name} →</a>)}
            </div>
          )}
        </aside>
      </div>
    </form>
  );
}

/**
 * Biểu mẫu dựng tại chỗ, cho ticket không theo template nào.
 *
 * Trước đây blank issue chỉ có một ô Markdown tự do: muốn hỏi đúng ba thông tin cũng phải lập hẳn
 * một template ở Settings. Phần này cho vừa **đặt câu hỏi** vừa **trả lời** ngay trên màn hình tạo
 * ticket. Câu trả lời được dựng thành Markdown ở trình duyệt (`renderFormBody`) rồi gửi đi như mô
 * tả thường, nên không cần `templateId` và backend không phải biết gì về nó.
 *
 * Dùng lại `ElementFields` và `AddElementRow` của trình soạn template — cùng một bộ ô, đặt trong
 * khung khác — để sửa một chỗ không quên chỗ kia.
 */
function AdhocForm({ elements, onElements, answers, onAnswers, problems }: {
  elements: TemplateElement[];
  onElements: (next: TemplateElement[]) => void;
  answers: FormAnswers;
  onAnswers: (next: FormAnswers) => void;
  problems: string[];
}) {
  const [openIndex, setOpenIndex] = useState<number | null>(null);
  // Ticket trắng là ca thường gặp nhất, nên dải nút loại phần tử ẩn cho tới khi có người cần.
  const [showTypes, setShowTypes] = useState(false);

  function setElement(index: number, next: TemplateElement) {
    onElements(elements.map((el, i) => (i === index ? next : el)));
  }

  function move(index: number, step: -1 | 1) {
    const to = index + step;
    if (to < 0 || to >= elements.length) return;
    const next = [...elements];
    [next[index], next[to]] = [next[to], next[index]];
    onElements(next);
    setOpenIndex(openIndex === index ? to : openIndex);
  }

  return (
    <div className="adhoc-form">
      {elements.map((el, i) => {
        const meta = ELEMENT_TYPES.find((t) => t.value === el.type)!;
        const open = openIndex === i;
        const id = el.id ?? '';
        return (
          <div key={i} className={`tpl-element ${open ? 'open' : ''}`.trim()}>
            <div className="head">
              <button type="button" className="btn btn-ghost grow" aria-expanded={open}
                title={tr('Sửa câu hỏi')}
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
                disabled={i === elements.length - 1} onClick={() => move(i, 1)}>↓</button>
              <button type="button" className="btn btn-ghost" title={tr('Xóa')}
                onClick={() => { onElements(elements.filter((_, k) => k !== i)); setOpenIndex(null); }}>×</button>
            </div>

            {open && (
              <ElementFields el={el} fieldId={`adhoc-${i}`} onChange={(next) => setElement(i, next)} />
            )}

            <div className="adhoc-answer">
              <FormElement el={el} value={answers[id]}
                onChange={(v) => onAnswers({ ...answers, [id]: v as string | string[] })} />
            </div>
          </div>
        );
      })}

      {showTypes || elements.length > 0 ? (
        <AddElementRow onAdd={(type) => { onElements([...elements, emptyElement(type)]); setOpenIndex(elements.length); }} />
      ) : (
        <button type="button" className="btn btn-ghost" style={{ alignSelf: 'flex-start' }}
          onClick={() => setShowTypes(true)}>
          {tr('+ Thêm ô nhập có cấu trúc')}
        </button>
      )}

      {problems.length > 0 && (
        <ul className="tpl-errors">
          {problems.map((e) => <li key={e}>{e}</li>)}
        </ul>
      )}
    </div>
  );
}

function FormElement({ el, value, onChange }: { el: TemplateElement; value: unknown; onChange: (v: unknown) => void }) {
  const a = el.attributes;
  const required = el.validations?.required;
  if (el.type === 'markdown') return <div className="form-el markdown-body" dangerouslySetInnerHTML={{ __html: (a.value ?? '').replace(/</g, '&lt;') }} />;
  const label = <label className="lbl">{a.label}{required && <span className="req"> *</span>}</label>;
  const hint = a.description ? <div className="hint">{a.description}</div> : null;
  if (el.type === 'input') return <div className="form-el">{label}{hint}<input className="input" required={required} placeholder={a.placeholder} value={(value as string) ?? a.value ?? ''} onChange={(e) => onChange(e.target.value)} /></div>;
  if (el.type === 'textarea') return <div className="form-el">{label}{hint}<textarea className="input" required={required} placeholder={a.placeholder} value={(value as string) ?? a.value ?? ''} onChange={(e) => onChange(e.target.value)} /></div>;
  if (el.type === 'dropdown') {
    const options = (a.options ?? []).map((o) => (typeof o === 'string' ? o : o.label));
    if (a.multiple) {
      const selected = Array.isArray(value) ? (value as string[]) : [];
      return <div className="form-el">{label}{hint}<div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>{options.map((o) => <label key={o} className="radio"><input type="checkbox" checked={selected.includes(o)} onChange={(e) => onChange(e.target.checked ? [...selected, o] : selected.filter((x) => x !== o))} /><span className="dot" />{o}</label>)}</div></div>;
    }
    return <div className="form-el">{label}{hint}<Select value={(value as string) ?? (a.default !== undefined ? options[a.default] : '')} onChange={onChange} placeholder={tr('Chọn')} ariaLabel={a.label} options={options.map((o) => ({ value: o, label: o }))} /></div>;
  }
  const checked = Array.isArray(value) ? (value as string[]) : [];
  return (
    <div className="form-el">{label}{hint}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {(a.options ?? []).map((o) => { const l = typeof o === 'string' ? o : o.label; const req = typeof o !== 'string' && o.required; return <label key={l} className="radio"><input type="checkbox" required={req} checked={checked.includes(l)} onChange={(e) => onChange(e.target.checked ? [...checked, l] : checked.filter((x) => x !== l))} /><span className="dot" />{l}{req && <span style={{ color: 'var(--color-accent)' }}> {tr('bắt buộc')}</span>}</label>; })}
      </div>
    </div>
  );
}

export default function NewIssuePage() {
  return <Guard permission="ticket.create"><Suspense fallback={<div className="empty">{tr('Đang tải…')}</div>}><NewIssue /></Suspense></Guard>;
}
