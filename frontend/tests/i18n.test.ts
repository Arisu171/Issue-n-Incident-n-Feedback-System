import { describe, expect, it } from 'vitest';
import { tr } from '@/lib/i18n';
import { en } from '@/lib/messages/en';

/**
 * Lõi i18n và tính toàn vẹn của catalog.
 *
 * Các test dịch chạy ở ngôn ngữ mặc định ('vi') vì `activeLocale` chỉ đổi từ trình duyệt —
 * nên ở đây `tr()` phải trả lại **đúng chuỗi nguồn**. Đó cũng chính là hành vi cần bảo đảm:
 * thiếu bản dịch thì người dùng thấy tiếng Việt, không thấy khoá thô.
 */
describe('tr', () => {
  it('trả lại chuỗi nguồn khi chưa đổi ngôn ngữ', () => {
    expect(tr('Đang tải…')).toBe('Đang tải…');
  });

  it('thay chỗ giữ chỗ theo tên', () => {
    expect(tr('{v0} kết quả', { v0: 31 })).toBe('31 kết quả');
    expect(tr('Cấp {v0}, ngang hoặc trên cấp {v1} của bạn.', { v0: 60, v1: 40 }))
      .toBe('Cấp 60, ngang hoặc trên cấp 40 của bạn.');
  });

  it('thay mọi lần xuất hiện của cùng một tên', () => {
    expect(tr('{v0} và {v0}', { v0: 'x' })).toBe('x và x');
  });

  it('chấp nhận giá trị không phải chuỗi', () => {
    expect(tr('{v0}', { v0: null })).toBe('null');
    expect(tr('{v0}', { v0: 7 })).toBe('7');
  });

  it('bỏ qua chỗ giữ chỗ không được truyền', () => {
    expect(tr('còn {v0}')).toBe('còn {v0}');
  });
});

describe('catalog English', () => {
  it('không có bản dịch rỗng', () => {
    const empty = Object.entries(en).filter(([, value]) => value.trim() === '');
    expect(empty).toEqual([]);
  });

  it('giữ nguyên bộ chỗ giữ chỗ của chuỗi nguồn', () => {
    const holes = (s: string) => [...s.matchAll(/\{(v\d+)\}/g)].map((m) => m[1]).sort();
    const mismatched = Object.entries(en)
      .filter(([source, target]) => holes(source).join() !== holes(target).join())
      .map(([source]) => source);
    expect(mismatched).toEqual([]);
  });

  it('không dịch từ vựng sản phẩm thành thứ khác', () => {
    // Issues/Incident/Feedback/Board/Inbox… giữ nguyên ở cả hai ngôn ngữ nên không được
    // xuất hiện như một khoá cần dịch.
    expect(en).not.toHaveProperty('Issues');
    expect(en).not.toHaveProperty('Board');
    expect(en).not.toHaveProperty('Inbox');
  });
});
