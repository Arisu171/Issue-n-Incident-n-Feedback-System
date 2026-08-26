'use client';

import { useCallback, useRef, useState } from 'react';

/**
 * Ba trạng thái bắt buộc của mọi thao tác người dùng, theo yêu cầu kỹ thuật
 * "Tất cả events 3 status".
 *
 * `idle` không phải trạng thái thứ tư của một thao tác — nó chỉ mô tả lúc chưa có thao tác nào
 * xảy ra, tức là chưa có event để mà gắn trạng thái.
 */
export type ActionStatus = 'idle' | 'processing' | 'success' | 'failed';

/**
 * Phần chỉ đọc của một thao tác. Component hiển thị chỉ cần bấy nhiêu, và vì không tham chiếu
 * tới kiểu trả về T nên nhận được ActionState của bất kỳ thao tác nào.
 */
export interface ActionView {
  status: ActionStatus;
  message: string | null;
  error: unknown;
  isProcessing: boolean;
}

export interface ActionState<T = unknown> extends ActionView {
  status: ActionStatus;
  /** Thông báo hiển thị khi thành công. */
  message: string | null;
  /** Lỗi khi thất bại — giữ nguyên ApiError để hiển thị allowedNextStatus và correlationId. */
  error: unknown;
  /** true trong lúc đang chạy; dùng để disable nút và tránh double-submit. */
  isProcessing: boolean;
  run: (action: () => Promise<T>, successMessage?: string) => Promise<T | undefined>;
  reset: () => void;
}

export interface UseActionOptions {
  /**
   * Chế độ cho lượt TẢI dữ liệu: lượt gọi mới thay thế lượt đang chạy thay vì bị bỏ qua,
   * và chỉ kết quả của lượt mới nhất được áp — kết quả cũ về muộn bị vứt.
   *
   * Mặc định (false) dành cho thao tác GHI: bấm hai lần thì lần thứ hai bị bỏ qua để không
   * gửi hai request. Nhưng với lượt tải, "bỏ qua" nghĩa là thay đổi bộ lọc rơi đúng lúc đang
   * tải sẽ bị nuốt không dấu vết và bảng hiển thị dữ liệu của bộ lọc cũ.
   */
  latest?: boolean;
}

/**
 * Gom ba trạng thái của một thao tác về một chỗ.
 *
 * Trước đây mỗi màn hình tự quản lý cặp busy/notice/error riêng, và thực tế đã có 5 trên 9 thao
 * tác bị thiếu ít nhất một trạng thái. Đi qua hook này thì một thao tác không thể vô tình thiếu
 * trạng thái nào, vì cả ba đều do cùng một chỗ đặt.
 */
export function useAction<T = unknown>(options?: UseActionOptions): ActionState<T> {
  const [status, setStatus] = useState<ActionStatus>('idle');
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const latest = options?.latest ?? false;

  // Chặn chạy chồng: người dùng bấm hai lần thì lần thứ hai bị bỏ qua thay vì gửi hai request.
  const running = useRef(false);

  // Đánh số lượt chạy để chế độ latest biết kết quả nào còn hiện hành.
  const seq = useRef(0);

  const run = useCallback(async (action: () => Promise<T>, successMessage?: string) => {
    if (running.current && !latest) {
      return undefined;
    }

    const id = ++seq.current;
    running.current = true;
    setStatus('processing');
    setMessage(null);
    setError(null);

    try {
      const result = await action();
      if (id !== seq.current) {
        return undefined; // Đã có lượt mới hơn — kết quả này không còn giá trị.
      }
      setStatus('success');
      setMessage(successMessage ?? null);
      return result;
    } catch (err) {
      if (id !== seq.current) {
        return undefined;
      }
      setStatus('failed');
      setError(err);
      return undefined;
    } finally {
      if (id === seq.current) {
        running.current = false;
      }
    }
  }, [latest]);

  const reset = useCallback(() => {
    setStatus('idle');
    setMessage(null);
    setError(null);
  }, []);

  return {
    status,
    message,
    error,
    isProcessing: status === 'processing',
    run,
    reset,
  };
}
