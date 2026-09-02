import { describe, expect, it } from 'vitest';
import { mergeBody, missingRequired, renderFormBody } from '@/lib/templateBody';
import type { TemplateElement } from '@/lib/tickets';

/**
 * Chốt định dạng Markdown mà `TemplateService.RenderAsync` sinh ra.
 *
 * `lib/templateBody.ts` là bản dựng lại ở trình duyệt, dùng cho biểu mẫu người dùng thêm tại chỗ
 * trên màn hình New issue — lúc đó chưa có template nào để backend tra. Hai bản phải ra cùng một
 * văn bản, nếu không thì cùng một biểu mẫu sẽ cho ra hai kiểu mô tả tùy theo nó có được lưu thành
 * template hay chưa. Các kỳ vọng dưới đây chép từ `TemplateService.cs:104-190`.
 */

const input = (id: string, label: string, required = false): TemplateElement => ({
  type: 'input', id, attributes: { label }, ...(required ? { validations: { required: true } } : {}),
});

describe('renderFormBody', () => {
  it('mỗi trường là một heading cấp ba, cách nhau một dòng trống', () => {
    const body = renderFormBody([input('a', 'Phiên bản'), input('b', 'Trình duyệt')],
      { a: '1.4.2', b: 'Firefox' });
    expect(body).toBe('### Phiên bản\n\n1.4.2\n\n### Trình duyệt\n\nFirefox');
  });

  it('trường bỏ trống ghi _No response_ chứ không biến mất', () => {
    expect(renderFormBody([input('a', 'Phiên bản')], {})).toBe('### Phiên bản\n\n_No response_');
    expect(renderFormBody([input('a', 'Phiên bản')], { a: '   ' })).toBe('### Phiên bản\n\n_No response_');
  });

  it('đoạn hướng dẫn chỉ để đọc, không vào mô tả', () => {
    const els: TemplateElement[] = [
      { type: 'markdown', attributes: { value: 'Đọc kỹ trước khi điền.' } },
      input('a', 'Phiên bản'),
    ];
    expect(renderFormBody(els, { a: '1.0' })).toBe('### Phiên bản\n\n1.0');
  });

  it('thiếu nhãn thì lấy mã định danh làm tiêu đề', () => {
    const el: TemplateElement = { type: 'input', id: 'what-happened', attributes: { label: '' } };
    expect(renderFormBody([el], { 'what-happened': 'x' })).toBe('### what-happened\n\nx');
  });

  it('dropdown nhiều lựa chọn nối bằng dấu phẩy', () => {
    const el: TemplateElement = {
      type: 'dropdown', id: 'sev', attributes: { label: 'Mức độ', options: ['P1', 'P2', 'P3'], multiple: true },
    };
    expect(renderFormBody([el], { sev: ['P1', 'P3'] })).toBe('### Mức độ\n\nP1, P3');
    expect(renderFormBody([el], {})).toBe('### Mức độ\n\n_No response_');
  });

  it('dropdown một lựa chọn nhận cả chuỗi lẫn mảng một phần tử', () => {
    const el: TemplateElement = { type: 'dropdown', id: 'sev', attributes: { label: 'Mức độ', options: ['P1', 'P2'] } };
    expect(renderFormBody([el], { sev: 'P2' })).toBe('### Mức độ\n\nP2');
    expect(renderFormBody([el], { sev: ['P2'] })).toBe('### Mức độ\n\nP2');
  });

  it('hộp kiểm liệt kê mọi lựa chọn kèm dấu tích, không chỉ cái được chọn', () => {
    const el: TemplateElement = {
      type: 'checkboxes', id: 'ack', attributes: { label: 'Xác nhận', options: ['Đã đọc quy định', 'Đã thử lại'] },
    };
    expect(renderFormBody([el], { ack: ['Đã thử lại'] }))
      .toBe('### Xác nhận\n\n- [ ] Đã đọc quy định\n- [x] Đã thử lại');
  });

  it('lựa chọn dạng đối tượng lấy trường label', () => {
    const el: TemplateElement = {
      type: 'checkboxes', id: 'ack',
      attributes: { label: 'Xác nhận', options: [{ label: 'Tôi đồng ý', required: true }] },
    };
    expect(renderFormBody([el], { ack: ['Tôi đồng ý'] })).toBe('### Xác nhận\n\n- [x] Tôi đồng ý');
  });

  it('ô có render sinh khối mã, kể cả khi bỏ trống', () => {
    const el: TemplateElement = { type: 'textarea', id: 'log', attributes: { label: 'Log', render: 'shell' } };
    expect(renderFormBody([el], { log: 'panic' })).toBe('### Log\n\n```shell\npanic\n```');
    expect(renderFormBody([el], {})).toBe('### Log\n\n```shell\n\n```');
  });

  it('biểu mẫu rỗng cho chuỗi rỗng', () => {
    expect(renderFormBody([], {})).toBe('');
  });
});

describe('mergeBody', () => {
  it('mô tả tự do đứng sau biểu mẫu, cách một dòng trống', () => {
    expect(mergeBody('### A\n\nx', 'Ghi chú thêm')).toBe('### A\n\nx\n\nGhi chú thêm');
  });

  it('bên nào trống thì lấy nguyên bên còn lại', () => {
    expect(mergeBody('', 'Chỉ có mô tả')).toBe('Chỉ có mô tả');
    expect(mergeBody('### A\n\nx', '   ')).toBe('### A\n\nx');
    expect(mergeBody('', '')).toBe('');
  });
});

describe('missingRequired', () => {
  it('chỉ tính trường bắt buộc còn trống', () => {
    const els = [input('a', 'Phiên bản', true), input('b', 'Trình duyệt')];
    expect(missingRequired(els, { a: '1.0' })).toEqual([]);
    expect(missingRequired(els, {})).toEqual(['Phiên bản']);
    expect(missingRequired(els, { a: '  ' })).toEqual(['Phiên bản']);
  });

  it('mảng rỗng cũng là chưa điền', () => {
    const el: TemplateElement = {
      type: 'checkboxes', id: 'ack',
      attributes: { label: 'Xác nhận', options: ['x'] }, validations: { required: true },
    };
    expect(missingRequired([el], { ack: [] })).toEqual(['Xác nhận']);
    expect(missingRequired([el], { ack: ['x'] })).toEqual([]);
  });

  it('đoạn hướng dẫn không bao giờ bị đòi câu trả lời', () => {
    const el: TemplateElement = { type: 'markdown', attributes: { value: 'hi' }, validations: { required: true } };
    expect(missingRequired([el], {})).toEqual([]);
  });
});
