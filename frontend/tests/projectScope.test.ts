import { describe, expect, it } from 'vitest';
import {
  UNCATEGORIZED,
  isUncategorized,
  resolveActiveProject,
  supportsUncategorized,
} from '@/lib/projectScope';

const REAL = ['demo-alpha', 'demo-beta', 'support'];

describe('resolveActiveProject', () => {
  it('giữ nguyên project thật còn trong danh sách', () => {
    expect(resolveActiveProject('demo-beta', REAL, false)).toBe('demo-beta');
  });

  it('bỏ project thật đã mất quyền, về cái đầu tiên', () => {
    expect(resolveActiveProject('da-roi', REAL, false)).toBe('demo-alpha');
  });

  it('không tự chọn hộ khi chưa chọn gì', () => {
    expect(resolveActiveProject(null, REAL, true)).toBeNull();
  });

  it('trả null khi không còn project nào với tới', () => {
    expect(resolveActiveProject('demo-alpha', [], false)).toBeNull();
  });

  /**
   * Đây là hồi quy của lỗi thật: mục chưa phân loại bị đá về dự án thật đầu tiên ngay khi bấm
   * một tab bất kỳ. Nguyên nhân là project ảo không bao giờ nằm trong `GET /api/projects`, nên
   * phép kiểm "còn trong danh sách không" luôn trượt. Bỏ nhánh `isUncategorized` trong
   * `resolveActiveProject` là test này đỏ.
   */
  it('giữ mục chưa phân loại dù nó không nằm trong danh sách project thật', () => {
    expect(REAL).not.toContain(UNCATEGORIZED); // tiền đề của lỗi
    expect(resolveActiveProject(UNCATEGORIZED, REAL, true)).toBe(UNCATEGORIZED);
  });

  it('bỏ mục chưa phân loại khi không có quyền phân loại', () => {
    expect(resolveActiveProject(UNCATEGORIZED, REAL, false)).toBe('demo-alpha');
  });

  it('không có quyền phân loại mà cũng không có project nào thì bỏ chọn', () => {
    expect(resolveActiveProject(UNCATEGORIZED, [], false)).toBeNull();
  });
});

describe('isUncategorized', () => {
  it('nhận đúng slug, không phân biệt hoa thường', () => {
    expect(isUncategorized(UNCATEGORIZED)).toBe(true);
    expect(isUncategorized('Uncategorized')).toBe(true);
  });

  it('không nhận nhầm project thật hay giá trị rỗng', () => {
    expect(isUncategorized('demo-alpha')).toBe(false);
    expect(isUncategorized(null)).toBe(false);
    expect(isUncategorized(undefined)).toBe(false);
  });
});

describe('supportsUncategorized', () => {
  it('chỉ sự cố và phản hồi — hai bảng duy nhất cho project_id null', () => {
    expect(supportsUncategorized('incidents')).toBe(true);
    expect(supportsUncategorized('feedbacks')).toBe(true);
  });

  it('từ chối các màn hình bắt buộc thuộc một project', () => {
    for (const section of ['issues', 'labels', 'settings']) {
      expect(supportsUncategorized(section)).toBe(false);
    }
    expect(supportsUncategorized(null)).toBe(false);
  });
});
