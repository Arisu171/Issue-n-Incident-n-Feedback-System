'use client';

import { useCallback, useEffect, useState } from 'react';
import { useParams, useRouter } from 'next/navigation';
import {
  api,
  ApiError,
  formatDuration,
  formatTime,
  session,
  STATUS_LABEL,
  type Feedback,
  type Incident,
  type IncidentComment,
  type IncidentSeverity,
  type StatusHistoryEntry,
} from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ActionFeedback, ErrorBox, FeedbackStatusBadge, Guard, PageHead, RequiredMark, Select, SeverityBadge, SlaBadge, StatusBadge, SubmitButton } from '@/components/ui';
import { UnassignedAvatar } from '@/components/tickets/Bits';
import { EditTrail } from '@/components/EditTrail';
import { ActiveEditorsNotice } from '@/components/ActiveEditorsNotice';
import { useEditClaim } from '@/lib/useEditClaim';
import { useTransferTargets } from '@/components/tickets/ProjectTransfer';
import { tr } from '@/lib/i18n';

/** UC-BIZ-02, UC-BIZ-03, UC-BIZ-05, UC-BIZ-06, UC-BIZ-07 trên một màn hình chi tiết. */
function IncidentDetail({ id }: { id: string }) {
  // Sự cố thuộc về project; slug lấy từ đường dẫn để mọi lời gọi API đi đúng phạm vi.
  const { project } = useParams<{ project: string }>();
  const router = useRouter();
  const [incident, setIncident] = useState<Incident | null>(null);
  const [history, setHistory] = useState<StatusHistoryEntry[]>([]);
  const [comments, setComments] = useState<IncidentComment[]>([]);
  const [feedbacks, setFeedbacks] = useState<Feedback[]>([]);
  const [candidates, setCandidates] = useState<{ id: string; login: string; displayName: string }[]>([]);
  const [note, setNote] = useState('');
  const [commentBody, setCommentBody] = useState('');
  const [assignee, setAssignee] = useState('');

  // Bản nháp của form sửa nội dung. Khởi tạo từ bản ghi mỗi lần mở form chứ không đồng bộ
  // liên tục: người đang gõ dở mà một lượt tải nền ập vào ghi đè là mất chữ.
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState({ title: '', description: '', severity: 'Medium' as IncidentSeverity, reason: '' });
  const [editingComment, setEditingComment] = useState<string | null>(null);
  const [commentDraft, setCommentDraft] = useState('');

  // Mỗi thao tác có trạng thái riêng: đang chuyển trạng thái không được làm mờ nút gán người
  // xử lý, và thông báo của thao tác này không được đè lên thông báo của thao tác kia.
  const load = useAction<Incident>({ latest: true });
  const transitionAction = useAction<Incident>();
  const commentAction = useAction<IncidentComment>();
  const editAction = useAction<Incident>();
  const commentEditAction = useAction<IncidentComment>();
  const assignAction = useAction<void>();
  const transferAction = useAction<Incident>();
  const deleteAction = useAction<void>();

  const canAssign = session.can('incident.assign');

  // Chuyển project: cùng chỗ và cùng khuôn với "Gán người xử lý", vì nó cũng là một thao tác
  // trên chính sự cố này. Mục `uncategorized` không có màn hình riêng — nó là danh sách sự cố
  // của một project ảo — nên việc phân loại đi qua đúng nút này.
  const transferTargets = useTransferTargets(project);
  const [transferTo, setTransferTo] = useState('');
  const canDelete = session.can('incident.delete');
  const canUpdateStatus = session.can('incident.update_status');
  const canResolve = session.can('incident.resolve');
  const canReadFeedback = session.can('feedback.read');
  const canComment = session.can('incident.comment');

  // Luật hiển thị phải trùng luật của backend (ResourceAccessRules.CanEditIncidentContent),
  // nếu không thì nút hiện ra để rồi nhận 403 — hoặc tệ hơn, nút không hiện cho người thật
  // sự có quyền. API vẫn là nơi phán quyết cuối cùng; đây chỉ là chuyện bày ra hay không.
  const canManageAny = session.can('incident.manage_any');
  const me = session.user()?.id;

  // Chỗ sửa được giữ đúng trong lúc form mở, và trả lại khi đóng. Danh sách trả về là những
  // người khác cũng đang mở form — hiện lên để người dùng biết trước khi gõ xong cả đoạn.
  const incidentEditors = useEditClaim(
    editing, id,
    () => api.claimIncidentEdit(project, id),
    () => api.releaseIncidentEdit(project, id),
  );

  const commentEditors = useEditClaim(
    editingComment !== null, editingComment ?? '',
    () => api.claimCommentEdit(project, id, editingComment!),
    () => api.releaseCommentEdit(project, id, editingComment!),
  );

  // Lượt tải chính chỉ gồm những gì đi cùng quyền incident.read — quyền mà Guard đã bảo đảm.
  const runLoad = useCallback(async () => {
    const detail = await load.run(async () => {
      const [incidentDetail, entries, thread] = await Promise.all([
        api.getIncident(project, id),
        api.getHistory(project, id),
        api.listComments(project, id),
      ]);
      setHistory(entries);
      setComments(thread);
      return incidentDetail;
    });

    if (detail) {
      setIncident(detail);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  useEffect(() => {
    void runLoad();
  }, [runLoad]);

  // Hai phần phụ tải riêng và im lặng khi lỗi: thiếu quyền phụ (feedback.read, user.read)
  // chỉ ẩn phần tương ứng — trước đây chúng nằm chung lượt tải chính nên một lỗi 403 lẻ
  // kéo cả trang thành "Không tìm thấy sự cố".
  useEffect(() => {
    if (!canReadFeedback) return;
    let cancelled = false;
    api
      .listFeedbacks(project, { incidentId: id, pageSize: 100 })
      .then((page) => {
        if (!cancelled) setFeedbacks(page.items);
      })
      .catch(() => {
        if (!cancelled) setFeedbacks([]);
      });
    return () => {
      cancelled = true;
    };
  }, [id, canReadFeedback]);

  useEffect(() => {
    if (!canAssign) return;
    let cancelled = false;
    api
      .incidentAssignableUsers(project)
      .then((users) => {
        if (!cancelled) setCandidates(users);
      })
      .catch(() => {
        if (!cancelled) setCandidates([]);
      });
    return () => {
      cancelled = true;
    };
  }, [canAssign]);

  async function transition() {
    if (!incident?.allowedNextStatus) return;
    const target = incident.allowedNextStatus;

    const updated = await transitionAction.run(
      () => api.updateStatus(project, id, target, note || undefined),
      tr('Đã chuyển sang "{v0}".', { v0: tr(STATUS_LABEL[target]) }),
    );

    if (updated) {
      setNote('');
      await runLoad();
    }
  }

  async function doTransfer(event: React.FormEvent) {
    event.preventDefault();
    const moved = await transferAction.run(
      () => api.transferIncident(project, id, transferTo),
      tr('Đã chuyển sự cố sang project khác.'),
    );
    // Sự cố không còn nằm dưới đường dẫn hiện tại nữa: ở lại đây thì lần tải sau nhận 404.
    // Đi bằng router: gán thẳng địa chỉ trình duyệt sẽ tải lại cả tài liệu và để lại một
    // khung trắng giữa hai trang.
    if (moved) {
      router.push(`/projects/${transferTo}/incidents/${id}`);
    }
  }

  async function doAssign(event: React.FormEvent) {
    event.preventDefault();
    const ok = await assignAction.run(() => api.assign(project, id, assignee), tr('Đã gán người xử lý.'));
    if (ok !== undefined) {
      await runLoad();
    }
  }

  async function softDelete() {
    if (!confirm(tr('Xóa mềm sự cố này? Bản ghi sẽ biến mất khỏi danh sách nhưng lịch sử vẫn được giữ.')))
      return;

    const ok = await deleteAction.run(
      () => api.deleteIncident(project, id),
      tr('Đã xóa mềm sự cố, đang quay lại danh sách…'),
    );
    if (ok !== undefined) {
      router.push(`/projects/${project}/incidents`);
    }
  }

  function openEdit() {
    if (!incident) return;
    setDraft({
      title: incident.title,
      description: incident.description ?? '',
      severity: incident.severity,
      reason: '',
    });
    setEditing(true);
  }

  async function saveEdit(event: React.FormEvent) {
    event.preventDefault();
    if (!incident) return;

    // Chỉ gửi trường thật sự đổi: gửi nguyên cả form thì backend vẫn bỏ qua giá trị trùng,
    // nhưng payload sẽ mang cả những trường người dùng không hề chạm vào — và khi có tranh
    // cãi, thứ đọc lại được phải đúng bằng thứ người ta đã sửa.
    const body: Parameters<typeof api.updateIncident>[2] = {};
    if (draft.title.trim() !== incident.title) body.title = draft.title.trim();
    if (draft.description !== (incident.description ?? '')) body.description = draft.description;
    if (draft.severity !== incident.severity) body.severity = draft.severity;

    if (Object.keys(body).length === 0) {
      setEditing(false);
      return;
    }

    if (draft.reason.trim()) body.reason = draft.reason.trim();

    // Luôn kèm phiên bản đang thấy trên màn hình. Backend chỉ **bắt buộc** If-Match khi bản ghi
    // đang bị người khác chiếm dụng, nhưng gửi sẵn thì một form mở từ lâu không bao giờ ghi đè
    // lặng lẽ — nó nhận 412 và người dùng được tải lại bản mới.
    const updated = await editAction.run(
      async () => {
        try {
          return await api.updateIncident(project, id, body, incident.version);
        } catch (e) {
          if (e instanceof ApiError && e.status === 412) await runLoad();
          throw e;
        }
      },
      tr('Đã cập nhật nội dung sự cố.'),
    );

    if (updated) {
      setIncident(updated);
      setEditing(false);
    }
  }

  async function saveComment(commentId: string, version: number) {
    const updated = await commentEditAction.run(
      async () => {
        try {
          return await api.updateComment(project, id, commentId, commentDraft.trim(), undefined, version);
        } catch (e) {
          if (e instanceof ApiError && e.status === 412) await runLoad();
          throw e;
        }
      },
      tr('Đã cập nhật trao đổi.'),
    );

    if (updated) {
      setComments((prev) => prev.map((c) => (c.id === commentId ? updated : c)));
      setEditingComment(null);
    }
  }

  async function postComment(event: React.FormEvent) {
    event.preventDefault();
    const created = await commentAction.run(
      () => api.createComment(project, id, commentBody.trim()),
      tr('Đã gửi trao đổi.'),
    );
    if (created) {
      setCommentBody('');
      setComments((prev) => [...prev, created]);
    }
  }

  if (load.isProcessing && !incident) return <div className="empty">{tr('Đang tải chi tiết sự cố…')}</div>;

  if (!incident) {
    // 403 và 404 là hai câu chuyện khác nhau: "không có quyền" mà báo "không tìm thấy" thì
    // người dùng sẽ đi tìm nhầm chỗ.
    const errorStatus = load.error instanceof ApiError ? load.error.status : null;
    return (
      <>
        <ErrorBox error={load.error} />
        <div className="empty">
          {errorStatus === 403
            ? tr('Sự cố này không do bạn báo cáo và cũng không được giao cho bạn.')
            : errorStatus === 404
              ? tr('Không tìm thấy sự cố.')
              : tr('Không tải được chi tiết sự cố.')}
        </div>
      </>
    );
  }

  // Bước Resolved đòi thêm incident.resolve; nút bị vô hiệu hóa nhưng API mới là nơi quyết định.
  // Trùng ResourceAccessRules.CanEditIncidentContent ở backend: người báo cáo, hoặc
  // incident.manage_any. Người được giao xử lý cố ý KHÔNG có trong danh sách.
  const canEditContent = incident.reporter.id === me || canManageAny;

  const nextNeedsResolve = incident.allowedNextStatus === 'Resolved';
  const canDoNext = nextNeedsResolve ? canResolve : canUpdateStatus;

  return (
    <>
      <PageHead
        kicker="Incident"
        title={incident.title}
        hint={<>
          <StatusBadge status={incident.status} /> <SeverityBadge severity={incident.severity} />{' '}
          <SlaBadge sla={incident.sla} />
        </>}
        actions={canDelete && incident.status === 'Resolved' ? (
          <SubmitButton
            action={deleteAction}
            type="button"
            className="btn btn-secondary danger"
            processingLabel={tr('Đang xóa…')}
            onClick={softDelete}
          >
            {tr('Xóa mềm')}
          </SubmitButton>
        ) : undefined}
      />

      <ActionFeedback action={deleteAction} processingLabel={tr('Đang xóa mềm sự cố…')} />
      {load.status === 'failed' && <ErrorBox error={load.error} />}

      <div className="incident-layout">
        {/* Cột trái gộp một khối chung, không nền — các phần ngăn nhau bằng nét kẻ. */}
        <div className="incident-main">
          <section className="incident-block">
            <h5>
              {tr('Thông tin sự cố')}
              <EditTrail
                lastEdit={incident.lastEdit}
                loadRevisions={() => api.incidentRevisions(project, id)}
              />
              {canEditContent && !editing && !incident.isDeleted && (
                <>
                  {' '}
                  <button type="button" className="ghost small" onClick={openEdit}>
                    {tr('Sửa')}
                  </button>
                </>
              )}
            </h5>

            {editing ? (
              <form onSubmit={saveEdit}>
                <ActiveEditorsNotice editors={incidentEditors} />
                <ActionFeedback action={editAction} processingLabel={tr('Đang lưu nội dung…')} />
                <div className="field">
                  <label htmlFor="edit-title">{tr('Tiêu đề')}<RequiredMark /></label>
                  <input
                    id="edit-title"
                    required
                    minLength={5}
                    maxLength={255}
                    value={draft.title}
                    onChange={(e) => setDraft({ ...draft, title: e.target.value })}
                  />
                </div>
                <div className="field">
                  <label htmlFor="edit-description">{tr('Mô tả')}</label>
                  <textarea
                    id="edit-description"
                    maxLength={10000}
                    value={draft.description}
                    onChange={(e) => setDraft({ ...draft, description: e.target.value })}
                  />
                </div>
                <div className="field">
                  <label htmlFor="edit-severity">{tr('Mức độ')}</label>
                  <Select
                    id="edit-severity"
                    value={draft.severity}
                    onChange={(next) => setDraft({ ...draft, severity: next as IncidentSeverity })}
                    options={[
                      { value: 'Low', label: 'Low' },
                      { value: 'Medium', label: 'Medium' },
                      { value: 'High', label: 'High' },
                      { value: 'Critical', label: 'Critical' },
                    ]}
                  />
                </div>
                {/* Lý do chỉ hỏi khi sửa bài người khác: bắt tác giả giải trình mỗi lần sửa
                    chính tả của mình là thứ ai cũng học cách bỏ qua, và một ô lý do luôn trống
                    thì không còn nói lên điều gì. */}
                {incident.reporter.id !== me && (
                  <div className="field">
                    <label htmlFor="edit-reason">{tr('Lý do sửa (đi kèm mọi dòng lịch sử)')}</label>
                    <input
                      id="edit-reason"
                      maxLength={500}
                      value={draft.reason}
                      onChange={(e) => setDraft({ ...draft, reason: e.target.value })}
                    />
                  </div>
                )}
                <div className="editor-actions">
                  <button type="button" className="btn btn-secondary" onClick={() => setEditing(false)}>
                    {tr('Hủy')}
                  </button>
                  <SubmitButton action={editAction} processingLabel={tr('Đang lưu…')} className="btn btn-primary">
                    {tr('Lưu')}
                  </SubmitButton>
                </div>
              </form>
            ) : (
            <dl className="meta">
              <dt>{tr('Mô tả')}</dt>
              <dd>{incident.description || <span className="muted">{tr('Không có')}</span>}</dd>
              <dt>{tr('Người ghi nhận')}</dt>
              <dd>
                {incident.reporter.displayName} <span className="muted">({incident.reporter.email})</span>
              </dd>
              <dt>{tr('Người xử lý')}</dt>
              <dd>{incident.assignee?.displayName ?? <UnassignedAvatar size="sm" withText />}</dd>
              <dt>{tr('Ghi nhận lúc')}</dt>
              <dd>{formatTime(incident.createdAt)}</dd>
              <dt>{tr('Bắt đầu khắc phục')}</dt>
              <dd>{formatTime(incident.mitigatingAt)}</dd>
              <dt>{tr('Đóng lúc')}</dt>
              <dd>
                {formatTime(incident.resolvedAt)}
                {incident.resolver && (
                  <span className="muted"> · bởi {incident.resolver.displayName}</span>
                )}
              </dd>
              <dt>{tr('Thời gian điều tra')}</dt>
              <dd>{formatDuration(incident.createdAt, incident.mitigatingAt)}</dd>
              <dt>{tr('Thời gian khắc phục')}</dt>
              <dd>{formatDuration(incident.mitigatingAt, incident.resolvedAt)}</dd>
              <dt>{tr('SLA bước hiện tại')}</dt>
              <dd>
                {incident.sla ? (
                  <>
                    <SlaBadge sla={incident.sla} />{' '}
                    <span className="muted">
                      đã {incident.sla.elapsedHours}h / ngưỡng {incident.sla.thresholdHours}h
                    </span>
                  </>
                ) : (
                  <span className="muted">{tr('Sự cố đã đóng, đồng hồ SLA đã dừng')}</span>
                )}
              </dd>
            </dl>
            )}
          </section>

          <section className="incident-block">
            <h5>{tr('Lịch sử trạng thái')}</h5>
            <p className="card-hint">
              Mỗi lần chuyển trạng thái thành công sinh đúng một dòng, ghi trong cùng transaction với
              việc đổi trạng thái (BR-BIZ-06). Bảng này chỉ ghi thêm, không sửa và không xóa được.
            </p>
            {history.length === 0 ? (
              <div className="empty">{tr('Chưa có lần chuyển trạng thái nào.')}</div>
            ) : (
              <ul className="timeline">
                {history.map((entry) => (
                  <li key={entry.id}>
                    <div>
                      <strong>{tr(STATUS_LABEL[entry.fromStatus])}</strong> →{' '}
                      <strong>{tr(STATUS_LABEL[entry.toStatus])}</strong>
                    </div>
                    <div className="muted">
                      {entry.changedBy.displayName} · {formatTime(entry.changedAt)}
                    </div>
                    {entry.note && <div>{entry.note}</div>}
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className="incident-block">
            <h5>Trao đổi ({comments.length})</h5>
            <p className="card-hint">
              {tr('Kênh hội thoại giữa người gửi và đội xử lý (UC-BIZ-09). Trao đổi không làm thay đổi trạng thái sự cố — vòng đời vẫn đi qua nút chuyển trạng thái.')}
            </p>
            {comments.length === 0 ? (
              <div className="empty">{tr('Chưa có trao đổi nào.')}</div>
            ) : (
              <ul className="timeline">
                {comments.map((comment) => (
                  <li key={comment.id}>
                    <div>
                      <strong>{comment.author.displayName}</strong>{' '}
                      <span className="muted">· {formatTime(comment.createdAt)}</span>
                      <EditTrail
                        lastEdit={comment.lastEdit}
                        loadRevisions={() => api.commentRevisions(project, id, comment.id)}
                      />
                      {(comment.author.id === me || canManageAny) && editingComment !== comment.id
                        && !incident.isDeleted && (
                        <>
                          {' '}
                          <button
                            type="button"
                            className="ghost small"
                            onClick={() => {
                              setEditingComment(comment.id);
                              setCommentDraft(comment.body);
                            }}
                          >
                            {tr('Sửa')}
                          </button>
                        </>
                      )}
                    </div>
                    {editingComment === comment.id ? (
                      <div>
                        <ActiveEditorsNotice editors={commentEditors} />
                        <ActionFeedback action={commentEditAction} processingLabel={tr('Đang lưu trao đổi…')} />
                        <div className="field">
                          <label htmlFor={`edit-comment-${comment.id}`}>{tr('Nội dung')}<RequiredMark /></label>
                          <textarea
                            id={`edit-comment-${comment.id}`}
                            required
                            maxLength={2000}
                            value={commentDraft}
                            onChange={(e) => setCommentDraft(e.target.value)}
                          />
                        </div>
                        <div className="editor-actions">
                          <button type="button" className="btn btn-secondary" onClick={() => setEditingComment(null)}>
                            {tr('Hủy')}
                          </button>
                          <SubmitButton
                            action={commentEditAction}
                            type="button"
                            disabled={!commentDraft.trim()}
                            processingLabel={tr('Đang lưu…')}
                            className="btn btn-primary"
                            onClick={() => saveComment(comment.id, comment.version)}
                          >
                            {tr('Lưu')}
                          </SubmitButton>
                        </div>
                      </div>
                    ) : (
                      <div>{comment.body}</div>
                    )}
                  </li>
                ))}
              </ul>
            )}
            {canComment && !incident.isDeleted && (
              <form onSubmit={postComment}>
                <ActionFeedback action={commentAction} processingLabel={tr('Đang gửi trao đổi…')} />
                <div className="field">
                  <label htmlFor="comment">{tr('Viết trao đổi (tối đa 2000 ký tự)')}<RequiredMark /></label>
                  <textarea
                    id="comment"
                    required
                    maxLength={2000}
                    value={commentBody}
                    onChange={(e) => setCommentBody(e.target.value)}
                  />
                </div>
                <SubmitButton
                  action={commentAction}
                  disabled={!commentBody.trim()}
                  processingLabel={tr('Đang gửi…')} className="btn btn-primary">
                  {tr('Gửi trao đổi')}
                </SubmitButton>
              </form>
            )}
          </section>

          {canReadFeedback && (
            <section className="incident-block">
              <h5>Phản hồi khách hàng đã gắn ({feedbacks.length})</h5>
              {feedbacks.length === 0 ? (
                <div className="empty">{tr('Chưa có phản hồi nào gắn vào sự cố này.')}</div>
              ) : (
                <div className="table-wrap">
                  <table>
                    <thead>
                      <tr>
                        <th>{tr('Kênh')}</th>
                        <th>{tr('Nội dung')}</th>
                        <th>{tr('Trạng thái')}</th>
                        <th>{tr('Liên hệ')}</th>
                        <th>{tr('Ghi nhận')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {feedbacks.map((f) => (
                        <tr key={f.id}>
                          <td>{f.channel}</td>
                          <td>{f.content}</td>
                          <td>
                            <FeedbackStatusBadge status={f.status} />
                          </td>
                          <td className="muted">{f.customerEmail ?? '—'}</td>
                          <td className="muted">{formatTime(f.createdAt)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </section>
          )}
        </div>

        <aside className="sidebar">
          <Section title={tr('Trạng thái')}>
            <div className="sb-row">
              <StatusBadge status={incident.status} />
              <SeverityBadge severity={incident.severity} />
            </div>
            <div className="sb-row muted">
              <span>{tr('Ghi nhận lúc')}</span>
              <span>{formatTime(incident.createdAt)}</span>
            </div>
            {incident.mitigatingAt && (
              <div className="sb-row muted">
                <span>{tr('Bắt đầu khắc phục')}</span>
                <span>{formatTime(incident.mitigatingAt)}</span>
              </div>
            )}
            {incident.resolvedAt && (
              <div className="sb-row muted">
                <span>{tr('Đóng lúc')}</span>
                <span>{formatTime(incident.resolvedAt)}</span>
              </div>
            )}
            <div className="sb-row muted">
              <span>{tr('Người xử lý')}</span>
              <span>{incident.assignee?.displayName ?? <UnassignedAvatar size="sm" withText />}</span>
            </div>
            {incident.sla && (
              <div className="sb-row">
                <SlaBadge sla={incident.sla} />
                <span className="muted">
                  {tr('{v0}h / ngưỡng {v1}h', { v0: incident.sla.elapsedHours, v1: incident.sla.thresholdHours })}
                </span>
              </div>
            )}
            {!incident.allowedNextStatus && (
              <span className="none">{tr('Sự cố đã ở trạng thái cuối.')}</span>
            )}
          </Section>

          {/*
            Thao tác chuyển trạng thái chỉ dựng cho người có quyền. Người không có quyền — khách
            hàng là chính — vẫn thấy đủ thông tin trạng thái ở trên, nhưng không thấy ô ghi chú,
            không thấy nút, và không thấy tên permission còn thiếu. Backend vẫn chặn như cũ; đây
            thuần là chuyện đừng bày ra thứ người ta không dùng được.
          */}
          {incident.allowedNextStatus && canDoNext && (
            <Section title={tr('Chuyển trạng thái')}>
              <span className="muted">
                {tr('Bước hợp lệ tiếp theo:')} <strong>{tr(STATUS_LABEL[incident.allowedNextStatus])}</strong>
              </span>
              <div className="field">
                <label htmlFor="note">{tr('Ghi chú (tùy chọn, tối đa 500 ký tự)')}</label>
                <textarea
                  id="note"
                  maxLength={500}
                  placeholder={tr('Ghi chú nội bộ')}
                  value={note}
                  onChange={(e) => setNote(e.target.value)}
                />
              </div>
              <ActionFeedback
                action={transitionAction}
                processingLabel={tr('Đang chuyển trạng thái…')}
              />
              <SubmitButton
                action={transitionAction}
                type="button"
                className="btn btn-primary btn-block"
                processingLabel={tr('Đang chuyển…')}
                onClick={transition}
              >
                {tr('Chuyển sang {v0}', { v0: tr(STATUS_LABEL[incident.allowedNextStatus]) })}
              </SubmitButton>
            </Section>
          )}

          {canAssign && incident.status !== 'Resolved' && candidates.length > 0 && (
            <Section title={tr('Gán người xử lý')}>
              <ActionFeedback action={assignAction} processingLabel={tr('Đang gán người xử lý…')} />
              <form onSubmit={doAssign}>
                <div className="field">
                  <label htmlFor="assignee">{tr('Kỹ thuật viên')}</label>
                  <Select
                    id="assignee"
                    value={assignee}
                    placeholder={tr('Chọn')}
                    onChange={setAssignee}
                    options={candidates.map((user) => ({
                      value: user.id,
                      label: tr('{v0} (@{v1})', { v0: user.displayName, v1: user.login }),
                    }))}
                  />
                </div>
                <SubmitButton
                  action={assignAction}
                  disabled={!assignee}
                  className="btn btn-primary btn-block"
                  processingLabel={tr('Đang gán…')}
                >
                  {tr('Gán')}
                </SubmitButton>
              </form>
            </Section>
          )}

          {transferTargets.length > 0 && (
            <Section title={tr('Chuyển project')}>
              <ActionFeedback action={transferAction} processingLabel={tr('Đang chuyển project…')} />
              <form onSubmit={doTransfer}>
                <div className="field">
                  <label htmlFor="transfer-to">{tr('Project đích')}</label>
                  <Select
                    id="transfer-to"
                    value={transferTo}
                    placeholder={tr('Chọn')}
                    onChange={setTransferTo}
                    options={transferTargets}
                  />
                </div>
                <SubmitButton
                  action={transferAction}
                  disabled={!transferTo}
                  className="btn btn-primary btn-block"
                  processingLabel={tr('Đang chuyển…')}
                >
                  {tr('Chuyển')}
                </SubmitButton>
              </form>
            </Section>
          )}
        </aside>
      </div>
    </>
  );
}

/** Khối trong cột phải — cùng khuôn với sidebar của màn hình Issues. */
function Section({ title, children }: { title: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="sb-section">
      <div className="sb-title"><span>{title}</span></div>
      <div className="sb-body">{children}</div>
    </div>
  );
}

export default function IncidentDetailPage() {
  const params = useParams<{ id: string }>();

  return (
    <Guard permission="incident.read">
      <IncidentDetail id={params.id} />
    </Guard>
  );
}
