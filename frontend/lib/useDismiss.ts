'use client';

import { useEffect, type RefObject } from 'react';

/**
 * Đóng một lớp nổi khi bấm ra ngoài hoặc bấm Escape.
 *
 * Dùng `pointerdown` chứ không phải `click`: `click` chỉ bắn sau khi nhả chuột, nên nếu người
 * dùng bấm ra ngoài rồi kéo, lớp nổi vẫn mở. `pointerdown` cũng chạy **trước** `onClick` của
 * phần tử bên dưới, nên bấm vào một nút khác vừa đóng menu vừa kích hoạt nút đó trong một lần
 * bấm, thay vì phải bấm hai lần.
 *
 * Nút mở phải nằm **bên trong** `ref`, nếu không thì bấm vào nó sẽ vừa đóng (do pointerdown ra
 * ngoài) vừa mở lại (do onClick), thành ra không đóng được.
 *
 * Nhận được **nhiều ref** cho trường hợp lớp nổi được portal ra `document.body`: lúc đó danh sách
 * không còn là con của nút mở, nên nếu chỉ soát một ref thì bấm vào chính danh sách cũng bị tính
 * là "bấm ra ngoài" — lớp nổi đóng ngay ở `pointerdown` và `onClick` của mục không bao giờ chạy.
 */
export function useDismiss(
  ref: RefObject<HTMLElement | null> | readonly RefObject<HTMLElement | null>[],
  onDismiss: () => void,
  active = true,
): void {
  useEffect(() => {
    if (!active) return;

    const refs = Array.isArray(ref) ? ref : [ref as RefObject<HTMLElement | null>];

    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      const inside = refs.some((r) => r.current?.contains(target));
      // Chỉ đóng khi có ít nhất một ref đã gắn: lúc chưa gắn thì `inside` luôn false, và đóng
      // theo đó là đóng nhầm.
      if (!inside && refs.some((r) => r.current)) onDismiss();
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onDismiss();
    }

    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [ref, onDismiss, active]);
}
