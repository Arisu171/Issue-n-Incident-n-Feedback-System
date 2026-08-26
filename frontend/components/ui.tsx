'use client';

import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import Link from 'next/link';
import { useDismiss } from '@/lib/useDismiss';
import type { ActionView } from '@/lib/useAction';
import { useRouter } from 'next/navigation';
import {
  ApiError,
  FEEDBACK_STATUS_LABEL,
  SEVERITY_LABEL,
  STATUS_LABEL,
  session,
  type FeedbackStatus,
  type IncidentSeverity,
  type IncidentStatus,
  type SlaStatus,
} from '@/lib/api';
import { tr } from '@/lib/i18n';

/**
 * `useLayoutEffect` phía server không chạy được và React sẽ cảnh báo mỗi lần render. Component
 * client vẫn được Next render sẵn trên server, nên đổi sang `useEffect` ở đó — nơi không có bố
 * cục để đo thì cũng không có gì để làm sớm.
 */
const useIsoLayoutEffect = typeof window === 'undefined' ? useEffect : useLayoutEffect;

export function StatusBadge({ status }: { status: IncidentStatus }) {
  return <span className={`badge ${status}`}>{tr(STATUS_LABEL[status])}</span>;
}

/** Vòng đời tiếp nhận của phản hồi — cho khách hàng thấy ngay "đã có ai đọc chưa, đã ai trả lời chưa". */
export function FeedbackStatusBadge({ status }: { status: FeedbackStatus }) {
  return <span className={`badge fb-${status}`}>{tr(FEEDBACK_STATUS_LABEL[status])}</span>;
}

export function SeverityBadge({ severity }: { severity: IncidentSeverity }) {
  return <span className={`badge sev-${severity}`}>{tr(SEVERITY_LABEL[severity])}</span>;
}

/**
 * Cảnh báo SLA của mục 6.9. Ngưỡng do API tính, giao diện chỉ hiển thị — nhờ vậy màn hình,
 * bộ lọc và job cảnh báo nền luôn dùng chung một định nghĩa.
 */
export function SlaBadge({ sla }: { sla: SlaStatus | null }) {
  if (!sla) return <span className="muted">—</span>;

  if (sla.breached) {
    return (
      <span className="badge sla-breached" title={tr('Ngưỡng {v0} giờ', { v0: sla.thresholdHours })}>
        {tr('Quá hạn {v0}h', { v0: Math.floor(sla.elapsedHours) })}
      </span>
    );
  }

  return (
    <span className="badge sla-ok" title={tr('Ngưỡng {v0} giờ', { v0: sla.thresholdHours })}>
      {tr('Còn {v0}h', { v0: Math.max(0, Math.floor(sla.remainingHours ?? 0)) })}
    </span>
  );
}

/**
 * Hiển thị đủ ba trạng thái của một thao tác theo yêu cầu "Tất cả events 3 status":
 * đang xử lý, thành công, thất bại. Dùng chung cho mọi màn hình để không màn nào
 * vô tình thiếu một trạng thái.
 */
/**
 * Đầu trang dùng chung. Trước đây mỗi trang tự dựng phần tiêu đề bằng flex nội tuyến nên
 * kicker, cỡ chữ, lề và vị trí cụm nút mỗi nơi một kiểu. Lấy màn hình Issues làm chuẩn:
 * kicker nhỏ in hoa màu accent, tiêu đề h2, mô tả tùy chọn, cụm hành động dạt phải.
 */
/**
 * Dấu hiệu trường bắt buộc. Một chỗ duy nhất để sau này đổi kiểu hiển thị.
 *
 * `aria-hidden` vì thuộc tính `required` trên chính ô nhập đã báo cho trình đọc màn hình rồi;
 * đọc thêm "sao" chỉ là nhiễu.
 */
export function RequiredMark() {
  return <span className="req" aria-hidden="true"> *</span>;
}

/**
 * Đầu trang: kicker → tiêu đề → chú thích.
 *
 * `kicker` **bắt buộc**, và nằm sát ngay trên tiêu đề — không chèn được gì vào giữa.
 */
export function PageHead({
  kicker, title, hint, actions,
}: {
  kicker: React.ReactNode; title: React.ReactNode; hint?: React.ReactNode; actions?: React.ReactNode;
}) {
  return (
    <div className="page-head">
      <div className="page-head-text">
        <div className="kicker">{kicker}</div>
        <h2>{title}</h2>
        {hint ? <p>{hint}</p> : null}
      </div>
      {actions ? <div className="page-head-actions">{actions}</div> : null}
    </div>
  );
}

/** Bao nhiêu lâu thì thẻ báo thành công tự biến mất. Lỗi thì không tự tắt. */
const TOAST_MS = 4000;

/**
 * Thông báo kết quả thao tác, **nổi trên màn hình** thay vì chèn vào giữa trang.
 *
 * Vì sao không để trong luồng trang nữa: thẻ báo chen vào giữa nội dung sẽ đẩy mọi thứ bên dưới
 * xuống đúng lúc người dùng vừa bấm, nên thứ họ định bấm tiếp đã dịch chỗ. Nó cũng thường nằm
 * ngoài tầm nhìn khi thao tác xảy ra ở cuối một bảng dài — người dùng bấm xong không thấy gì và
 * tưởng hỏng.
 *
 * Thẻ nằm trong một lớp `position: fixed` dùng chung, nên nhiều thao tác trên cùng màn hình xếp
 * chồng lên nhau thay vì tranh chỗ.
 *
 * Cách dùng ở các trang **không đổi** — vẫn là `<ActionFeedback action={...} />` đặt ở đâu cũng
 * được, vì nội dung được cổng ra ngoài. Nhờ vậy không phải sửa một dòng nào ở 20 màn hình.
 */
export function ActionFeedback({
  action,
  processingLabel = tr('Đang xử lý…'),
}: {
  action: ActionView;
  processingLabel?: string;
}) {
  const [dismissed, setDismissed] = useState(false);
  // Cổng chỉ mở sau khi đã gắn vào DOM: `document` không tồn tại lúc máy chủ dựng trang.
  const [mounted, setMounted] = useState(false);
  useEffect(() => { setMounted(true); }, []);

  // Mỗi lần thao tác đổi trạng thái là một thông báo mới — thẻ vừa tắt tay không được che mất nó.
  useEffect(() => { setDismissed(false); }, [action.status, action.message]);

  // Thành công thì tự tắt; **thất bại thì không**, vì lỗi còn mang bước hợp lệ tiếp theo và
  // correlationId — những thứ người dùng cần đọc kỹ và có khi phải chép lại.
  useEffect(() => {
    if (action.status !== 'success') return;
    const timer = setTimeout(() => setDismissed(true), TOAST_MS);
    return () => clearTimeout(timer);
  }, [action.status, action.message]);

  if (!mounted || dismissed) return null;

  let body: React.ReactNode = null;
  let tone = 'info';
  let role: 'status' | 'alert' = 'status';

  if (action.status === 'processing') {
    body = <><span className="spinner" aria-hidden="true" /> {processingLabel}</>;
  } else if (action.status === 'success' && action.message) {
    tone = 'success';
    body = <>✓ {action.message}</>;
  } else if (action.status === 'failed') {
    tone = 'error';
    role = 'alert';
    body = <ErrorMessage error={action.error} />;
  }

  if (body === null) return null;

  return createPortal(
    <div className={`alert ${tone} toast`} role={role} aria-live="polite">
      <div className="toast-body">{body}</div>
      {/* Đang chạy thì không cho tắt: tắt một thao tác chưa xong là giấu mất thứ duy nhất nói
          rằng nó vẫn đang chạy. */}
      {action.status !== 'processing' && (
        <button type="button" className="toast-close" aria-label={tr('Đóng')} onClick={() => setDismissed(true)}>×</button>
      )}
    </div>,
    toastLayer(),
  );
}

/**
 * Lớp chứa các thẻ nổi, tạo một lần và dùng chung.
 *
 * Không dựng bằng React trong `layout` vì `ActionFeedback` nằm rải rác ở mọi cây component; một
 * nút DOM chung là cách duy nhất để chúng xếp chồng theo thứ tự xuất hiện mà không cần context.
 */
function toastLayer(): HTMLElement {
  const id = 'toast-layer';
  let node = document.getElementById(id);
  if (!node) {
    node = document.createElement('div');
    node.id = id;
    node.className = 'toast-layer';
    document.body.appendChild(node);
  }
  return node;
}

/**
 * Nút gửi biểu mẫu tự phản ánh trạng thái đang xử lý: đổi nhãn và tự khóa lại,
 * nên không cần mỗi màn hình tự nhớ disable.
 */
export function SubmitButton({
  action,
  children,
  processingLabel = tr('Đang xử lý…'),
  disabled = false,
  className,
  onClick,
  type = 'submit',
}: {
  action: ActionView;
  children: React.ReactNode;
  processingLabel?: string;
  disabled?: boolean;
  className?: string;
  onClick?: () => void;
  type?: 'submit' | 'button';
}) {
  return (
    <button
      type={type}
      className={className}
      onClick={onClick}
      disabled={disabled || action.isProcessing}
      aria-busy={action.isProcessing}
    >
      {action.isProcessing ? processingLabel : children}
    </button>
  );
}

/**
 * Hiển thị lỗi API. Với 409 transition, API luôn kèm allowedNextStatus (NFR-USE-01) nên
 * người dùng biết ngay bước hợp lệ kế tiếp thay vì chỉ thấy "thao tác thất bại".
 */
/**
 * Nội dung một lỗi, **không kèm khung**.
 *
 * Tách khỏi <see cref="ErrorBox"/> vì cùng nội dung đó xuất hiện ở hai khung khác nhau: khung
 * trong luồng trang (lỗi khi tải dữ liệu — nó là nội dung của trang lúc đó) và thẻ nổi (lỗi của
 * một thao tác). Trước đây thẻ nổi phải lồng `ErrorBox` vào trong, thành hai lớp `.alert` chồng
 * nhau với hai đường viền.
 */
function ErrorMessage({ error }: { error: unknown }) {
  if (!error) return null;

  if (error instanceof ApiError) {
    return (
      <>
        <div>{error.message}</div>
        {error.allowedNextStatus && (
          <div>
            {tr('Bước hợp lệ tiếp theo:')} <strong>{STATUS_LABEL[error.allowedNextStatus as IncidentStatus]}</strong>
          </div>
        )}
        {error.correlationId && (
          <div>
            <code>correlationId: {error.correlationId}</code>
          </div>
        )}
      </>
    );
  }

  return <>{(error as Error).message ?? tr('Lỗi không xác định')}</>;
}

/**
 * Màn hình "không tìm thấy".
 *
 * Khác hẳn <see cref="ErrorBox"/> về mục đích: `ErrorBox` nói "có gì đó hỏng", còn cái này nói
 * "thứ bạn tìm không tồn tại" — hai tình huống đòi hai phản ứng khác nhau của người dùng. Đi tới
 * một ticket đã bị xoá hay một người dùng không có thật là chuyện bình thường (link trong bình
 * luận cũ, ai đó gõ nhầm số), không phải sự cố hệ thống, nên nó không được bày ra như một lỗi đỏ
 * kèm correlationId.
 *
 * Luôn có <b>một lối ra</b>: ngõ cụt không có đường quay lại buộc người dùng bấm nút back của
 * trình duyệt, thứ mà họ vừa dùng để tới đây.
 */
export function NotFoundView({ title, hint, backHref, backLabel }: {
  title: string;
  hint?: string;
  backHref: string;
  backLabel: string;
}) {
  return (
    <div className="empty" style={{ gap: 'var(--space-3)' }}>
      <strong style={{ fontFamily: 'var(--font-heading)', fontSize: 18 }}>{title}</strong>
      {hint && <span style={{ maxWidth: '42ch', textAlign: 'center' }}>{hint}</span>}
      <Link href={backHref} className="btn btn-secondary">{backLabel}</Link>
    </div>
  );
}

/** `true` khi máy chủ nói "không có thứ đó", chứ không phải "tôi đang hỏng". */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/**
 * Lỗi hiển thị **trong luồng trang**. Dùng cho lỗi khi tải dữ liệu: lúc đó danh sách không có
 * gì để bày, nên lời giải thích chính là nội dung của trang — nó không nên trôi đi sau vài giây
 * như một thẻ nổi.
 */
export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null;
  return <div className="alert error"><ErrorMessage error={error} /></div>;
}

/** Chặn truy cập màn hình khi chưa đăng nhập hoặc thiếu permission tương ứng. */
export function Guard({
  permission,
  children,
}: {
  /**
   * Bỏ trống nghĩa là **chỉ cần đã đăng nhập**, không đòi quyền nào. Dùng cho những màn hình mở
   * cho mọi người mà phần cần giữ kín do backend cắt, ví dụ trang hồ sơ.
   */
  permission?: string;
  children: React.ReactNode;
}) {
  const router = useRouter();
  const [state, setState] = useState<'checking' | 'anonymous' | 'allowed' | 'denied'>('checking');

  useEffect(() => {
    // `isAuthenticated` chứ không phải `session.user()`: nó đòi đủ cả token lẫn hồ sơ, và tự
    // dọn nửa phiên. Hỏi mỗi hồ sơ là một nửa của vòng lặp điều hướng đã từng xảy ra.
    if (!session.isAuthenticated()) {
      setState('anonymous');
      router.replace('/login');
      return;
    }
    const user = session.user()!;
    setState(!permission || user.permissions.includes(permission) ? 'allowed' : 'denied');
  }, [permission, router]);

  if (state === 'checking') return <div className="empty">{tr('Đang tải…')}</div>;

  // Trạng thái dứt khoát, không phải một vòng quay không hồi kết.
  //
  // Bản trước để nguyên `checking` rồi trông chờ vào `router.replace`. Chuyển trang không xong
  // — vì bị vòng lặp, vì mạng, vì bất cứ lý do gì — là người dùng nhìn "Đang tải…" mãi mãi mà
  // không biết phải làm gì. Ở đây luôn có một câu trả lời và một lối đi, kể cả khi việc chuyển
  // trang chưa kịp diễn ra.
  if (state === 'anonymous') {
    return (
      <div className="alert info">
        <strong>{tr('Bạn cần đăng nhập để xem màn hình này.')}</strong>
        <div style={{ marginTop: 4 }}>
          <Link href="/login">{tr('Tới trang đăng nhập')}</Link>
        </div>
      </div>
    );
  }

  if (state === 'denied') {
    // Người vừa tự đăng ký thường rơi vào nhánh này. Nói "thiếu permission X" với họ là vô
    // nghĩa; điều họ cần biết là phải làm gì tiếp theo.
    const user = session.user();
    if (user && user.permissions.length === 0) {
      return (
        <div className="alert info">
          <strong>{tr('Tài khoản của bạn chưa được cấp quyền nào.')}</strong>
          <div style={{ marginTop: 4 }}>
            {tr('Liên hệ quản trị viên để được gán vai trò phù hợp. Bạn đăng nhập được nhưng chưa xem hay thao tác được dữ liệu nào.')}
          </div>
        </div>
      );
    }

    return (
      <div className="alert error">
        {tr('Tài khoản của bạn không có permission')} <code>{permission}</code> {tr('để xem màn hình này.')}
      </div>
    );
  }

  return <>{children}</>;
}

/**
 * Giữ nội dung trên màn hình trong lúc **tải lại**.
 *
 * Mẫu sai mà nó thay thế: `{load.isProcessing && <Đang tải…>}` cạnh `{!load.isProcessing && …}`.
 * Cách đó gỡ hẳn cái bảng khỏi DOM mỗi lần làm mới, nên sửa một dòng là mất vị trí cuộn, mất
 * những phần đang mở, và màn hình nháy trắng một nhịp — trong khi dữ liệu cũ vẫn còn đó và vẫn
 * đọc được.
 *
 * Đúng ra chỗ trống chỉ dành cho lần tải ĐẦU, khi thật sự chưa có gì để bày. Những lần sau thì
 * bảng ở nguyên, chỉ mờ đi để nói rằng nó sắp được thay.
 *
 * `aria-busy` để trình đọc màn hình biết vùng này đang cập nhật; không chặn thao tác vì các nút
 * đã tự khoá theo `action.isProcessing` của riêng chúng.
 */
export function Refreshing({ busy, children }: { busy: boolean; children: React.ReactNode }) {
  return (
    <div className={busy ? 'is-refreshing' : undefined} aria-busy={busy || undefined}>
      {children}
    </div>
  );
}

export function Pager({
  page,
  pageSize,
  totalCount,
  onChange,
}: {
  page: number;
  pageSize: number;
  totalCount: number;
  onChange: (page: number) => void;
}) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  return (
    <div className="pager">
      <button
        type="button"
        className="secondary"
        disabled={page <= 1}
        onClick={() => onChange(page - 1)}
      >
        {tr('Trước')}
      </button>
      <span>
        Trang {page}/{totalPages} · {totalCount} bản ghi
      </span>
      <button
        type="button"
        className="secondary"
        disabled={page >= totalPages}
        onClick={() => onChange(page + 1)}
      >
        Sau
      </button>
    </div>
  );
}

export interface SelectOption {
  value: string;
  label: string;
}

interface AnchoredBox { left: number; width: number; maxHeight: number; top?: number; bottom?: number }

/**
 * Neo một danh sách nổi vào ô theo toạ độ màn hình.
 *
 * Không đủ chỗ bên dưới thì lật lên trên bằng cách neo `bottom` chứ không tính `top` từ chiều
 * cao: danh sách ngắn hơn `maxHeight` vẫn dính sát mép ô thay vì lơ lửng.
 */
function useAnchoredBox(
  ref: React.RefObject<HTMLElement | null>,
  open: boolean,
  /**
   * `align: 'end'` neo mép PHẢI của danh sách vào mép phải của ô, tức danh sách xổ về bên trái.
   * `minWidth` cho danh sách rộng hơn ô. Cả hai sinh ra cho cái nút mũi tên bề ngang một chữ:
   * neo trái theo bề rộng ô sẽ cho một danh sách rộng đúng bằng cái mũi tên, và tràn khỏi mép
   * phải màn hình khi ô nằm ở cột cuối bảng.
   */
  opts?: { align?: 'start' | 'end'; minWidth?: number },
): AnchoredBox | undefined {
  const [box, setBox] = useState<AnchoredBox>();
  const align = opts?.align ?? 'start';
  const minWidth = opts?.minWidth ?? 0;

  const place = useCallback(() => {
    const el = ref.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const gap = 4;
    const edge = 8;
    const below = window.innerHeight - r.bottom - gap - edge;
    const above = r.top - gap - edge;
    const flip = below < 160 && above > below;
    const room = Math.max(120, Math.min(260, flip ? above : below));

    const width = Math.min(Math.max(r.width, minWidth), window.innerWidth - edge * 2);
    // Kẹp vào trong màn hình ở cả hai mép: danh sách neo phải vẫn có thể thò ra bên trái khi ô
    // nằm sát lề, và ngược lại.
    const raw = align === 'end' ? r.right - width : r.left;
    const left = Math.max(edge, Math.min(raw, window.innerWidth - width - edge));

    setBox(flip
      ? { left, width, maxHeight: room, bottom: window.innerHeight - r.top + gap }
      : { left, width, maxHeight: room, top: r.bottom + gap });
  }, [ref, align, minWidth]);

  // Đo trước khi trình duyệt vẽ, nếu không danh sách nhấp nháy ở góc trên trái một khung hình.
  useIsoLayoutEffect(() => {
    if (!open) return;
    place();
  }, [open, place]);

  useEffect(() => {
    if (!open) return;
    // `capture: true` để bắt cả cuộn trong vùng cuộn lồng nhau, không riêng cửa sổ.
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
  }, [open, place]);

  return box;
}

/**
 * Ô tìm kèm danh sách: gõ tới đâu lọc tới đó, không phải bấm thêm nút nào.
 *
 * Trước đây chỗ thêm thành viên là **hai** ô rời — gõ tên vào ô tìm, rồi phải bấm sang dropdown
 * bên dưới mới chọn được. Hai bước cho một việc, và không có gì trên màn hình nói rằng ô thứ hai
 * vừa đổi nội dung theo ô thứ nhất.
 *
 * `text` do nơi gọi giữ: chữ gõ vào thường phải đi tiếp lên server để lọc, không chỉ lọc tại chỗ.
 */
export function Combobox({
  id, text, onTextChange, onChange, options, placeholder, ariaLabel, disabled, emptyLabel,
}: {
  id?: string;
  text: string;
  onTextChange: (next: string) => void;
  onChange: (value: string) => void;
  options: SelectOption[];
  placeholder?: string;
  ariaLabel?: string;
  disabled?: boolean;
  emptyLabel?: string;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const listId = useId();
  const close = useCallback(() => setOpen(false), []);
  useDismiss([ref, listRef], close, open);
  const box = useAnchoredBox(ref, open);

  function pick(option: SelectOption) {
    onChange(option.value);
    onTextChange(option.label);
    setOpen(false);
  }

  return (
    <div className="custom-select" ref={ref}>
      <input
        id={id}
        className="input"
        role="combobox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-autocomplete="list"
        aria-label={ariaLabel}
        placeholder={placeholder}
        disabled={disabled}
        value={text}
        onChange={(e) => { onTextChange(e.target.value); setOpen(true); }}
        onFocus={() => setOpen(true)}
        onKeyDown={(e) => {
          if (e.key === 'Escape') { setOpen(false); return; }
          if (e.key === 'ArrowDown') {
            e.preventDefault();
            setOpen(true);
            listRef.current?.querySelector<HTMLButtonElement>('.custom-select-option')?.focus();
          }
        }}
      />

      {open && box && typeof document !== 'undefined' && createPortal((
        <div
          className="custom-select-dropdown"
          role="listbox"
          id={listId}
          ref={listRef}
          style={{ left: box.left, width: box.width, maxHeight: box.maxHeight, top: box.top, bottom: box.bottom }}
        >
          {options.length === 0 && (
            <div className="custom-select-option muted" aria-disabled="true">{emptyLabel ?? tr('Không có kết quả')}</div>
          )}
          {options.map((o) => (
            <button
              key={o.value}
              type="button"
              role="option"
              aria-selected="false"
              className="custom-select-option"
              onClick={() => pick(o)}
            >
              {o.label}
            </button>
          ))}
        </div>
      ), document.body)}
    </div>
  );
}

/**
 * Dropdown tự vẽ, thay cho `<select>` gốc.
 *
 * Lý do không dùng `<select>`: phần danh sách bung ra do hệ điều hành vẽ, CSS không với tới
 * được, nên nó luôn lệch khỏi bản thiết kế — khác phông, khác màu, khác bo góc.
 *
 * Đổi lại phải tự lo những thứ `<select>` cho không: bàn phím, ARIA, và đóng khi bấm ra ngoài.
 *
 * Danh sách được **portal ra `document.body`** với `position: fixed`. Trước đây nó là
 * `position: absolute` nằm trong ô, nên bất kỳ tổ tiên nào có `overflow` khác `visible` đều cắt
 * mất nó. Chỗ đau nhất là bảng: `.table-wrap` đặt `overflow-x: auto`, và theo CSS spec trục còn
 * lại đang là `visible` bị tính lại thành `auto` — nên danh sách vừa bị cắt vừa làm vùng bảng
 * cuộn dọc, đẩy nội dung hàng đi chỗ khác. 7 trên 8 màn hình có bảng đều dính.
 *
 * `inline` cho ô co theo nội dung (bộ chọn project trên thanh trên, bộ lọc trên thanh công cụ);
 * mặc định ô chiếm hết bề ngang như một trường biểu mẫu.
 */
export function Select({
  id,
  value,
  onChange,
  options,
  disabled,
  inline = false,
  chip = false,
  caretOnly = false,
  align = 'start',
  menuWidth,
  placeholder,
  ariaLabel,
  className = '',
  style,
}: {
  id?: string;
  value: string;
  onChange: (val: string) => void;
  options: SelectOption[];
  disabled?: boolean;
  inline?: boolean;
  /** Trigger cỡ chip, không mũi tên — để đứng chung hàng với chip. */
  chip?: boolean;
  /**
   * Trigger chỉ còn cái mũi tên, không hiện giá trị đang chọn.
   *
   * Dùng khi giá trị đã được bày ra ngay bên cạnh dưới một dạng khác — ví dụ tên sự cố kèm liên
   * kết. Nhắc lại nó trong trigger vừa thừa, vừa cướp mất chỗ của chính cái tên đó.
   */
  caretOnly?: boolean;
  /** `end` cho danh sách xổ về bên trái, neo theo mép phải của nút. */
  align?: 'start' | 'end';
  /** Bề rộng tối thiểu của danh sách, khi nút quá hẹp để đọc được các lựa chọn. */
  menuWidth?: number;
  placeholder?: string;
  ariaLabel?: string;
  className?: string;
  style?: React.CSSProperties;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const listId = useId();

  const close = useCallback(() => setOpen(false), []);
  // Danh sách nằm ngoài `ref` sau khi portal, nên phải soát cả hai — thiếu `listRef` thì bấm vào
  // một mục bị tính là bấm ra ngoài, danh sách đóng ngay ở pointerdown và onClick không kịp chạy.
  useDismiss([ref, listRef], close, open);

  const box = useAnchoredBox(ref, open, { align, minWidth: menuWidth });

  // Mở ra thì đưa con trỏ tới mục đang chọn, để bấm mũi tên đi tiếp được ngay.
  //
  // Phụ thuộc `placed` chứ không phải `open`: danh sách chỉ được dựng sau khi đo xong, nên ở lượt
  // render đầu tiên `listRef` còn rỗng. Và phải là **boolean** chứ không phải chính `box` — `box`
  // đổi theo mỗi lần cuộn, phụ thuộc vào nó thì con trỏ bị giật về mục đang chọn giữa lúc người
  // dùng đang bấm mũi tên.
  const placed = box !== undefined;
  useEffect(() => {
    if (!open || !placed) return;
    const el = listRef.current?.querySelector<HTMLButtonElement>('.custom-select-option.selected')
      ?? listRef.current?.querySelector<HTMLButtonElement>('.custom-select-option');
    el?.focus();
  }, [open, placed]);

  const selected = options.find((o) => o.value === value);

  /** Mũi tên lên/xuống chạy trong danh sách, Home/End nhảy đầu cuối. */
  function moveFocus(from: HTMLElement, step: number | 'first' | 'last') {
    const items = [...(listRef.current?.querySelectorAll<HTMLButtonElement>('.custom-select-option') ?? [])];
    if (items.length === 0) return;
    const at = items.indexOf(from as HTMLButtonElement);
    const next = step === 'first' ? 0
      : step === 'last' ? items.length - 1
        : Math.min(items.length - 1, Math.max(0, at + step));
    items[next]?.focus();
  }

  return (
    <div className={`custom-select ${inline || chip ? 'inline' : ''} ${className}`.trim()} ref={ref} style={style}>
      <button
        type="button"
        id={id}
        className={`custom-select-trigger ${chip ? 'btn-chip' : ''} ${caretOnly ? 'caret-only' : ''} ${open ? 'open' : ''}`.trim()}
        onClick={() => !disabled && setOpen((v) => !v)}
        disabled={disabled}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? listId : undefined}
        aria-label={ariaLabel}
        onKeyDown={(e) => {
          if (e.key === 'ArrowDown' || e.key === 'Enter' || e.key === ' ') {
            if (!open) { e.preventDefault(); setOpen(true); }
          }
        }}
      >
        {!caretOnly && (
          <span className="custom-select-value">{selected ? selected.label : (placeholder ?? tr('Chọn...'))}</span>
        )}
        {!chip && (
          <svg className="custom-select-icon" width="16" height="16" viewBox="0 0 24 24" fill="none"
            stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <polyline points="6 9 12 15 18 9" />
          </svg>
        )}
      </button>

      {open && box && typeof document !== 'undefined' && createPortal((
        <div
          className="custom-select-dropdown"
          role="listbox"
          id={listId}
          ref={listRef}
          aria-labelledby={id}
          style={{ left: box.left, width: box.width, maxHeight: box.maxHeight, top: box.top, bottom: box.bottom }}
        >
          {options.map((opt) => (
            <button
              type="button"
              key={opt.value}
              role="option"
              aria-selected={opt.value === value}
              className={`custom-select-option ${opt.value === value ? 'selected' : ''}`}
              onClick={() => { onChange(opt.value); setOpen(false); }}
              onKeyDown={(e) => {
                const target = e.currentTarget;
                if (e.key === 'ArrowDown') { e.preventDefault(); moveFocus(target, 1); }
                else if (e.key === 'ArrowUp') { e.preventDefault(); moveFocus(target, -1); }
                else if (e.key === 'Home') { e.preventDefault(); moveFocus(target, 'first'); }
                else if (e.key === 'End') { e.preventDefault(); moveFocus(target, 'last'); }
                else if (e.key === 'Tab') setOpen(false);
              }}
            >
              {opt.label}
            </button>
          ))}
        </div>
      ), document.body)}
    </div>
  );
}

/**
 * Công tắc bật/tắt.
 *
 * Vẫn là một `input[type=checkbox]` thật, chỉ vẽ khác: nó giữ nguyên bàn phím, đọc màn hình và
 * trạng thái `disabled` — một cặp `div` bắt sự kiện chuột thì mất sạch những thứ đó.
 *
 * Dùng cho lựa chọn **có hiệu lực ngay**, khác với ô tick trong biểu mẫu chờ bấm Lưu.
 */
/**
 * Ô mật khẩu kèm nút hiện/ẩn của riêng ứng dụng.
 *
 * <b>Vì sao không dựa vào trình duyệt.</b> Edge tự vẽ một nút con mắt (`::-ms-reveal`), nhưng
 * nó chỉ xuất hiện khi ô đang "dirty" trong phiên focus hiện tại: gõ vào thì thấy, bấm ra ngoài
 * rồi quay lại gõ tiếp thì mất. Chrome và Firefox không vẽ gì cả. Một nút lúc có lúc không, và
 * khác nhau theo trình duyệt, thì tệ hơn là không có nút.
 *
 * Nút gốc bị ẩn bằng CSS (`::-ms-reveal`) để không có hai con mắt chồng nhau.
 */
export function PasswordInput({
  id,
  value,
  onChange,
  autoComplete,
  required = false,
  minLength,
  placeholder,
  className,
}: {
  id?: string;
  value: string;
  onChange: (value: string) => void;
  autoComplete?: string;
  required?: boolean;
  minLength?: number;
  placeholder?: string;
  className?: string;
}) {
  const [visible, setVisible] = useState(false);

  return (
    <div className="password-field">
      <input
        id={id}
        className={className}
        type={visible ? 'text' : 'password'}
        autoComplete={autoComplete}
        required={required}
        minLength={minLength}
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
      <button
        type="button"
        className="password-toggle"
        onClick={() => setVisible((v) => !v)}
        aria-pressed={visible}
        aria-label={visible ? tr('Ẩn mật khẩu') : tr('Hiện mật khẩu')}
        title={visible ? tr('Ẩn mật khẩu') : tr('Hiện mật khẩu')}
      >
        {/* Nét mảnh theo currentColor, cùng quy ước với mọi icon khác trong dự án — emoji đổi
            hình theo hệ điều hành và không ăn nhập với phần còn lại của giao diện. */}
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
          strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          {visible ? (
            <>
              <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94" />
              <path d="M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19" />
              <path d="M14.12 14.12a3 3 0 1 1-4.24-4.24" />
              <line x1="1" y1="1" x2="23" y2="23" />
            </>
          ) : (
            <>
              <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
              <circle cx="12" cy="12" r="3" />
            </>
          )}
        </svg>
      </button>
    </div>
  );
}

export function Toggle({
  checked,
  onChange,
  disabled = false,
  ariaLabel,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  ariaLabel: string;
}) {
  return (
    <label className="switch">
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        aria-label={ariaLabel}
        onChange={(event) => onChange(event.target.checked)}
      />
      <span className="track" aria-hidden="true"><span className="knob" /></span>
    </label>
  );
}
