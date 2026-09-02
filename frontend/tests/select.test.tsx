import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Select } from '@/components/ui';

/**
 * Danh sách của `Select` được portal ra `document.body` để không tổ tiên nào có `overflow` cắt
 * được nó — trước đây `.table-wrap { overflow-x: auto }` cắt mất danh sách trong mọi bảng.
 *
 * Việc portal kéo theo một cái bẫy: danh sách không còn là con của ô, nên bộ "đóng khi bấm ra
 * ngoài" tính luôn cả cú bấm vào một mục là bấm ra ngoài. Nó đóng ngay ở `pointerdown` và
 * `onClick` của mục không bao giờ chạy — ô chọn trông như hỏng. Test cuối cùng khoá đúng chỗ đó.
 */

const OPTIONS = [
  { value: 'p1', label: 'Ưu tiên 1' },
  { value: 'p2', label: 'Ưu tiên 2' },
];

function open(ariaLabel = 'Mức độ') {
  fireEvent.click(screen.getByLabelText(ariaLabel));
  return screen.getByRole('listbox');
}

describe('Select', () => {
  it('đóng thì không có listbox nào trong tài liệu', () => {
    render(<Select value="" onChange={() => undefined} options={OPTIONS} ariaLabel="Mức độ" />);
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('mở ra thì danh sách nằm ngoài ô, ngay dưới body', () => {
    const { container } = render(
      <Select value="" onChange={() => undefined} options={OPTIONS} ariaLabel="Mức độ" />,
    );
    const list = open();

    // Chính là điều khiến overflow của tổ tiên không cắt được nó nữa: danh sách là con trực tiếp
    // của body, không còn tổ tiên nào giữa nó và gốc tài liệu để mà cắt.
    expect(container.contains(list)).toBe(false);
    expect(list.parentElement).toBe(document.body);

    // Toạ độ do JS đặt inline (`position: fixed` nằm trong globals.css, không nạp ở jsdom nên
    // không kiểm được ở đây). Có `maxHeight` nghĩa là bước đo đã chạy.
    expect(list.style.maxHeight).not.toBe('');
    expect(within(list).getAllByRole('option')).toHaveLength(2);
  });

  it('bấm vào một mục vẫn gọi onChange dù danh sách nằm ngoài ô', () => {
    const onChange = vi.fn();
    render(<Select value="" onChange={onChange} options={OPTIONS} ariaLabel="Mức độ" />);
    const list = open();

    // pointerdown chạy trước click; nếu bộ dismiss coi đây là "bấm ra ngoài" thì danh sách biến
    // mất trước khi click kịp tới mục, và onChange không bao giờ được gọi.
    const option = within(list).getByRole('option', { name: 'Ưu tiên 2' });
    fireEvent.pointerDown(option);
    fireEvent.click(option);

    expect(onChange).toHaveBeenCalledWith('p2');
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('bấm ra ngoài thì đóng', () => {
    render(
      <div>
        <Select value="" onChange={() => undefined} options={OPTIONS} ariaLabel="Mức độ" />
        <button type="button">chỗ khác</button>
      </div>,
    );
    open();
    fireEvent.pointerDown(screen.getByRole('button', { name: 'chỗ khác' }));
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('nút mở khai báo trạng thái và trỏ tới danh sách cho trình đọc màn hình', () => {
    render(<Select value="" onChange={() => undefined} options={OPTIONS} ariaLabel="Mức độ" />);
    const trigger = screen.getByLabelText('Mức độ');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(trigger).not.toHaveAttribute('aria-controls');

    const list = open();
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(trigger.getAttribute('aria-controls')).toBe(list.id);
  });

  it('mở ra thì con trỏ nhảy vào mục đang chọn, để bấm mũi tên đi tiếp được ngay', () => {
    render(<Select value="p2" onChange={() => undefined} options={OPTIONS} ariaLabel="Mức độ" />);
    const list = open();
    expect(document.activeElement).toBe(within(list).getByRole('option', { name: 'Ưu tiên 2' }));
  });

  it('mục đang chọn được đánh dấu', () => {
    render(<Select value="p2" onChange={() => undefined} options={OPTIONS} ariaLabel="Mức độ" />);
    const list = open();
    expect(within(list).getByRole('option', { name: 'Ưu tiên 2' })).toHaveAttribute('aria-selected', 'true');
    expect(within(list).getByRole('option', { name: 'Ưu tiên 1' })).toHaveAttribute('aria-selected', 'false');
  });
});
