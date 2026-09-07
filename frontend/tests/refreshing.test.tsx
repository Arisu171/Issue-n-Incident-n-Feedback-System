import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, sep } from 'node:path';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Refreshing } from '@/components/ui';

describe('Refreshing', () => {
  it('vẫn hiện nội dung trong lúc tải lại', () => {
    render(<Refreshing busy><span>hàng dữ liệu</span></Refreshing>);
    expect(screen.getByText('hàng dữ liệu')).toBeInTheDocument();
  });

  it('báo aria-busy cho trình đọc màn hình khi đang tải lại', () => {
    const { container } = render(<Refreshing busy><span>x</span></Refreshing>);
    expect(container.firstElementChild).toHaveAttribute('aria-busy', 'true');
  });

  it('lúc rảnh thì không để lại thuộc tính hay lớp thừa', () => {
    const { container } = render(<Refreshing busy={false}><span>x</span></Refreshing>);
    const root = container.firstElementChild!;
    expect(root).not.toHaveAttribute('aria-busy');
    expect(root.className).toBe('');
  });
});

/**
 * Chốt bằng máy cho một lỗi giao diện đã xảy ra thật ở ba màn hình.
 *
 * Mẫu `{!load.isProcessing && data && …}` gỡ hẳn bảng khỏi DOM mỗi lần làm mới: sửa một dòng là
 * mất vị trí cuộn, mất những phần đang mở, và nháy trắng một nhịp — trong khi dữ liệu cũ vẫn còn
 * nguyên và vẫn đọc được. Chỗ trống chỉ dành cho lần tải ĐẦU.
 *
 * Quét mã nguồn thay vì dựng từng trang lên: lỗi này nằm ở *hình dạng của điều kiện*, và một
 * phép quét bắt được cả những màn hình chưa ai viết.
 */
describe('không màn hình nào được gỡ bảng khi tải lại', () => {
  // `process.cwd()` chứ không phải `import.meta.url`: dưới vitest, URL của module mang tiền
  // tố `/@fs/` của dev server nên đổi sang đường dẫn tệp thật sẽ ra một chỗ không tồn tại.
  const ROOT = process.cwd();

  function walk(dir: string, out: string[] = []): string[] {
    for (const name of readdirSync(dir)) {
      const full = join(dir, name);
      if (statSync(full).isDirectory()) walk(full, out);
      else if (/\.tsx$/.test(name)) out.push(full);
    }
    return out;
  }

  it('không còn chỗ nào ẩn nội dung theo `!load.isProcessing`', () => {
    const offenders: string[] = [];
    for (const file of walk(join(ROOT, 'app'))) {
      const src = readFileSync(file, 'utf8');
      // Ẩn nội dung khi đang tải: `{!load.isProcessing && <thứ gì đó>}`.
      if (/\{\s*!\s*load\.isProcessing\s*&&/.test(src)) {
        offenders.push(file.slice(ROOT.length).split(sep).join('/'));
      }
      // Hiện chỗ trống mà KHÔNG kèm điều kiện "chưa có dữ liệu" — tức che mất bảng đang có.
      for (const m of src.matchAll(/\{\s*load\.isProcessing\s*&&([^}]*)\}/g)) {
        if (!/!\s*\w/.test(m[1]) && /className="empty"/.test(m[1])) {
          offenders.push(`${file.slice(ROOT.length).split(sep).join('/')} — chỗ trống không kèm điều kiện "chưa có dữ liệu"`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });
});
