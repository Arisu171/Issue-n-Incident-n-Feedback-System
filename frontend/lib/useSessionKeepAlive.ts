'use client';

import { useEffect } from 'react';
import { api, session } from '@/lib/api';

/** Cấp lại token khi còn dưới ngần này thời gian là hết hạn. */
const RENEW_BEFORE_MS = 5 * 60 * 1000;
/** Nhịp kiểm tra. Không cần dày hơn: cửa sổ cấp lại rộng 5 phút. */
const CHECK_EVERY_MS = 60 * 1000;
/** Coi là "còn dùng" nếu có thao tác trong khoảng này. */
const ACTIVE_WITHIN_MS = 10 * 60 * 1000;

/**
 * Giữ phiên sống theo **hoạt động thật** của người dùng.
 *
 * Token vẫn ngắn hạn — đó là cửa sổ sống của một token bị lộ, và hệ thống chưa có danh sách thu
 * hồi nên không nên nới. Thay vì kéo dài token, phiên được cấp lại khi hai điều cùng đúng: token
 * sắp hết hạn, và người dùng có thao tác gần đây. Ai đóng máy đi họp thì phiên tự hết như cũ.
 *
 * Trần tuyệt đối do máy chủ giữ (`JwtOptions.SessionMaxHours`), không phải ở đây — client không
 * phải là chỗ đáng tin để quyết định khi nào một phiên phải kết thúc.
 */
export function useSessionKeepAlive(): void {
  useEffect(() => {
    if (!session.token()) return;

    let lastActivity = Date.now();
    let renewing = false;

    const markActive = () => { lastActivity = Date.now(); };
    // `passive` để không chặn cuộn. Vẫn không dùng `mousemove`: chuột nhích một chút không có
    // nghĩa là đang làm việc.
    //
    // `wheel` và `scroll` là bắt buộc, không phải cho đủ bộ. Thiếu chúng thì **đọc** không được
    // tính là dùng: người xem một sự cố dài bằng con lăn suốt mười phút, không bấm cũng không
    // gõ, bị xếp vào loại đã bỏ đi — token không được cấp lại, và cú bấm kế tiếp nhận 401 giữa
    // lúc đang làm việc. Đọc là cách dùng chính của phần mềm này, không phải trạng thái nghỉ.
    const events: (keyof WindowEventMap)[] = ['pointerdown', 'keydown', 'focus', 'wheel', 'scroll'];
    for (const name of events) window.addEventListener(name, markActive, { passive: true });

    async function tick() {
      if (renewing) return;
      const token = session.token();
      const expiresAt = session.expiresAt();
      if (!token || expiresAt === null) return;

      const timeLeft = expiresAt - Date.now();
      const active = Date.now() - lastActivity < ACTIVE_WITHIN_MS;
      if (timeLeft > RENEW_BEFORE_MS || !active) return;

      renewing = true;
      try {
        // Thất bại thì không tự đăng xuất ở đây: request kế tiếp nhận 401 và lớp `request`
        // đã lo việc đưa về màn đăng nhập. Làm hai nơi cùng một việc chỉ gây tranh nhau.
        await api.refreshSession();
      } finally {
        renewing = false;
      }
    }

    const timer = window.setInterval(() => { void tick(); }, CHECK_EVERY_MS);
    void tick();

    return () => {
      window.clearInterval(timer);
      for (const name of events) window.removeEventListener(name, markActive);
    };
  }, []);
}
