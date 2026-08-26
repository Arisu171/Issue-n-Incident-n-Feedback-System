'use client';

import { useEffect, useState } from 'react';
import { session } from '@/lib/api';
import { tickets } from '@/lib/tickets';
import { tr } from '@/lib/i18n';

/** Mục ảo cho dữ liệu chưa gắn project nào (`ProjectId IS NULL` ở server). */
export const UNCATEGORIZED = 'uncategorized';

export interface TransferTarget {
  value: string;
  label: string;
}

/**
 * Luật lọc, tách riêng khỏi React để test thẳng bằng dữ liệu chứ không phải dựng component.
 *
 * Hai thứ bị loại: project **đang đứng** (chuyển tới chính nó là thao tác rỗng), và mục chưa
 * phân loại khi đang đứng ở đó. Đây là luật chung của dự án — ô chọn không bày ra lựa chọn mà
 * cuối cùng không chọn được.
 */
export function transferTargets(
  projects: readonly { slug: string; name: string }[],
  current: string,
): TransferTarget[] {
  return [
    ...projects.filter((p) => p.slug !== current).map((p) => ({ value: p.slug, label: p.name })),
    ...(current === UNCATEGORIZED ? [] : [{ value: UNCATEGORIZED, label: tr('chưa phân loại') }]),
  ];
}

/**
 * Danh sách project có thể chuyển tới, đã bỏ sẵn những lựa chọn không dẫn tới đâu.
 *
 * Trả về mảng rỗng cho người không có quyền phân loại, nên nơi gọi chỉ cần kiểm độ dài thay vì
 * lặp lại phép kiểm quyền.
 *
 * Đây là tất cả phần dùng chung giữa hai chỗ đặt. **Hình dạng của ô chọn thì không dùng chung**:
 * màn hình sự cố đặt nó trong cột phải cùng khuôn với khối "Gán người xử lý", màn hình phản hồi
 * đặt ngay trong hàng cạnh ô "Gắn vào sự cố" — mỗi nơi theo khuôn sẵn có của nó, vì `uncategorized`
 * không phải một màn hình riêng cần kiểu điều khiển riêng.
 */
export function useTransferTargets(current: string): TransferTarget[] {
  const canClassify = session.can('project.member.manage');
  const [targets, setTargets] = useState<TransferTarget[]>([]);

  useEffect(() => {
    if (!canClassify) {
      setTargets([]);
      return;
    }
    let cancelled = false;
    void (async () => {
      try {
        const list = await tickets.projects();
        if (cancelled) return;
        setTargets(transferTargets(list, current));
      } catch {
        /* không đọc được danh sách project thì cũng không phân loại được */
      }
    })();
    return () => { cancelled = true; };
  }, [canClassify, current]);

  return targets;
}
