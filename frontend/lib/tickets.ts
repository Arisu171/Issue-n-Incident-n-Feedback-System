/**
 * Client cho API module Tickets — Architecture v3.1 (mục 6.5). Quy ước:
 * - POST tạo ticket/comment tự sinh `Idempotency-Key` (BR-REL-01).
 * - GET ticket đọc `ETag`; PATCH gửi `If-Match` để nhận 412 khi phiên bản lệch (BR-CONC-01).
 * - Lỗi là ProblemDetails (ApiError) — 409 có `blocking_items`, 422 có `errors`, 200 có `warnings`.
 */
import { API_BASE, ApiError, session } from '@/lib/api';
import { tr } from '@/lib/i18n';

export type TicketState = 'OPEN' | 'CLOSED';
export type StateReason = 'COMPLETED' | 'NOT_PLANNED' | 'DUPLICATE' | 'REOPENED';
export type LockReason = 'OFF_TOPIC' | 'TOO_HEATED' | 'RESOLVED' | 'SPAM';
export type ReactionType = 'THUMBS_UP' | 'THUMBS_DOWN' | 'LAUGH' | 'HOORAY' | 'CONFUSED' | 'HEART' | 'ROCKET' | 'EYES';
export type Priority = 'P0' | 'P1' | 'P2' | 'P3';
export type HideReason = 'ABUSE' | 'OFF_TOPIC' | 'OUTDATED' | 'RESOLVED' | 'SPAM' | 'DUPLICATE';

export interface UserSummary { id: string; login: string; displayName: string }

export interface ProjectMember { userId: string; login: string; displayName: string; roles: string[] }
export interface ProjectAccess { members: ProjectMember[]; roleAccess: string[] }
export interface Label { id: string; name: string; colorHex: string; description: string | null; isDefault: boolean; isArchived: boolean }
export interface Milestone {
  id: string; number: number; title: string; description: string | null; dueOn: string | null;
  state: TicketState; closedAt: string | null; openCount: number; closedCount: number; createdAt: string;
}
export interface IssueType { id: string; name: string; color: string; description: string | null; isEnabled: boolean }
export interface TicketRef { id: string; projectSlug: string; number: number; title: string; state: TicketState; stateReason: StateReason | null }
export interface SubIssuesSummary { total: number; completed: number; percentCompleted: number }

export interface Ticket {
  id: string; projectSlug: string; number: number; title: string; body: string; bodyHtml: string;
  author: UserSummary; authorAssociation: string; state: TicketState; stateReason: StateReason | null;
  duplicateOf: TicketRef | null; closedBy: UserSummary | null; closedAt: string | null;
  locked: boolean; activeLockReason: LockReason | null; pinned: boolean;
  type: IssueType | null; parent: TicketRef | null; milestone: Milestone | null;
  labels: Label[]; assignees: UserSummary[]; assignee: UserSummary | null;
  priority: Priority | null; slaDueAt: string | null; firstResponseAt: string | null;
  comments: number; reactions: Record<string, number>; viewerReactions: ReactionType[]; subIssuesSummary: SubIssuesSummary;
  version: number; createdAt: string; updatedAt: string; warnings: string[];
}

export interface TimelineEvent {
  sequence: number; id: string; ticketId: string; ticketVersion: number; eventType: string;
  actor: UserSummary | null; payload: Record<string, unknown>; body: string | null; bodyHtml: string | null;
  visibility: 'PUBLIC' | 'INTERNAL' | 'ACTOR_ONLY'; createdAt: string;
  isEdited: boolean; editedAt: string | null; isHidden: boolean; hiddenReason: HideReason | null;
  reactions: Record<string, number> | null; viewerReactions: ReactionType[] | null; authorAssociation: string | null;
}

export interface Comment {
  id: string; sequence: number; ticketId: string; author: UserSummary | null; authorAssociation: string;
  body: string; bodyHtml: string; createdAt: string; editedAt: string | null; isHidden: boolean; hiddenReason: HideReason | null;
  reactions: Record<string, number>; viewerReactions: ReactionType[];
}

export interface CursorPage<T> { items: T[]; nextCursor: string | null; totalCount: number | null }

/** Đường liên hệ hiện ở màn hình chọn template, thay cho việc mở ticket. */
export interface ContactLink { name: string; url: string; about?: string }

export interface Project {
  id: string; slug: string; name: string; description: string | null; blankIssuesEnabled: boolean;
  strictClosePolicy: boolean; autoReopenOnCustomerComment: boolean; customersSeeOnlyOwn: boolean;
  contactLinks: ContactLink[]; isArchived: boolean; openTickets: number; closedTickets: number; createdAt: string;
}

export interface Template {
  id: string; projectSlug: string; name: string; description: string | null; titlePrefix: string | null;
  defaults: { labels?: string[]; assignees?: string[]; type?: string | null; projects?: string[] };
  body: TemplateElement[]; isEnabled: boolean; position: number;
}
export interface TemplateElement {
  type: 'markdown' | 'input' | 'textarea' | 'dropdown' | 'checkboxes'; id?: string;
  attributes: { label?: string; description?: string; placeholder?: string; value?: string; render?: string; options?: (string | { label: string; required?: boolean })[]; multiple?: boolean; default?: number };
  validations?: { required?: boolean };
}

export interface BoardColumn { id: string; name: string; color: string | null; position: number; itemCount: number }
export interface Board {
  id: string; name: string; description: string | null; visibility: 'PRIVATE' | 'INTERNAL';
  automation: { item_closed_to_column_id?: string; auto_add_query?: string | null }; isClosed: boolean;
  createdBy: UserSummary | null; createdAt: string; columns: BoardColumn[]; itemCount: number;
}
export interface BoardItem {
  id: string; columnId: string | null; position: number; draftTitle: string | null; ticket: TicketRef | null;
  labels: Label[]; assignees: UserSummary[]; addedAt: string;
}
export interface BoardDetail { board: Board; items: BoardItem[] }

export interface NotificationThread {
  id: string; ticket: TicketRef; reason: string; unread: boolean; isDone: boolean; isSaved: boolean;
  lastEventType: string | null; lastActor: UserSummary | null; lastReadAt: string | null; updatedAt: string;
}

export interface ReactionSummary { counts: Record<string, number>; viewerReactions: ReactionType[]; total: number }
export interface SubIssue { ticket: TicketRef; position: number; summary: SubIssuesSummary }
export interface Webhook { id: string; project: string | null; targetUrl: string; events: string[]; isActive: boolean; consecutiveFailures: number; createdAt: string; secret: string | null }
export interface WebhookDelivery {
  id: string; event: string; action: string; httpStatus: number | null; durationMs: number | null; error: string | null;
  isRedelivery: boolean; attempt: number; nextAttemptAt: string | null; createdAt: string; deliveredAt: string | null; requestBody: string; responseBody: string | null;
}
export interface SlaPolicy { id: string; priority: Priority; responseTimeMinutes: number; resolutionTimeMinutes: number; escalateAfterMinutes: number; isActive: boolean }
export interface PresignedUpload { uploadUrl: string; method: string; headers: Record<string, string>; key: string; publicUrl: string; expiresAt: string; maxBytes: number }

/** Kết quả kèm ETag để lần PATCH sau gửi If-Match. */
export interface WithEtag<T> { data: T; etag: string | null }

function idempotencyKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

async function call<T>(path: string, init: RequestInit & { idempotent?: boolean; ifMatch?: string | null } = {}): Promise<WithEtag<T>> {
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');
  if (init.body) headers.set('Content-Type', 'application/json');
  const token = session.token();
  if (token) headers.set('Authorization', `Bearer ${token}`);
  if (init.idempotent) headers.set('Idempotency-Key', idempotencyKey());
  if (init.ifMatch) headers.set('If-Match', init.ifMatch);

  const response = await fetch(`${API_BASE}${path}`, { ...init, headers, cache: 'no-store' });

  if (response.status === 401 && typeof window !== 'undefined') {
    session.clear();
    if (!window.location.pathname.startsWith('/login')) window.location.href = '/login?expired=1';
  }

  const etag = response.headers.get('ETag');
  if (response.status === 204) return { data: null as unknown as T, etag };
  const text = await response.text();
  const payload = text ? JSON.parse(text) : {};
  if (!response.ok) {
    throw new ApiError(response.status, payload.title ?? tr('Lỗi'), payload.detail ?? payload.title ?? `HTTP ${response.status}`, payload);
  }
  return { data: payload as T, etag };
}

const get = <T,>(path: string) => call<T>(path).then((r) => r.data);
const json = (body: unknown) => JSON.stringify(body);

export function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') search.set(k, String(v));
  const s = search.toString();
  return s ? `?${s}` : '';
}

export const tickets = {
  // ---- projects ----
  projects: (includeArchived = false) =>
    get<Project[]>(`/api/projects${includeArchived ? '?includeArchived=true' : ''}`),
  project: (slug: string) => get<Project>(`/api/projects/${slug}`),
  createProject: (body: { slug: string; name: string; description?: string; isArchived?: boolean }) => call<Project>('/api/projects', { method: 'POST', body: json(body) }).then((r) => r.data),
  deleteProject: (slug: string) => call<void>(`/api/projects/${slug}`, { method: 'DELETE' }),
  updateProject: (slug: string, body: Partial<Pick<Project, 'name' | 'description' | 'blankIssuesEnabled' | 'strictClosePolicy' | 'autoReopenOnCustomerComment' | 'customersSeeOnlyOwn' | 'isArchived' | 'contactLinks'>>) =>
    call<Project>(`/api/projects/${slug}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),

  // ---- tickets ----
  list: (slug: string, params: { state?: string; labels?: string; milestone?: string; assignee?: string; author?: string; type?: string; q?: string; sort?: string; direction?: string; cursor?: string; per_page?: number }) =>
    get<CursorPage<Ticket>>(`/api/projects/${slug}/tickets${qs(params)}`),
  get: (slug: string, number: number) => call<Ticket>(`/api/projects/${slug}/tickets/${number}`),
  create: (slug: string, body: { title: string; body?: string; labels?: string[]; assignees?: string[]; milestone?: number; type?: string; priority?: Priority; templateId?: string; formAnswers?: Record<string, unknown>; parentNumber?: number }) =>
    call<Ticket>(`/api/projects/${slug}/tickets`, { method: 'POST', body: json(body), idempotent: true }),
  update: (slug: string, number: number, body: { title?: string; body?: string; state?: 'open' | 'closed'; stateReason?: string; duplicateOf?: string; labels?: string[]; assignees?: string[]; milestone?: number; type?: string; priority?: Priority; clearPriority?: boolean }, ifMatch?: string | null) =>
    call<Ticket>(`/api/projects/${slug}/tickets/${number}`, { method: 'PATCH', body: json(body), ifMatch }),
  remove: (slug: string, number: number) => call<void>(`/api/projects/${slug}/tickets/${number}`, { method: 'DELETE' }),
  timeline: (slug: string, number: number, cursor?: string | null) => get<CursorPage<TimelineEvent>>(`/api/projects/${slug}/tickets/${number}/timeline${qs({ cursor, per_page: 100 })}`),
  lock: (slug: string, number: number, lockReason?: LockReason | null) => call<Ticket>(`/api/projects/${slug}/tickets/${number}/lock`, { method: 'PUT', body: json({ lockReason }) }).then((r) => r.data),
  unlock: (slug: string, number: number) => call<Ticket>(`/api/projects/${slug}/tickets/${number}/lock`, { method: 'DELETE' }).then((r) => r.data),
  pin: (slug: string, number: number, pinned: boolean) => call<Ticket>(`/api/projects/${slug}/tickets/${number}/pin`, { method: pinned ? 'PUT' : 'DELETE' }).then((r) => r.data),
  transfer: (slug: string, number: number, toProject: string) => call<Ticket>(`/api/projects/${slug}/tickets/${number}/transfer`, { method: 'POST', body: json({ toProject }) }).then((r) => r.data),

  // ---- comments ----
  comment: (slug: string, number: number, body: string) => call<Comment>(`/api/projects/${slug}/tickets/${number}/comments`, { method: 'POST', body: json({ body }), idempotent: true }).then((r) => r.data),
  internalNote: (slug: string, number: number, body: string) => call<Comment>(`/api/projects/${slug}/tickets/${number}/internal-notes`, { method: 'POST', body: json({ body }), idempotent: true }).then((r) => r.data),
  editComment: (slug: string, number: number, id: string, body: string) => call<Comment>(`/api/projects/${slug}/tickets/${number}/comments/${id}`, { method: 'PATCH', body: json({ body }) }).then((r) => r.data),
  deleteComment: (slug: string, number: number, id: string) => call<void>(`/api/projects/${slug}/tickets/${number}/comments/${id}`, { method: 'DELETE' }),
  hideComment: (slug: string, number: number, id: string, reason: HideReason) => call<Comment>(`/api/projects/${slug}/tickets/${number}/comments/${id}/minimize`, { method: 'PUT', body: json({ reason }) }).then((r) => r.data),
  unhideComment: (slug: string, number: number, id: string) => call<Comment>(`/api/projects/${slug}/tickets/${number}/comments/${id}/minimize`, { method: 'DELETE' }).then((r) => r.data),
  commentEdits: (slug: string, number: number, id: string) => get<{ eventId: string; editor: UserSummary | null; body: string; editedAt: string; isCurrent: boolean }[]>(`/api/projects/${slug}/tickets/${number}/comments/${id}/edits`),

  // ---- reactions ----
  toggleReaction: (slug: string, number: number, content: ReactionType, commentId?: string) =>
    call<ReactionSummary>(`/api/projects/${slug}/tickets/${number}${commentId ? `/comments/${commentId}` : ''}/reactions/toggle`, { method: 'POST', body: json({ content }) }).then((r) => r.data),

  // ---- labels / milestones / types / assignees ----
  labels: (slug: string) => get<Label[]>(`/api/projects/${slug}/labels`),
  createLabel: (slug: string, body: { name: string; color?: string; description?: string }) => call<Label>(`/api/projects/${slug}/labels`, { method: 'POST', body: json(body) }).then((r) => r.data),
  updateLabel: (slug: string, name: string, body: { newName?: string; color?: string; description?: string }) => call<Label>(`/api/projects/${slug}/labels/${encodeURIComponent(name)}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  deleteLabel: (slug: string, name: string) => call<void>(`/api/projects/${slug}/labels/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  milestones: (slug: string, state: 'open' | 'closed' | 'all' = 'open') => get<Milestone[]>(`/api/projects/${slug}/milestones?state=${state}`),
  createMilestone: (slug: string, body: { title: string; description?: string; dueOn?: string }) => call<Milestone>(`/api/projects/${slug}/milestones`, { method: 'POST', body: json(body) }).then((r) => r.data),
  updateMilestone: (slug: string, number: number, body: { title?: string; description?: string; dueOn?: string; clearDueOn?: boolean; state?: 'open' | 'closed' }) => call<Milestone>(`/api/projects/${slug}/milestones/${number}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  deleteMilestone: (slug: string, number: number) => call<void>(`/api/projects/${slug}/milestones/${number}`, { method: 'DELETE' }),
  issueTypes: (includeDisabled = false) => get<IssueType[]>(`/api/issue-types${qs({ includeDisabled })}`),
  createIssueType: (body: { name: string; color?: string; description?: string }) => call<IssueType>('/api/issue-types', { method: 'POST', body: json(body) }).then((r) => r.data),
  updateIssueType: (id: string, body: { name: string; color?: string; description?: string; isEnabled?: boolean }) => call<IssueType>(`/api/issue-types/${id}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  addAssignees: (slug: string, number: number, assignees: string[]) => call<Ticket>(`/api/projects/${slug}/tickets/${number}/assignees`, { method: 'POST', body: json({ assignees }) }).then((r) => r.data),
  removeAssignees: (slug: string, number: number, assignees: string[]) => call<Ticket>(`/api/projects/${slug}/tickets/${number}/assignees`, { method: 'DELETE', body: json({ assignees }) }).then((r) => r.data),

  // ---- templates ----
  templates: (slug: string, includeDisabled = false) => get<Template[]>(`/api/projects/${slug}/templates${qs({ includeDisabled })}`),
  createTemplate: (slug: string, body: unknown) => call<Template>(`/api/projects/${slug}/templates`, { method: 'POST', body: json(body) }).then((r) => r.data),
  updateTemplate: (slug: string, id: string, body: unknown) => call<Template>(`/api/projects/${slug}/templates/${id}`, { method: 'PUT', body: json(body) }).then((r) => r.data),
  deleteTemplate: (slug: string, id: string) => call<void>(`/api/projects/${slug}/templates/${id}`, { method: 'DELETE' }),

  // ---- relations ----
  subIssues: (slug: string, number: number) => get<SubIssue[]>(`/api/projects/${slug}/tickets/${number}/sub_issues`),
  addSubIssue: (slug: string, number: number, subIssue: string) => call<SubIssue[]>(`/api/projects/${slug}/tickets/${number}/sub_issues`, { method: 'POST', body: json({ subIssue }) }).then((r) => r.data),
  removeSubIssue: (slug: string, number: number, subIssue: string) => call<void>(`/api/projects/${slug}/tickets/${number}/sub_issues`, { method: 'DELETE', body: json({ subIssue }) }),
  blockedBy: (slug: string, number: number) => get<TicketRef[]>(`/api/projects/${slug}/tickets/${number}/dependencies/blocked_by`),
  blocking: (slug: string, number: number) => get<TicketRef[]>(`/api/projects/${slug}/tickets/${number}/dependencies/blocking`),
  addBlockedBy: (slug: string, number: number, issue: string) => call<TicketRef[]>(`/api/projects/${slug}/tickets/${number}/dependencies/blocked_by`, { method: 'POST', body: json({ issue }) }).then((r) => r.data),
  removeBlockedBy: (slug: string, number: number, issue: string) => call<void>(`/api/projects/${slug}/tickets/${number}/dependencies/blocked_by`, { method: 'DELETE', body: json({ issue }) }),

  // ---- boards ----
  boards: (includeClosed = false) => get<Board[]>(`/api/boards${qs({ includeClosed })}`),
  board: (id: string) => get<BoardDetail>(`/api/boards/${id}`),
  createBoard: (body: { name: string; description?: string }) => call<Board>('/api/boards', { method: 'POST', body: json(body) }).then((r) => r.data),
  updateBoard: (id: string, body: { name: string; description?: string; automation?: unknown; isClosed?: boolean }) => call<Board>(`/api/boards/${id}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  addColumn: (id: string, name: string) => call<Board>(`/api/boards/${id}/columns`, { method: 'POST', body: json({ name }) }).then((r) => r.data),
  // `name` là trường bắt buộc của BoardColumnRequest, nên đổi vị trí vẫn phải gửi kèm tên.
  updateColumn: (id: string, columnId: string, body: { name: string; position?: number; color?: string }) =>
    call<Board>(`/api/boards/${id}/columns/${columnId}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  deleteColumn: (id: string, columnId: string) =>
    call<Board>(`/api/boards/${id}/columns/${columnId}`, { method: 'DELETE' }).then((r) => r.data),
  addBoardItem: (id: string, body: { ticket?: string; draftTitle?: string; columnId?: string }) => call<BoardItem>(`/api/boards/${id}/items`, { method: 'POST', body: json(body) }).then((r) => r.data),
  moveBoardItem: (id: string, itemId: string, body: { columnId?: string; position?: number }) => call<BoardItem>(`/api/boards/${id}/items/${itemId}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  removeBoardItem: (id: string, itemId: string) => call<void>(`/api/boards/${id}/items/${itemId}`, { method: 'DELETE' }),

  // ---- subscriptions / notifications ----
  subscription: (ticketId: string) => get<{ ticketId: string; subscribed: boolean; ignored: boolean; reason: string | null }>(`/api/notifications/threads/${ticketId}/subscription`),
  setSubscription: (ticketId: string, subscribed: boolean, ignored = false) => call<{ subscribed: boolean; ignored: boolean }>(`/api/notifications/threads/${ticketId}/subscription`, { method: 'PUT', body: json({ subscribed, ignored }) }).then((r) => r.data),
  projectWatch: (slug: string) => get<{ project: string; level: string }>(`/api/projects/${slug}/subscription`),
  setProjectWatch: (slug: string, level: 'ALL' | 'PARTICIPATING' | 'IGNORE') => call<{ level: string }>(`/api/projects/${slug}/subscription`, { method: 'PUT', body: json({ level }) }).then((r) => r.data),
  notifications: (params: { all?: boolean; participating?: boolean; saved?: boolean; done?: boolean; q?: string; cursor?: string }) => get<CursorPage<NotificationThread>>(`/api/notifications${qs(params)}`),
  unreadCount: () => get<{ unreadCount: number }>('/api/notifications/unread-count'),
  patchNotification: (id: string, body: { unread?: boolean; saved?: boolean; done?: boolean }) => call<NotificationThread>(`/api/notifications/threads/${id}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  markAllRead: () => call<void>('/api/notifications/mark-read', { method: 'POST' }),
  markTicketRead: (ticketId: string) => call<void>(`/api/notifications/tickets/${ticketId}/mark-read`, { method: 'POST' }),

  // ---- search ----
  search: (params: { q: string; sort?: string; cursor?: string; per_page?: number }) => get<CursorPage<Ticket>>(`/api/search/tickets${qs(params)}`),

  // ---- storage ----
  presign: (fileName: string, contentType: string, contentLength: number, project?: string) =>
    get<PresignedUpload>(`/api/storage/presigned-url${qs({ file_name: fileName, content_type: contentType, content_length: contentLength, project })}`),
  /**
   * `project` quyết định tệp nằm ở đâu trên đĩa: có thì `projects/{slug}/…`, không thì
   * `shared/…`. Ảnh đại diện không thuộc project nào nên cố ý bỏ trống.
   */
  async upload(file: File, project?: string): Promise<{ key: string; url: string }> {
    const type = file.type || 'application/octet-stream';
    const presigned = await tickets.presign(file.name, type, file.size, project);
    const response = await fetch(presigned.uploadUrl, { method: presigned.method, headers: { 'Content-Type': type }, body: file });
    if (!response.ok) throw new ApiError(response.status, tr('Upload thất bại'), tr('Không tải được tệp ({v0}).', { v0: response.status }), {});
    return { key: presigned.key, url: presigned.publicUrl };
  },

  // ---- thành viên project ----
  // Quyền gắn theo project: `members` cấp cho từng người (nhân viên), `roleAccess` cấp một lần
  // cho cả vai trò (khách hàng — cấp từng người thì với hàng nghìn khách là việc không ai làm nổi).
  members: (slug: string) => get<ProjectAccess>(`/api/projects/${slug}/members`),
  memberCandidates: (slug: string, search?: string) =>
    get<UserSummary[]>(`/api/projects/${slug}/member-candidates${qs({ search })}`),
  addMember: (slug: string, login: string, role: string) =>
    call<void>(`/api/projects/${slug}/members/${login}/roles/${role}`, { method: 'PUT' }),
  removeMember: (slug: string, login: string, role: string) =>
    call<void>(`/api/projects/${slug}/members/${login}/roles/${role}`, { method: 'DELETE' }),
  setRoleAccess: (slug: string, role: string, granted: boolean) =>
    call<void>(`/api/projects/${slug}/role-access/${role}`, { method: granted ? 'PUT' : 'DELETE' }),

  // ---- webhooks / sla ----
  webhooks: (slug: string) => get<Webhook[]>(`/api/projects/${slug}/webhooks`),
  createWebhook: (slug: string, body: { targetUrl: string; events?: string[]; secret?: string }) => call<Webhook>(`/api/projects/${slug}/webhooks`, { method: 'POST', body: json(body) }).then((r) => r.data),
  updateWebhook: (slug: string, id: string, body: { targetUrl: string; events?: string[]; isActive?: boolean }) => call<Webhook>(`/api/projects/${slug}/webhooks/${id}`, { method: 'PATCH', body: json(body) }).then((r) => r.data),
  deleteWebhook: (slug: string, id: string) => call<void>(`/api/projects/${slug}/webhooks/${id}`, { method: 'DELETE' }),
  deliveries: (slug: string, id: string) => get<WebhookDelivery[]>(`/api/projects/${slug}/webhooks/${id}/deliveries`),
  redeliver: (slug: string, id: string, deliveryId: string) => call<WebhookDelivery>(`/api/projects/${slug}/webhooks/${id}/deliveries/${deliveryId}/redeliver`, { method: 'POST' }).then((r) => r.data),
  ping: (slug: string, id: string) => call<WebhookDelivery>(`/api/projects/${slug}/webhooks/${id}/pings`, { method: 'POST' }).then((r) => r.data),
  slaPolicies: () => get<SlaPolicy[]>('/api/sla-policies'),
  putSlaPolicy: (priority: Priority, body: { responseTimeMinutes: number; resolutionTimeMinutes: number; escalateAfterMinutes: number; isActive: boolean }) => call<SlaPolicy>(`/api/sla-policies/${priority}`, { method: 'PUT', body: json(body) }).then((r) => r.data),

  // ---- người gán được (cho assignee picker) ----
  // Trước đây gọi `/api/users`, mà endpoint đó đòi `user.read` — chỉ admin có — nên support và
  // responder mở ô chọn là nhận 403. Nó cũng trả cả email và vai trò của mọi tài khoản, thừa xa
  // so với một danh sách gợi ý.
  assignableUsers: (slug: string, search?: string) =>
    get<UserSummary[]>(`/api/projects/${slug}/assignable-users${qs({ search })}`),
};
