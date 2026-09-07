import { describe, expect, it } from 'vitest';
import { transferTargets, UNCATEGORIZED } from '@/components/tickets/ProjectTransfer';

/**
 * Ô chọn project đích không được bày ra lựa chọn mà cuối cùng không chọn được — luật chung của
 * dự án, và ở đây nó có hai vế cụ thể.
 */
describe('transferTargets', () => {
  const projects = [
    { slug: 'alpha', name: 'Alpha' },
    { slug: 'beta', name: 'Beta' },
  ];

  it('bỏ chính project đang đứng — chuyển tới nó là thao tác rỗng', () => {
    const values = transferTargets(projects, 'alpha').map((t) => t.value);
    expect(values).not.toContain('alpha');
    expect(values).toContain('beta');
  });

  it('ở trong một project thì vẫn trả ngược về mục chưa phân loại được', () => {
    expect(transferTargets(projects, 'alpha').map((t) => t.value)).toContain(UNCATEGORIZED);
  });

  it('đang đứng ở mục chưa phân loại thì không bày lại chính nó', () => {
    const values = transferTargets(projects, UNCATEGORIZED).map((t) => t.value);
    expect(values).not.toContain(UNCATEGORIZED);
    expect(values).toEqual(['alpha', 'beta']);
  });

  it('hiển thị tên project chứ không phải slug', () => {
    expect(transferTargets(projects, UNCATEGORIZED)[0]).toEqual({ value: 'alpha', label: 'Alpha' });
  });
});
