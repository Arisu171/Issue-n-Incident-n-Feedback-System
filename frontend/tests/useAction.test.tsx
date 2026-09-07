import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useAction } from '@/lib/useAction';

/**
 * Yêu cầu kỹ thuật "Tất cả events 3 status" — processing, success, failed.
 * Đây là nơi ba trạng thái đó được định nghĩa, nên cũng là nơi phải kiểm chứng chặt nhất.
 */
describe('useAction — ba trạng thái của một thao tác', () => {
  it('bắt đầu ở idle khi chưa có thao tác nào xảy ra', () => {
    const { result } = renderHook(() => useAction());

    expect(result.current.status).toBe('idle');
    expect(result.current.isProcessing).toBe(false);
    expect(result.current.message).toBeNull();
    expect(result.current.error).toBeNull();
  });

  it('đi qua processing rồi tới success khi thao tác thành công', async () => {
    let resolveAction: (value: string) => void = () => {};
    const pending = new Promise<string>((resolve) => {
      resolveAction = resolve;
    });

    const { result } = renderHook(() => useAction<string>());

    let runPromise: Promise<string | undefined>;
    act(() => {
      runPromise = result.current.run(() => pending, 'Đã lưu.');
    });

    // Trạng thái 1: đang xử lý.
    await waitFor(() => expect(result.current.status).toBe('processing'));
    expect(result.current.isProcessing).toBe(true);

    await act(async () => {
      resolveAction('xong');
      await runPromise;
    });

    // Trạng thái 2: thành công.
    expect(result.current.status).toBe('success');
    expect(result.current.isProcessing).toBe(false);
    expect(result.current.message).toBe('Đã lưu.');
    expect(result.current.error).toBeNull();
  });

  it('đi qua processing rồi tới failed khi thao tác ném lỗi', async () => {
    const { result } = renderHook(() => useAction());
    const boom = new Error('API trả 409');

    await act(async () => {
      await result.current.run(() => Promise.reject(boom), 'Không bao giờ hiện');
    });

    // Trạng thái 3: thất bại.
    expect(result.current.status).toBe('failed');
    expect(result.current.isProcessing).toBe(false);
    expect(result.current.error).toBe(boom);
    expect(result.current.message).toBeNull();
  });

  it('trả về giá trị của thao tác khi thành công và undefined khi thất bại', async () => {
    const { result } = renderHook(() => useAction<number>());

    let ok: number | undefined;
    await act(async () => {
      ok = await result.current.run(() => Promise.resolve(42));
    });
    expect(ok).toBe(42);

    let failed: number | undefined;
    await act(async () => {
      failed = await result.current.run(() => Promise.reject(new Error('lỗi')));
    });
    // Nhờ vậy màn hình chỉ điều hướng hoặc reset form khi thao tác thực sự thành công.
    expect(failed).toBeUndefined();
  });

  it('không nuốt lỗi: thao tác thất bại không làm hỏng lần chạy kế tiếp', async () => {
    const { result } = renderHook(() => useAction<string>());

    await act(async () => {
      await result.current.run(() => Promise.reject(new Error('lỗi tạm thời')));
    });
    expect(result.current.status).toBe('failed');

    await act(async () => {
      await result.current.run(() => Promise.resolve('ok'), 'Lần hai thành công.');
    });
    expect(result.current.status).toBe('success');
    expect(result.current.error).toBeNull();
  });

  it('chặn chạy chồng: bấm hai lần chỉ gửi một request', async () => {
    let resolveAction: (value: string) => void = () => {};
    const pending = new Promise<string>((resolve) => {
      resolveAction = resolve;
    });
    const action = vi.fn(() => pending);

    const { result } = renderHook(() => useAction<string>());

    let first: Promise<string | undefined>;
    let second: Promise<string | undefined>;
    act(() => {
      first = result.current.run(action);
      second = result.current.run(action);
    });

    await act(async () => {
      resolveAction('xong');
      await Promise.all([first, second]);
    });

    // Quan trọng với PATCH /status: lần gọi thứ hai sẽ bị API từ chối bằng 409 dù lần đầu
    // đã thành công, và người dùng sẽ tưởng thao tác của mình hỏng.
    expect(action).toHaveBeenCalledTimes(1);
  });

  it('thao tác thành công trả về null (API 204) vẫn phân biệt được với thất bại', async () => {
    const { result } = renderHook(() => useAction<null>());

    let ok: null | undefined;
    await act(async () => {
      ok = await result.current.run(() => Promise.resolve(null), 'Đã xóa.');
    });

    // Hợp đồng với lib/api.ts: 204 trả null chứ không phải undefined, vì call site dùng
    // `ok !== undefined` để quyết định điều hướng/refresh sau khi thao tác thành công.
    expect(ok).toBeNull();
    expect(ok !== undefined).toBe(true);
    expect(result.current.status).toBe('success');
  });

  it('chế độ latest: lượt gọi mới thay thế lượt đang chạy, kết quả cũ về muộn bị bỏ', async () => {
    let resolveFirst: (value: string) => void = () => {};
    const first = new Promise<string>((resolve) => {
      resolveFirst = resolve;
    });

    const { result } = renderHook(() => useAction<string>({ latest: true }));

    let firstRun: Promise<string | undefined>;
    let secondRun: Promise<string | undefined>;
    act(() => {
      firstRun = result.current.run(() => first);
    });
    await waitFor(() => expect(result.current.status).toBe('processing'));

    // Khác chế độ mặc định: lượt thứ hai KHÔNG bị bỏ qua — nó thay thế lượt thứ nhất.
    act(() => {
      secondRun = result.current.run(() => Promise.resolve('bộ lọc mới'));
    });

    await act(async () => {
      expect(await secondRun).toBe('bộ lọc mới');
      resolveFirst('bộ lọc cũ');
      expect(await firstRun).toBeUndefined();
    });

    expect(result.current.status).toBe('success');
  });

  it('reset đưa thao tác về idle', async () => {
    const { result } = renderHook(() => useAction<string>());

    await act(async () => {
      await result.current.run(() => Promise.resolve('ok'), 'Xong.');
    });
    expect(result.current.status).toBe('success');

    act(() => result.current.reset());

    expect(result.current.status).toBe('idle');
    expect(result.current.message).toBeNull();
    expect(result.current.error).toBeNull();
  });

  it('xóa thông báo cũ ngay khi bắt đầu lượt chạy mới', async () => {
    const { result } = renderHook(() => useAction<string>());

    await act(async () => {
      await result.current.run(() => Promise.resolve('ok'), 'Thông báo cũ.');
    });
    expect(result.current.message).toBe('Thông báo cũ.');

    let resolveAction: (value: string) => void = () => {};
    const pending = new Promise<string>((resolve) => {
      resolveAction = resolve;
    });

    let runPromise: Promise<string | undefined>;
    act(() => {
      runPromise = result.current.run(() => pending);
    });

    // Không được để thông báo thành công của lượt trước còn treo trong lúc lượt sau đang chạy.
    await waitFor(() => expect(result.current.status).toBe('processing'));
    expect(result.current.message).toBeNull();

    await act(async () => {
      resolveAction('xong');
      await runPromise;
    });
  });
});
