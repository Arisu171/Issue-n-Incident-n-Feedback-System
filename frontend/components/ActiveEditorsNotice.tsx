'use client';

import type { ActiveEditor } from '@/lib/api';
import { tr } from '@/lib/i18n';

/**
 * "Có người khác đang mở form này."
 *
 * Không chặn gì cả — đúng tinh thần của chỗ giữ ở backend. Nó chỉ nói sớm điều mà người dùng
 * sẽ phải biết muộn: lưu xong có thể thấy nội dung của mình chồng lên nội dung người khác, hoặc
 * nhận 412 và phải gõ lại. Biết trước thì còn kịp đi hỏi nhau một câu.
 */
export function ActiveEditorsNotice({ editors }: { editors: ActiveEditor[] }) {
  if (editors.length === 0) return null;

  const names = editors.map((e) => e.user.displayName).join(', ');

  return (
    <div className="notice-inline" role="status">
      {tr('{v0} cũng đang mở form sửa mục này. Ai lưu sau sẽ phải tải lại nếu nội dung đã đổi.', { v0: names })}
    </div>
  );
}
