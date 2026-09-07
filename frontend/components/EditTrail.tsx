'use client';

import { useState } from 'react';
import { formatTime, type EditSignature, type Revision } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ErrorBox } from '@/components/ui';
import { tr } from '@/lib/i18n';

/**
 * Tên trường trong lịch sử là tên **cột** (`customer_email`), vì đó là thứ backend ghi và là
 * thứ tra ngược lại được khi có tranh cãi. Bảng này chỉ dịch sang tiếng người cho màn hình;
 * trường lạ thì hiện nguyên tên cột chứ không giấu đi.
 */
const FIELD_LABEL: Record<string, string> = {
  title: 'Tiêu đề',
  description: 'Mô tả',
  severity: 'Mức độ',
  body: 'Nội dung',
  content: 'Nội dung',
  customer_email: 'Email liên hệ',
  channel: 'Kênh',
};

function fieldLabel(field: string) {
  return FIELD_LABEL[field] ? tr(FIELD_LABEL[field]) : field;
}

/**
 * Nhãn "đã sửa" kèm đường mở lịch sử đầy đủ.
 *
 * Không hiện gì khi bản ghi còn nguyên bản: người đọc chỉ cần biết đến chuyện chỉnh sửa khi nó
 * đã thực sự xảy ra.
 *
 * Lịch sử tải **lười** — chỉ khi có người bấm mở. Một trang chi tiết có hàng chục bình luận,
 * nạp sẵn lịch sử của tất cả là hàng chục request cho thứ hầu như không ai mở.
 */
export function EditTrail({
  lastEdit,
  loadRevisions,
}: {
  lastEdit: EditSignature | null;
  loadRevisions: () => Promise<Revision[]>;
}) {
  const [open, setOpen] = useState(false);
  const [rows, setRows] = useState<Revision[] | null>(null);

  // Tên `load` là bắt buộc, không phải tùy hứng: contract test ba trạng thái miễn thông báo
  // thành công cho đúng biến tên này, vì một lượt TẢI thì chính dữ liệu hiện ra đã là phản hồi.
  const load = useAction<Revision[]>({ latest: true });

  if (!lastEdit) return null;

  async function toggle() {
    if (open) {
      setOpen(false);
      return;
    }

    setOpen(true);

    // Tải một lần rồi giữ lại: đóng mở qua lại không phải là lý do để hỏi lại máy chủ.
    if (!rows) {
      const result = await load.run(loadRevisions);
      if (result) setRows(result);
    }
  }

  return (
    <>
      <span className="muted">
        {' · '}
        {tr('đã sửa bởi {v0} lúc {v1}', { v0: lastEdit.by.displayName, v1: formatTime(lastEdit.at) })}{' '}
        <button type="button" className="ghost small" onClick={toggle}>
          {open ? tr('ẩn lịch sử') : tr('xem lịch sử')}
        </button>
      </span>

      {open && (
        <div className="edit-trail">
          {load.status === 'failed' && <ErrorBox error={load.error} />}
          {load.isProcessing && !rows && <div className="empty">{tr('Đang tải lịch sử sửa…')}</div>}
          {rows && rows.length === 0 && <div className="empty">{tr('Chưa có lần sửa nào.')}</div>}
          {rows && rows.length > 0 && (
            <ul className="timeline">
              {rows.map((r) => (
                <li key={r.id}>
                  <div>
                    <strong>{fieldLabel(r.field)}</strong>{' '}
                    <span className="muted">
                      · {r.editedBy.displayName} · {formatTime(r.editedAt)}
                    </span>{' '}
                    {/* Tác giả tự sửa bài mình và người khác sửa hộ là hai việc khác nhau với
                        người đọc, nên chúng phải nhìn khác nhau chứ không cùng một dòng xám. */}
                    {r.onBehalf && (
                      <span className="badge fb-Acknowledged">{tr('sửa hộ')}</span>
                    )}
                  </div>
                  <div className="revision-diff">
                    <del>{r.oldValue || <span className="muted">{tr('(trống)')}</span>}</del>
                    <ins>{r.newValue || <span className="muted">{tr('(trống)')}</span>}</ins>
                  </div>
                  {r.reason && (
                    <div className="muted">{tr('Lý do: {v0}', { v0: r.reason })}</div>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </>
  );
}
