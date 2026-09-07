import { act, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ReactionBar } from '@/components/tickets/Timeline';
import type { ReactionType } from '@/lib/tickets';

/**
 * Thả emoji là thao tác nhỏ nhất trong ứng dụng và gần như không bao giờ hỏng, nên nó không được
 * bắt người dùng chờ một vòng mạng rồi mới thấy kết quả.
 */
describe('ReactionBar — cập nhật lạc quan', () => {
  it('số và nền đổi ngay khi bấm, không chờ server', async () => {
    let resolve: ((v: { counts: Record<string, number>; viewerReactions: ReactionType[] }) => void) | null = null;
    const pending = new Promise<{ counts: Record<string, number>; viewerReactions: ReactionType[] }>((r) => { resolve = r; });

    render(<ReactionBar counts={{ THUMBS_UP: 2 }} mine={[]} onToggle={() => pending} />);

    const button = screen.getByRole('button', { name: /2/ });
    expect(button).not.toHaveClass('mine');

    fireEvent.click(button);

    // Server còn chưa trả lời — nhưng người dùng đã thấy kết quả.
    const optimistic = screen.getByRole('button', { name: /3/ });
    expect(optimistic).toHaveClass('mine');

    await act(async () => { resolve!({ counts: { THUMBS_UP: 9 }, viewerReactions: ['THUMBS_UP'] as ReactionType[] }); });

    // Và con số cuối cùng là con số của server, không phải phép cộng đoán ở trình duyệt: người
    // khác có thể vừa thả cùng lúc.
    expect(screen.getByRole('button', { name: /9/ })).toHaveClass('mine');
  });

  it('gửi hỏng thì trả nút về đúng chỗ cũ', async () => {
    let reject: ((e: unknown) => void) | null = null;
    const pending = new Promise<never>((_, r) => { reject = r; });

    render(<ReactionBar counts={{ THUMBS_UP: 2 }} mine={[]} onToggle={() => pending} />);
    fireEvent.click(screen.getByRole('button', { name: /2/ }));
    expect(screen.getByRole('button', { name: /3/ })).toBeInTheDocument();

    await act(async () => { reject!(new Error('mạng hỏng')); });

    // Để lại con số sai còn tệ hơn không đổi gì: người dùng tin vào nó.
    const back = screen.getByRole('button', { name: /2/ });
    expect(back).not.toHaveClass('mine');
  });
});
