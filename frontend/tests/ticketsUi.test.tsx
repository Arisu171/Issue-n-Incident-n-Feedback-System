import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { buildQuery, dslTokens, initials, isFilterToken, rangeStartIso, removeDslToken, setDslQualifier, splitQuery } from '@/lib/util';
import { Markdown, sanitizeHtml } from '@/components/Markdown';
import { buildDsl, labelTextColor, stateFromDsl, timeAgo } from '@/lib/util';

describe('Markdown (BR-SEC-02 client layer)', () => {
  it('strips script and event handlers even if server output were compromised', () => {
    const html = '<p>hi <a href="javascript:alert(1)">x</a><img src=x onerror="alert(2)"><script>alert(3)</script></p>';
    const safe = sanitizeHtml(html);
    expect(safe).not.toContain('<script');
    expect(safe).not.toContain('onerror');
    expect(safe).not.toContain('javascript:');
  });

  it('keeps task list checkboxes and mention links', () => {
    render(<Markdown html='<ul class="contains-task-list"><li><input type="checkbox" disabled checked> done</li></ul><a class="user-mention" href="/users/kien">@kien</a>' />);
    expect(screen.getByRole('checkbox')).toBeDisabled();
    expect(screen.getByText('@kien')).toHaveAttribute('href', '/users/kien');
  });
});

describe('util', () => {
  it('picks readable label text color', () => {
    expect(labelTextColor('d73a4a')).toBe('#ffffff');
    expect(labelTextColor('e4e669')).toBe('#1f2328');
    expect(labelTextColor('bad')).toBe('#1f2328');
  });

  it('builds Query DSL from filters and reads state back', () => {
    const q = buildDsl({ state: 'open', labels: ['bug', 'good first issue'], assignee: '@me', sort: 'updated-desc', text: 'đăng nhập' });
    expect(q).toBe('is:open label:bug label:"good first issue" assignee:@me sort:updated-desc đăng nhập');
    expect(stateFromDsl(q)).toBe('open');
    expect(stateFromDsl('is:closed label:bug')).toBe('closed');
    expect(stateFromDsl('label:bug')).toBe('all');
  });

  it('formats relative time like GitHub', () => {
    const now = new Date('2026-09-06T10:00:00Z');
    expect(timeAgo('2026-09-06T09:59:50Z', now)).toBe('vừa xong');
    expect(timeAgo('2026-09-06T09:30:00Z', now)).toBe('30 phút trước');
    expect(timeAgo('2026-09-04T10:00:00Z', now)).toBe('2 ngày trước');
  });
});

describe('Query DSL helpers', () => {
  it('tokenises while keeping quoted values intact', () => {
    expect(dslTokens('is:open label:"cần gấp" -label:wontfix')).toEqual([
      'is:open',
      'label:"cần gấp"',
      '-label:wontfix',
    ]);
  });

  it('removes one chip without touching the rest', () => {
    expect(removeDslToken('is:open label:bug sort:updated-desc', 'label:bug')).toBe(
      'is:open sort:updated-desc',
    );
  });

  it('replaces a qualifier instead of stacking duplicates', () => {
    expect(setDslQualifier('is:open sort:created-desc', ['sort'], 'sort:updated-desc')).toBe(
      'is:open sort:updated-desc',
    );
    expect(setDslQualifier('is:open label:bug', ['is', 'state'], null, (t) =>
      /^(is|state):(open|closed)$/.test(t))).toBe('label:bug');
  });
});

describe('initials', () => {
  it('takes the first letter of the first and last word', () => {
    expect(initials('Nguyễn Bảo Long')).toBe('NL');
    expect(initials('Lê Sơn Trường')).toBe('LT');
    expect(initials('Đỗ Quang Dũng')).toBe('ĐD');
  });

  it('separates names that used to collide', () => {
    expect(initials('Nguyễn Bảo Long')).not.toBe(initials('Nguyễn Huy Kiên'));
  });

  it('splits logins on dots and dashes', () => {
    expect(initials('son-truong')).toBe('st');
    expect(initials('huy.kien')).toBe('hk');
  });

  it('falls back to two letters for a single word', () => {
    expect(initials('admin')).toBe('ad');
    expect(initials('  Lê  ')).toBe('Lê');
  });

  it('always returns something for empty input', () => {
    expect(initials('   ')).toBe('?');
  });
});

describe('tách truy vấn thành chip và chữ', () => {
  it('chỉ nhận qualifier mà backend hiểu', () => {
    expect(isFilterToken('is:open')).toBe(true);
    expect(isFilterToken('-label:wontfix')).toBe(true);
    expect(isFilterToken('created:>2026-08-01')).toBe(true);
    expect(isFilterToken('label:"cần gấp"')).toBe(true);
  });

  it('không nhận nhầm chuỗi chỉ vì có dấu hai chấm', () => {
    // Đây là lý do danh sách qualifier phải đóng thay vì bắt theo mẫu `x:y`.
    expect(isFilterToken('http://example.com')).toBe(false);
    expect(isFilterToken('9:30')).toBe(false);
    expect(isFilterToken('ghichu:')).toBe(false);
    expect(isFilterToken('thanh-toán')).toBe(false);
  });

  it('gom chữ tự do lại dù nó nằm xen giữa các bộ lọc', () => {
    expect(splitQuery('payment is:open stuck label:incident')).toEqual({
      filters: ['is:open', 'label:incident'],
      text: 'payment stuck',
    });
  });

  it('để nguyên toán tử của DSL trong phần chữ', () => {
    // Bọc `(`, `OR` thành chip sẽ làm mất cấu trúc biểu thức.
    expect(splitQuery('(a OR b) is:open').filters).toEqual(['is:open']);
    expect(splitQuery('(a OR b) is:open').text).toBe('(a OR b)');
  });

  it('ghép lại thì chữ đứng trước bộ lọc', () => {
    expect(buildQuery(['is:open', 'label:bug'], 'payment stuck'))
      .toBe('payment stuck is:open label:bug');
    expect(buildQuery(['is:open'], '   ')).toBe('is:open');
    expect(buildQuery([], 'chỉ có chữ')).toBe('chỉ có chữ');
  });

  it('tách rồi ghép lại giữ nguyên mọi thành phần', () => {
    const query = 'thanh toán is:open -label:wontfix sort:created-desc lỗi';
    const { filters, text } = splitQuery(query);
    expect(buildQuery(filters, text).split(' ').sort()).toEqual(query.split(' ').sort());
  });
});

describe('khoảng thời gian của bộ lọc', () => {
  const now = new Date('2026-09-06T03:00:00.000Z');

  it('không giới hạn thì không gửi mốc nào', () => {
    expect(rangeStartIso('all', now)).toBeUndefined();
  });

  it('tính lùi đúng số ngày', () => {
    expect(rangeStartIso('1d', now)).toBe('2026-09-05T03:00:00.000Z');
    expect(rangeStartIso('1w', now)).toBe('2026-08-30T03:00:00.000Z');
    expect(rangeStartIso('1m', now)).toBe('2026-08-07T03:00:00.000Z');
  });

  it('không phụ thuộc múi giờ vì tính lùi từ thời điểm hiện tại', () => {
    // Đây là điểm khác bản cũ: `new Date('2026-09-06')` được hiểu là UTC nên ở giờ Việt Nam
    // mốc rơi vào 07:00 sáng và bộ lọc bỏ sót 7 giờ đầu ngày. Cách tính lùi không có chỗ nào
    // phân tích chuỗi ngày nên không dính vấn đề đó.
    const iso = rangeStartIso('1d', now)!;
    expect(new Date(iso).getTime()).toBe(now.getTime() - 24 * 60 * 60 * 1000);
  });
});
