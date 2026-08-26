import type { TemplateElement } from '@/lib/tickets';

/**
 * Dựng phần mô tả Markdown từ một biểu mẫu và câu trả lời của nó.
 *
 * ## Vì sao có bản này ở frontend
 *
 * Ticket tạo **theo template** thì backend dựng (`TemplateService.RenderAsync`). Ticket có ô
 * thêm tại chỗ mà chưa lưu thành template thì không có template để backend tra, nên phía trình
 * duyệt phải tự dựng rồi gửi đi như một mô tả thường.
 *
 * ## Đây là mã lặp, và đó là lựa chọn có chủ đích
 *
 * Gộp làm một sẽ phải đưa cả phần kiểm trường bắt buộc và mã lỗi 422 sang frontend, hoặc phải
 * thêm một endpoint chỉ để dựng văn bản. Cả hai đều đắt hơn cái giá phải trả ở đây: giữ hai bản
 * cùng định dạng. Đổi định dạng ở một bên thì phải đổi bên kia — test
 * `templateBody.test.ts` chốt đúng định dạng hiện tại để việc trôi lệch không đi qua âm thầm.
 */

export type FormAnswers = Record<string, string | string[]>;

function optionLabel(option: string | { label: string; required?: boolean }): string {
  return typeof option === 'string' ? option : option.label;
}

/** Đúng chuỗi mà backend ghi khi một trường không có câu trả lời. */
const NO_RESPONSE = '_No response_';

export function renderFormBody(elements: TemplateElement[], answers: FormAnswers): string {
  let out = '';

  for (const el of elements) {
    // Đoạn hướng dẫn chỉ để đọc, không vào mô tả — giống `RenderAsync`.
    if (el.type === 'markdown') continue;

    const id = el.id ?? '';
    const label = el.attributes.label || id;
    const answer = answers[id];

    if (el.type === 'input' || el.type === 'textarea') {
      const text = (typeof answer === 'string' ? answer : '').trim();
      const render = el.attributes.render;
      out += `### ${label}\n\n`;
      out += render
        ? `\`\`\`${render}\n${text}\n\`\`\``
        : (text.length === 0 ? NO_RESPONSE : text);
      out += '\n\n';
      continue;
    }

    if (el.type === 'dropdown') {
      const selected = Array.isArray(answer) ? answer : (typeof answer === 'string' && answer ? [answer] : []);
      out += `### ${label}\n\n${selected.length === 0 ? NO_RESPONSE : selected.join(', ')}\n\n`;
      continue;
    }

    // checkboxes — liệt kê **mọi** lựa chọn kèm dấu tích, không chỉ những cái được chọn.
    const checked = new Set(Array.isArray(answer) ? answer : []);
    out += `### ${label}\n\n`;
    for (const opt of el.attributes.options ?? []) {
      const text = optionLabel(opt);
      out += `${checked.has(text) ? '- [x] ' : '- [ ] '}${text}\n`;
    }
    out += '\n';
  }

  return out.trimEnd();
}

/**
 * Ghép phần biểu mẫu với phần mô tả tự do. Cùng quy tắc với `RenderAsync`: mô tả tự do đứng sau,
 * cách nhau một dòng trống; bên nào trống thì lấy nguyên bên còn lại.
 */
export function mergeBody(formBody: string, freeText: string): string {
  const free = freeText.trim();
  if (formBody.length === 0) return free;
  if (free.length === 0) return formBody;
  return `${formBody}\n\n${free}`;
}

/** Trường bắt buộc còn trống — chặn gửi ngay trên giao diện thay vì đợi backend trả 422. */
export function missingRequired(elements: TemplateElement[], answers: FormAnswers): string[] {
  const missing: string[] = [];
  for (const el of elements) {
    if (el.type === 'markdown' || !el.validations?.required) continue;
    const id = el.id ?? '';
    const answer = answers[id];
    const empty = Array.isArray(answer)
      ? answer.length === 0
      : !(typeof answer === 'string' && answer.trim().length > 0);
    if (empty) missing.push(el.attributes.label || id);
  }
  return missing;
}
