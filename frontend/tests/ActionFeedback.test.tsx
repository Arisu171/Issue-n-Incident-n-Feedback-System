import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ActionFeedback, SubmitButton } from '@/components/ui';
import { ApiError } from '@/lib/api';
import type { ActionView } from '@/lib/useAction';

function view(overrides: Partial<ActionView>): ActionView {
  return {
    status: 'idle',
    message: null,
    error: null,
    isProcessing: false,
    ...overrides,
  };
}

describe('ActionFeedback — người dùng thấy gì ở mỗi trạng thái', () => {
  it('idle thì không hiện gì cả', () => {
    const { container } = render(<ActionFeedback action={view({})} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('processing hiện nhãn đang xử lý và thông báo cho trình đọc màn hình', () => {
    render(<ActionFeedback action={view({ status: 'processing', isProcessing: true })} />);

    const alert = screen.getByRole('status');
    expect(alert).toHaveTextContent('Đang xử lý…');
    expect(alert).toHaveAttribute('aria-live', 'polite');
  });

  it('processing dùng được nhãn riêng của từng thao tác', () => {
    render(
      <ActionFeedback
        action={view({ status: 'processing', isProcessing: true })}
        processingLabel="Đang gán người xử lý…"
      />,
    );

    expect(screen.getByRole('status')).toHaveTextContent('Đang gán người xử lý…');
  });

  it('success hiện đúng thông báo đã truyền vào', () => {
    render(<ActionFeedback action={view({ status: 'success', message: 'Đã gán người xử lý.' })} />);

    expect(screen.getByRole('status')).toHaveTextContent('Đã gán người xử lý.');
  });

  it('failed hiện chi tiết lỗi từ ProblemDetails', () => {
    const error = new ApiError(409, 'Xung đột trạng thái', 'Không thể chuyển từ Investigating sang Resolved.', {
      correlationId: 'abc123',
    });

    render(<ActionFeedback action={view({ status: 'failed', error })} />);

    expect(screen.getByText(/Không thể chuyển từ Investigating sang Resolved/)).toBeInTheDocument();
    expect(screen.getByText(/abc123/)).toBeInTheDocument();
  });

  /** NFR-USE-01 — lỗi 409 phải nói rõ bước hợp lệ tiếp theo, không chỉ báo "thất bại". */
  it('failed với lỗi 409 hiện luôn bước hợp lệ tiếp theo', () => {
    const error = new ApiError(409, 'Xung đột trạng thái', 'Không thể chuyển.', {
      allowedNextStatus: 'Mitigating',
      correlationId: 'xyz789',
    });

    render(<ActionFeedback action={view({ status: 'failed', error })} />);

    expect(screen.getByText(/Bước hợp lệ tiếp theo/)).toBeInTheDocument();
    expect(screen.getByText('Đang khắc phục')).toBeInTheDocument();
  });
});

describe('SubmitButton — nút tự phản ánh trạng thái', () => {
  it('bình thường thì hiện nhãn gốc và bấm được', () => {
    render(
      <SubmitButton action={view({})}>
        Tạo sự cố
      </SubmitButton>,
    );

    const button = screen.getByRole('button', { name: 'Tạo sự cố' });
    expect(button).toBeEnabled();
    expect(button).toHaveAttribute('aria-busy', 'false');
  });

  it('processing thì đổi nhãn, tự khóa và đánh dấu aria-busy', () => {
    render(
      <SubmitButton
        action={view({ status: 'processing', isProcessing: true })}
        processingLabel="Đang tạo…"
      >
        Tạo sự cố
      </SubmitButton>,
    );

    const button = screen.getByRole('button');
    expect(button).toHaveTextContent('Đang tạo…');
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('aria-busy', 'true');
  });

  it('vẫn khóa được vì lý do nghiệp vụ khi không ở trạng thái processing', () => {
    render(
      <SubmitButton action={view({})} disabled>
        Gán
      </SubmitButton>,
    );

    expect(screen.getByRole('button')).toBeDisabled();
  });
});

/**
 * Thẻ thông báo nổi.
 *
 * Điều đáng khoá không phải "có hiện chữ không" — mấy test trên đã lo — mà là **nó nổi ra ngoài
 * luồng trang**, và **lỗi thì không tự biến mất** trong khi thông báo thành công thì có.
 */
describe('Thẻ thông báo nổi', () => {
  afterEach(() => {
    vi.useRealTimers();
    document.getElementById('toast-layer')?.remove();
  });

  it('nổi ra lớp riêng chứ không chèn vào chỗ đặt component', () => {
    const { container } = render(<ActionFeedback action={view({ status: 'success', message: 'Đã lưu.' })} />);

    // Không có gì ở chỗ trang đặt nó — nên nó không đẩy nội dung bên dưới xuống.
    expect(container).toBeEmptyDOMElement();

    const layer = document.getElementById('toast-layer');
    expect(layer).not.toBeNull();
    expect(layer).toHaveTextContent('Đã lưu.');
  });

  it('thông báo thành công tự biến mất', () => {
    vi.useFakeTimers();
    render(<ActionFeedback action={view({ status: 'success', message: 'Đã lưu.' })} />);
    expect(screen.getByRole('status')).toHaveTextContent('Đã lưu.');

    act(() => { vi.advanceTimersByTime(5000); });

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('lỗi thì ở lại cho tới khi người dùng tự đóng', () => {
    vi.useFakeTimers();
    render(<ActionFeedback action={view({ status: 'failed', error: new ApiError(500, 'Lỗi', 'Máy chủ hỏng', {}) })} />);

    act(() => { vi.advanceTimersByTime(30_000); });
    // Lỗi mang bước hợp lệ tiếp theo và correlationId — thứ người dùng cần đọc kỹ, có khi phải
    // chép lại. Tự tắt sau vài giây là lấy mất nó khỏi tay họ.
    expect(screen.getByRole('alert')).toHaveTextContent('Máy chủ hỏng');

    fireEvent.click(screen.getByRole('button', { name: 'Đóng' }));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('đang chạy thì không có nút đóng', () => {
    render(<ActionFeedback action={view({ status: 'processing', isProcessing: true })} />);

    // Tắt một thao tác chưa xong là giấu mất thứ duy nhất nói rằng nó vẫn đang chạy.
    expect(screen.queryByRole('button', { name: 'Đóng' })).toBeNull();
  });
});
