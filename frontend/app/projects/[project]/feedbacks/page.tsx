'use client';

import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import {
  api,
  CHANNEL_LABEL,
  formatTime,
  session,
  type Feedback,
  type FeedbackChannel,
  type FeedbackReply,
  type Incident,
  type Paged,
} from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ActionFeedback, ErrorBox, FeedbackStatusBadge, Guard, PageHead, Pager, Refreshing, RequiredMark, Select, StatusBadge, SubmitButton } from '@/components/ui';
import { UNCATEGORIZED, useTransferTargets, type TransferTarget } from '@/components/tickets/ProjectTransfer';
import { Icons } from '@/components/tickets/Bits';
import { QueryInput } from '@/components/QueryInput';
import { tr } from '@/lib/i18n';

/**
 * Luồng hội thoại của một phản hồi: lời xác nhận tự động của hệ thống và các câu trả lời
 * của đội hỗ trợ. Người có <code>feedback.respond</code> trả lời được ngay tại đây.
 */
function ReplyThread({
  project,
  feedbackId,
  canRespond,
  onReplied,
}: {
  project: string;
  feedbackId: string;
  canRespond: boolean;
  onReplied: () => void;
}) {
  const [replies, setReplies] = useState<FeedbackReply[] | null>(null);
  const [body, setBody] = useState('');
  const load = useAction<FeedbackReply[]>({ latest: true });
  const replyAction = useAction<FeedbackReply>();

  const runLoad = useCallback(async () => {
    const result = await load.run(() => api.listReplies(project, feedbackId));
    if (result) {
      setReplies(result);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [feedbackId]);

  useEffect(() => {
    void runLoad();
  }, [runLoad]);

  async function reply(event: React.FormEvent) {
    event.preventDefault();
    const created = await replyAction.run(
      () => api.replyFeedback(project, feedbackId, body.trim()),
      tr('Đã gửi câu trả lời tới khách hàng.'),
    );
    if (created) {
      setBody('');
      setReplies((prev) => [...(prev ?? []), created]);
      onReplied();
    }
  }

  return (
    <div className="thread">
      <ActionFeedback action={replyAction} processingLabel={tr('Đang gửi câu trả lời…')} />
      {load.status === 'failed' && <ErrorBox error={load.error} />}
      {load.isProcessing && !replies && <div className="empty">{tr('Đang tải trao đổi…')}</div>}

      {replies && replies.length === 0 && <div className="empty">{tr('Chưa có câu trả lời nào.')}</div>}

      {replies && replies.length > 0 && (
        <ul className="timeline">
          {replies.map((r) => (
            <li key={r.id}>
              <div>
                <strong>{r.isAutomatic ? tr('Hệ thống') : (r.responder?.displayName ?? '—')}</strong>{' '}
                {r.isAutomatic && <span className="badge fb-Acknowledged">{tr('tự động')}</span>}{' '}
                <span className="muted">· {formatTime(r.createdAt)}</span>
              </div>
              <div>{r.body}</div>
            </li>
          ))}
        </ul>
      )}

      {canRespond && (
        <form onSubmit={reply}>
          <div className="field">
            <label htmlFor={`reply-${feedbackId}`}>{tr('Trả lời khách hàng (tối đa 2000 ký tự)')}</label>
            <textarea
              id={`reply-${feedbackId}`}
              required
              maxLength={2000}
              value={body}
              onChange={(e) => setBody(e.target.value)}
            />
          </div>
          <SubmitButton action={replyAction} disabled={!body.trim()} processingLabel={tr('Đang gửi…')} className="btn btn-primary">
            {tr('Gửi câu trả lời')}
          </SubmitButton>
        </form>
      )}
    </div>
  );
}

/**
 * UC-BIZ-04 · UC-BIZ-09. Một màn hình, hai vai:
 * - Support (có <code>feedback.read.all</code>): hàng đợi phân loại + trả lời khách hàng.
 * - Khách hàng: "Phản hồi của tôi" — gửi, theo dõi trạng thái tiếp nhận và đọc câu trả lời.
 */
/**
 * Giá trị giả cho mục "bỏ đính kèm" nằm chung danh sách với các sự cố.
 *
 * Gộp vào một danh sách thay vì thêm một nút riêng: gắn, đổi và gỡ là ba nước của cùng một
 * quyết định — "phản hồi này thuộc sự cố nào" — nên chúng thuộc về cùng một chỗ bấm. Tiền tố
 * `__` để không bao giờ đụng một GUID thật.
 */
const UNLINK = '__unlink__';

function FeedbackQueue() {
  // Phản hồi thuộc về project; slug lấy từ đường dẫn để mọi lời gọi API đi đúng phạm vi.
  const { project } = useParams<{ project: string }>();
  // Quyền quyết định vai. API vẫn là nơi phán quyết cuối cùng — đây chỉ là chuyện hiển thị.
  const isStaff = session.can('feedback.read.all');
  const canCreate = session.can('feedback.create');
  const canLink = session.can('feedback.link');
  const canRespond = session.can('feedback.respond');
  const canReadIncidents = session.can('incident.read');

  const [data, setData] = useState<Paged<Feedback> | null>(null);
  const [openIncidents, setOpenIncidents] = useState<Incident[]>([]);
  // Cùng ô tìm kiếm và cùng lớp CSS với Issues; chữ tìm gửi lên server nên nó tìm trên **toàn
  // bộ** phản hồi chứ không chỉ trang đang xem.
  const [q, setQ] = useState('');
  /**
   * Bộ lọc hàng đợi, ba trạng thái. `all` là mặc định — mở màn hình ra thấy toàn bộ phản hồi
   * rồi mới thu hẹp, chứ không phải mở ra đã bị lọc sẵn mà không biết mình đang thiếu gì.
   */
  const [queue, setQueue] = useState<'all' | 'unlinked' | 'linked'>('all');
  const [page, setPage] = useState(1);
  const [openThread, setOpenThread] = useState<string | null>(null);

  const load = useAction<Paged<Feedback>>({ latest: true });
  const createAction = useAction<Feedback>();
  const linkAction = useAction<Feedback>();
  const transferAction = useAction<Feedback>();

  // Phản hồi không có trang chi tiết — mọi thao tác của nó nằm ngay trong hàng — nên ô chuyển
  // project cũng nằm đó, cạnh ô "Gắn vào sự cố".
  const transferTargets = useTransferTargets(project);

  const [creating, setCreating] = useState(false);
  const [channel, setChannel] = useState<FeedbackChannel>(isStaff ? 'Hotline' : 'Web');
  const [customerEmail, setCustomerEmail] = useState('');
  const [content, setContent] = useState('');

  const runLoad = useCallback(async () => {
    const result = await load.run(async () => {
      const page1 = await api.listFeedbacks(project, {
        // Khách hàng luôn thấy toàn bộ phản hồi của mình — bộ lọc hàng đợi là việc của Support.
        q: q || undefined,
        // API nhận bool? ba nhánh: true = chưa gắn, false = đã gắn, bỏ trống = tất cả.
        unlinkedOnly: isStaff && queue !== 'all' ? queue === 'unlinked' : undefined,
        page,
        pageSize: 20,
      });

      if (canLink && canReadIncidents) {
        // Chỉ sự cố chưa đóng mới nhận được phản hồi (BR-BIZ-07).
        const [investigating, mitigating] = await Promise.all([
          api.listIncidents(project, { status: 'Investigating', pageSize: 100 }),
          api.listIncidents(project, { status: 'Mitigating', pageSize: 100 }),
        ]);
        setOpenIncidents([...investigating.items, ...mitigating.items]);
      }

      return page1;
    });

    if (result) {
      setData(result);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, isStaff, queue, page, canLink, canReadIncidents]);

  useEffect(() => {
    void runLoad();
  }, [runLoad]);

  async function create(event: React.FormEvent) {
    event.preventDefault();
    const created = await createAction.run(
      () => api.createFeedback(project, { channel, customerEmail: customerEmail || undefined, content }),
      isStaff
        ? tr('Đã ghi nhận phản hồi vào hàng đợi chưa phân loại.')
        : tr('Đã gửi phản hồi. Hệ thống đã xác nhận tiếp nhận — mở "Trao đổi" để xem.'),
    );

    if (created) {
      setCreating(false);
      setCustomerEmail('');
      setContent('');
      setPage(1);
      await runLoad();
    }
  }

  async function link(feedbackId: string, incidentId: string) {
    if (!incidentId) return;
    const linked = await linkAction.run(
      () => api.linkFeedback(project, feedbackId, incidentId),
      tr('Đã gắn phản hồi vào sự cố.'),
    );
    if (linked) {
      await runLoad();
    }
  }

  async function unlink(feedbackId: string) {
    const done = await linkAction.run(
      () => api.unlinkFeedback(project, feedbackId),
      tr('Đã gỡ phản hồi khỏi sự cố.'),
    );
    if (done) {
      await runLoad();
    }
  }

  async function transfer(feedbackId: string, toProject: string) {
    if (!toProject) return;
    const moved = await transferAction.run(
      () => api.transferFeedback(project, feedbackId, toProject),
      tr('Đã chuyển phản hồi sang project khác. Sự cố đang gắn đã được gỡ ra vì nó ở lại project cũ.'),
    );
    if (moved) {
      await runLoad();
    }
  }

  const columnCount = isStaff ? 9 : 8;

  return (
    <>
      <PageHead
        kicker={isStaff ? tr('Toàn hệ thống') : tr('Của tôi')}
        title={isStaff ? 'Feedback' : tr('Phản hồi của tôi')}
        hint={isStaff
          ? tr('Gắn phản hồi vào sự cố đang mở để team kỹ thuật không xử lý trùng lặp, và trả lời ngay trên phản hồi của khách hàng.')
          : tr('Gửi phản hồi cho đội hỗ trợ và theo dõi tại đây: hệ thống xác nhận tiếp nhận ngay, và khi có người trả lời, trạng thái chuyển sang "Đã trả lời".')}
        actions={canCreate ? (
          <button type="button" className={creating ? 'btn btn-secondary' : 'btn btn-primary'} onClick={() => setCreating((v) => !v)}>
            {creating ? tr('Hủy') : isStaff ? tr('Ghi nhận phản hồi mới') : tr('Gửi phản hồi mới')}
          </button>
        ) : undefined}
      />

      <ActionFeedback action={createAction} processingLabel={tr('Đang ghi nhận phản hồi…')} />
      <ActionFeedback action={linkAction} processingLabel={tr('Đang gắn phản hồi vào sự cố…')} />
      <ActionFeedback action={transferAction} processingLabel={tr('Đang chuyển phản hồi sang project khác…')} />
      {load.status === 'failed' && <ErrorBox error={load.error} />}

      {canCreate && creating && (
        <div className="card">
          <h2>{isStaff ? tr('Ghi nhận phản hồi mới') : tr('Gửi phản hồi mới')}</h2>
          <form onSubmit={create}>
            <div className="row">
              <div className="field">
                <label htmlFor="channel">{tr('Kênh tiếp nhận')}</label>
                <Select
                  id="channel"
                  value={channel}
                  onChange={(next) => setChannel(next as FeedbackChannel)}
                  options={(Object.keys(CHANNEL_LABEL) as FeedbackChannel[])
                    .map((c) => ({ value: c, label: tr(CHANNEL_LABEL[c]) }))}
                />
              </div>
              <div className="field">
                <label htmlFor="customerEmail">
                  {isStaff ? tr('Email khách hàng (tùy chọn)') : tr('Email liên hệ (tùy chọn)')}
                </label>
                <input
                  id="customerEmail"
                  type="email"
                  value={customerEmail}
                  onChange={(e) => setCustomerEmail(e.target.value)}
                />
              </div>
            </div>
            <div className="field" style={{ marginTop: 14 }}>
              <label htmlFor="content">{tr('Nội dung phản hồi (tối thiểu 10 ký tự)')}<RequiredMark /></label>
              <textarea
                id="content"
                required
                minLength={10}
                value={content}
                onChange={(e) => setContent(e.target.value)}
              />
            </div>
            <SubmitButton action={createAction} processingLabel={tr('Đang gửi…')} className="btn btn-primary">
              {isStaff ? tr('Ghi nhận') : tr('Gửi phản hồi')}
            </SubmitButton>
          </form>
        </div>
      )}

      <div className="list-section">
        <div className="issues-toolbar" style={{ marginBottom: 16 }}>
          <QueryInput
            className="search"
            value={q}
            onSubmit={(next) => { setQ(next.trim()); setPage(1); }}
            prefix={Icons.search}
            placeholder={tr('Tìm phản hồi')}
            ariaLabel={tr('Tìm phản hồi')}
          />
        </div>

        {isStaff && (
          <div className="row" style={{ marginBottom: 16 }}>
            <div className="field">
              <label htmlFor="queue">{tr('Bộ lọc hàng đợi')}</label>
              <Select
                id="queue"
                value={queue}
                onChange={(next) => {
                  setQueue(next as 'all' | 'unlinked' | 'linked');
                  setPage(1);
                }}
                options={[
                  { value: 'all', label: tr('Tất cả') },
                  { value: 'unlinked', label: tr('Chưa phân loại') },
                  { value: 'linked', label: tr('Đã phân loại') },
                ]}
              />
            </div>
          </div>
        )}

        {load.isProcessing && !data && <div className="empty">{tr('Đang tải danh sách phản hồi…')}</div>}

        {data && data.items.length === 0 && !load.isProcessing && (
          <div className="empty">
            {isStaff ? tr('Hàng đợi trống.') : tr('Bạn chưa gửi phản hồi nào.')}
          </div>
        )}

        {data && data.items.length > 0 && (
          <Refreshing busy={load.isProcessing}>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>{tr('Kênh')}</th>
                    <th>{tr('Nội dung')}</th>
                    <th>{tr('Trạng thái')}</th>
                    {isStaff && <th>{tr('Liên hệ')}</th>}
                    <th>{tr('Sự cố')}</th>
                    <th>{tr('Trạng thái sự cố')}</th>
                    <th>{tr('Ghi nhận')}</th>
                    <th>{tr('Dự án')}</th>
                    <th>{tr('Hành động')}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((feedback) => (
                    <FeedbackRows
                      project={project}
                      key={feedback.id}
                      feedback={feedback}
                      isStaff={isStaff}
                      canLink={canLink}
                      canRespond={canRespond}
                      columnCount={columnCount}
                      openIncidents={openIncidents}
                      linkProcessing={linkAction.isProcessing}
                      transferTargets={transferTargets}
                      transferProcessing={transferAction.isProcessing}
                      onTransfer={transfer}
                      open={openThread === feedback.id}
                      onToggle={() =>
                        setOpenThread((current) => (current === feedback.id ? null : feedback.id))
                      }
                      onLink={link}
                      onUnlink={unlink}
                      onReplied={runLoad}
                    />
                  ))}
                </tbody>
              </table>
            </div>
            <Pager
              page={data.page}
              pageSize={data.pageSize}
              totalCount={data.totalCount}
              onChange={setPage}
            />
          </Refreshing>
        )}
      </div>
    </>
  );
}

function FeedbackRows({
  project,
  feedback,
  isStaff,
  canLink,
  canRespond,
  columnCount,
  openIncidents,
  linkProcessing,
  transferTargets,
  transferProcessing,
  open,
  onToggle,
  onLink,
  onUnlink,
  onTransfer,
  onReplied,
}: {
  project: string;
  feedback: Feedback;
  isStaff: boolean;
  canLink: boolean;
  canRespond: boolean;
  columnCount: number;
  openIncidents: Incident[];
  linkProcessing: boolean;
  transferTargets: TransferTarget[];
  transferProcessing: boolean;
  open: boolean;
  onToggle: () => void;
  onLink: (feedbackId: string, incidentId: string) => void;
  onUnlink: (feedbackId: string) => void;
  onTransfer: (feedbackId: string, toProject: string) => void;
  onReplied: () => void;
}) {
  const changeable = openIncidents.filter((incident) => incident.id !== feedback.incidentId);
  const projectLabel = project === UNCATEGORIZED ? tr('chưa phân loại') : project;

  return (
    <>
      <tr>
        <td>{tr(CHANNEL_LABEL[feedback.channel])}</td>
        <td style={{ maxWidth: 380 }}>{feedback.content}</td>
        <td>
          <FeedbackStatusBadge status={feedback.status} />
        </td>
        {isStaff && <td className="muted">{feedback.customerEmail ?? '—'}</td>}
        {/* Chia ngang 9:1. Phần 9 là **tên sự cố, bấm được** — thứ người đọc cần nhất ở đây, và
            nó xuống dòng khi dài thay vì bị cắt cụt. Phần 1 là cái mũi tên mở danh sách để đổi
            hoặc gỡ.

            Bản trước nhét cả hai vai vào một ô chọn: tên sự cố làm nhãn của trigger. Nhìn thì
            gọn, nhưng cái tên ấy **không bấm sang sự cố được** — muốn mở nó ra phải nhớ tên rồi
            đi tìm ở màn hình khác. */}
        <td>
          <div className="linked-incident">
            <div className="linked-incident-name">
              {feedback.incidentId ? (
                <Link href={`/projects/${project}/incidents/${feedback.incidentId}`}>{feedback.incidentTitle}</Link>
              ) : (
                <span className="muted">{tr('Chưa phân loại')}</span>
              )}
            </div>
            {canLink && (changeable.length > 0 || feedback.incidentId) && (
              <Select
                caretOnly
                align="end"
                menuWidth={320}
                inline
                value=""
                disabled={linkProcessing}
                ariaLabel={feedback.incidentId ? tr('Đổi hoặc gỡ sự cố đang gắn') : tr('Chọn sự cố')}
                onChange={(next) => {
                  if (!next) return;
                  if (next === UNLINK) onUnlink(feedback.id);
                  else onLink(feedback.id, next);
                }}
                options={[
                  // Mục gỡ nằm trên cùng: nó là thao tác sửa sai, và người đi tìm nó thường đang
                  // vội. Chỉ hiện khi thật sự có cái để gỡ.
                  ...(feedback.incidentId ? [{ value: UNLINK, label: tr('Bỏ đính kèm sự cố') }] : []),
                  ...changeable.map((incident) => ({ value: incident.id, label: incident.title })),
                ]}
              />
            )}
          </div>
        </td>
        <td>{feedback.incidentStatus ? <StatusBadge status={feedback.incidentStatus} /> : <span className="muted">—</span>}</td>
        <td className="muted">{formatTime(feedback.createdAt)}</td>
        <td>
          {transferTargets.length > 0 ? (
            <Select
              inline
              value=""
              placeholder={projectLabel}
              disabled={transferProcessing}
              ariaLabel={tr('Chuyển dự án')}
              onChange={(toProject) => toProject && onTransfer(feedback.id, toProject)}
              options={transferTargets}
            />
          ) : (
            <span className="muted">{projectLabel}</span>
          )}
        </td>
        <td>
          <button type="button" className="btn btn-secondary" onClick={onToggle}>
            {open ? tr('Ẩn trao đổi') : tr('Trao đổi')}
          </button>
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={columnCount}>
            <ReplyThread project={project} feedbackId={feedback.id} canRespond={canRespond} onReplied={onReplied} />
          </td>
        </tr>
      )}
    </>
  );
}

export default function FeedbacksPage() {
  return (
    <Guard permission="feedback.read">
      <FeedbackQueue />
    </Guard>
  );
}
