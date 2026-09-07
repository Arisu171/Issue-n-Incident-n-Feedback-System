'use client';

import { useEffect, useRef, useState } from 'react';
import type { ActiveEditor, EditClaim } from '@/lib/api';

/**
 * Nhịp gia hạn chỗ giữ, tính bằng mili giây.
 *
 * Backend cho một chỗ giữ sống 120 giây (`EditClaims:TtlSeconds`). Gia hạn mỗi 45 giây nghĩa là
 * lỡ một nhịp — mạng chập, tab bị trình duyệt tiết kiệm pin — vẫn chưa mất chỗ. Đặt nhịp bằng
 * đúng nửa TTL thì một nhịp lỡ là mất chỗ ngay.
 */
const HEARTBEAT_MS = 45_000;

/**
 * Giữ chỗ sửa suốt thời gian form còn mở, và trả lại khi đóng.
 *
 * Trả về những người **khác** đang cùng mở form — danh sách khác rỗng nghĩa là lần lưu sắp tới
 * bắt buộc kèm `If-Match`, mà client này thì luôn gửi sẵn, nên phần hiển thị mới là phần có
 * ích: người dùng biết mình đang giẫm chân ai **trước khi** gõ xong cả đoạn văn.
 *
 * <b>Giữ chỗ hỏng thì không chặn việc sửa.</b> Mọi lỗi ở đây đều nuốt: chỗ giữ là tiện nghi,
 * không phải điều kiện để được sửa. Chặn form vì không đăng ký được chỗ là đổi một tính năng
 * phụ lấy tính năng chính.
 */
export function useEditClaim(
  active: boolean,
  key: string,
  claim: () => Promise<EditClaim>,
  release: () => Promise<unknown>,
): ActiveEditor[] {
  const [others, setOthers] = useState<ActiveEditor[]>([]);

  // Hai lời gọi là closure mới ở mỗi lần vẽ lại. Đưa vào ref rồi cho effect phụ thuộc đúng
  // `active` và `key`: để chúng trong mảng phụ thuộc sẽ khiến effect chạy lại mỗi lần vẽ, tức
  // là nhả rồi giữ lại chỗ liên tục.
  const claimRef = useRef(claim);
  const releaseRef = useRef(release);
  claimRef.current = claim;
  releaseRef.current = release;

  useEffect(() => {
    if (!active) {
      setOthers([]);
      return;
    }

    let cancelled = false;

    async function beat() {
      try {
        const result = await claimRef.current();
        if (!cancelled) setOthers(result.others);
      } catch {
        // Im lặng — xem chú thích của hook.
      }
    }

    void beat();
    const timer = setInterval(() => void beat(), HEARTBEAT_MS);

    return () => {
      cancelled = true;
      clearInterval(timer);
      setOthers([]);
      // Không chờ kết quả: người dùng đã đóng form, và chỗ giữ còn sót lại cũng chỉ sống tới
      // lúc hết hạn.
      void releaseRef.current().catch(() => undefined);
    };
  }, [active, key]);

  return others;
}
