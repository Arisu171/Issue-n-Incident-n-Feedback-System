import { tr } from '@/lib/i18n';
/**
 * Client gọi API. Frontend chỉ ẩn/hiện giao diện cho thân thiện; quyết định quyền cuối cùng
 * luôn nằm ở API (Hình 4, ISS-07).
 */

/**
 * Địa chỉ API, NƯỚNG VÀO BUNDLE lúc build — `NEXT_PUBLIC_*` không đọc lại lúc chạy.
 *
 * Vì sao có nhánh ném lỗi: nếu chỉ để `?? 'http://localhost:8080'` thì một bản build production
 * quên đặt biến sẽ **âm thầm** gọi về localhost của chính máy người dùng. Không có lỗi, không có
 * log, chỉ là mọi lời gọi API đều hỏng theo cách khó đoán nhất. Thà dừng ngay lúc build.
 */
/**
 * Thông điệp lỗi lúc BUILD, không phải chuỗi giao diện — nó chỉ hiện trong log build cho lập
 * trình viên. Khai báo riêng ở đây để bộ kiểm i18n có đúng một literal để bỏ qua.
 */
const BUILD_ERROR_MISSING_API_BASE =
  'Thiếu NEXT_PUBLIC_API_BASE_URL khi build production. Đặt biến này ở nơi build (Vercel: Settings → Environment Variables; Docker: --build-arg), rồi build lại — giá trị được nhúng vào bundle nên khởi động lại không có tác dụng.';

function apiBase(): string {
  const configured = process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/$/, '');
  if (configured) return configured;

  if (process.env.NODE_ENV === 'production') {
    throw new Error(BUILD_ERROR_MISSING_API_BASE);
  }

  return 'http://localhost:8080';
}

export const API_BASE = apiBase();

const TOKEN_KEY = 'it.accessToken';
const EXPIRES_KEY = 'it.tokenExpiresAt';
const USER_KEY = 'it.currentUser';

export type IncidentStatus = 'Investigating' | 'Mitigating' | 'Resolved';
export type IncidentSeverity = 'Low' | 'Medium' | 'High' | 'Critical';
export type FeedbackChannel = 'Email' | 'Hotline' | 'Web' | 'Other';
/** Vòng đời tiếp nhận của một phản hồi: Mới → Đã tiếp nhận → Đã trả lời (BR-BIZ-12). */
export type FeedbackStatus = 'New' | 'Acknowledged' | 'Responded';

export interface UserRef {
  id: string;
  displayName: string;
  email: string;
}

export interface SlaStatus {
  breached: boolean;
  thresholdHours: number | null;
  elapsedHours: number;
  remainingHours: number | null;
}

export interface Incident {
  id: string;
  title: string;
  description: string | null;
  severity: IncidentSeverity;
  status: IncidentStatus;
  allowedNextStatus: IncidentStatus | null;
  reporter: UserRef;
  assignee: UserRef | null;
  createdAt: string;
  mitigatingAt: string | null;
  resolvedAt: string | null;
  resolver: UserRef | null;
  isDeleted: boolean;
  /** null khi sự cố đã đóng — lúc đó đồng hồ SLA đã dừng. */
  sla: SlaStatus | null;
}

export interface StatusHistoryEntry {
  id: string;
  fromStatus: IncidentStatus;
  toStatus: IncidentStatus;
  changedBy: UserRef;
  changedAt: string;
  note: string | null;
}

export interface Feedback {
  id: string;
  channel: FeedbackChannel;
  customerEmail: string | null;
  content: string;
  status: FeedbackStatus;
  incidentId: string | null;
  incidentTitle: string | null;
  incidentStatus: IncidentStatus | null;
  createdBy: string;
  createdByName: string;
  createdAt: string;
}

/** Một câu trả lời trong luồng hội thoại của phản hồi. `responder` null khi là lời xác nhận tự động. */
export interface FeedbackReply {
  id: string;
  feedbackId: string;
  responder: UserRef | null;
  isAutomatic: boolean;
  body: string;
  createdAt: string;
}

/** Một bình luận trong luồng trao đổi của sự cố (UC-BIZ-09). */
export interface IncidentComment {
  id: string;
  incidentId: string;
  author: UserRef;
  body: string;
  createdAt: string;
}

export interface AppUser {
  id: string;
  email: string;
  login: string;
  displayName: string;
  isActive: boolean;
  createdAt: string;
  roles: string[];
  /** Cấp = rank cao nhất trong các role; chỉ tác động được lên cấp thấp hơn mình (BR-SEC-08). */
  level: number;
}

export interface Role {
  id: string;
  name: string;
  description: string | null;
  /** Cấp của vai trò — càng lớn càng cao. */
  rank: number;
  permissions: string[];
}

export interface Permission {
  id: string;
  code: string;
  description: string | null;
}

/**
 * Hồ sơ nhìn từ bên ngoài. `email` và `phone` là `null` khi chủ tài khoản chưa bật cho người khác
 * xem — khác với chuỗi rỗng nghĩa là chưa điền, nên giao diện phân biệt được hai trường hợp.
 *
 * `emailVisible`/`phoneVisible` chỉ có giá trị khi tự xem hồ sơ mình.
 */
/** online · snooze · offline. `invisible` không bao giờ tới máy người khác — server trả offline. */
export type PresenceStatus = 'online' | 'snooze' | 'offline' | 'invisible';

export interface Profile {
  id: string;
  login: string;
  displayName: string;
  avatarUrl: string | null;
  status: PresenceStatus;
  biography: string | null;
  email: string | null;
  phone: string | null;
  createdAt: string;
  roles: string[];
  isSelf: boolean;
  emailVisible: boolean | null;
  phoneVisible: boolean | null;
}

export interface RegistrationPolicy {
  enabled: boolean;
  allowedEmailDomains: string[];
  requiresApproval: boolean;
  minPasswordLength: number;
  defaultRoles: string[];
}

export interface CurrentUser {
  id: string;
  email: string;
  /** Tên ngắn dùng cho @mention (Architecture v3.1 mục 5.1). */
  login: string;
  displayName: string;
  roles: string[];
  permissions: string[];
}

interface RegisterResponse {
  userId: string;
  email: string;
  login: string;
  displayName: string;
  roles: string[];
  requiresApproval: boolean;
  /** null khi tài khoản còn chờ admin duyệt. */
  session: LoginResponse | null;
}

interface LoginResponse {
  accessToken: string;
  tokenType: string;
  expiresInSeconds: number;
  expiresAt: string;
  userId: string;
  email: string;
  login: string;
  displayName: string;
  roles: string[];
  permissions: string[];
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** Lỗi chuẩn ProblemDetails của API, giữ nguyên các trường mở rộng như allowedNextStatus. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    message: string,
    readonly problem: Record<string, unknown>,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  get allowedNextStatus(): string | null {
    const value = this.problem['allowedNextStatus'];
    return typeof value === 'string' ? value : null;
  }

  get correlationId(): string | null {
    const value = this.problem['correlationId'];
    return typeof value === 'string' ? value : null;
  }
}

export const session = {
  token: () => (typeof window === 'undefined' ? null : localStorage.getItem(TOKEN_KEY)),
  user: (): CurrentUser | null => {
    if (typeof window === 'undefined') return null;
    const raw = localStorage.getItem(USER_KEY);
    if (!raw) return null;
    try {
      return JSON.parse(raw) as CurrentUser;
    } catch {
      // Hồ sơ hỏng (bị sửa tay, hoặc còn lại từ một phiên bản cũ có cấu trúc khác). Ném ra
      // giữa lúc dựng giao diện thì cả trang trắng; dọn đi rồi coi như chưa đăng nhập là
      // đường ra duy nhất người dùng tự đi được.
      session.clear();
      return null;
    }
  },

  /**
   * Đã đăng nhập hay chưa — **nguồn sự thật duy nhất** cho câu hỏi đó.
   *
   * Phải có ĐỦ cả token lẫn hồ sơ. Trước đây mỗi nơi hỏi một nửa: `Guard` hỏi hồ sơ, trang
   * đăng nhập hỏi token. Khi hai nửa lệch nhau — có token mà mất hồ sơ — hai bên đá nhau vô
   * hạn: Guard thấy thiếu hồ sơ nên đẩy sang `/login`, trang đăng nhập thấy còn token nên đẩy
   * về `/`, `/` chuyển tiếp vào một màn hình có Guard, và vòng lặp khép lại. Người dùng chỉ
   * thấy màn hình nháy "Đang tải…" mãi không dừng.
   *
   * Gặp nửa phiên thì **dọn hẳn** thay vì chỉ trả `false`: để nguyên là để lại đúng cái mồi
   * cho lần điều hướng sau. Vì có ghi, chỉ gọi trong effect hoặc trình xử lý sự kiện — đừng
   * gọi giữa lúc dựng giao diện.
   */
  isAuthenticated(): boolean {
    if (typeof window === 'undefined') return false;
    const token = session.token();
    const user = session.user();
    if (token && user) return true;
    if (token || user) session.clear();
    return false;
  },
  save(token: string, user: CurrentUser, expiresAt?: string) {
    localStorage.setItem(TOKEN_KEY, token);
    localStorage.setItem(USER_KEY, JSON.stringify(user));
    if (expiresAt) localStorage.setItem(EXPIRES_KEY, expiresAt);
  },
  /** Hạn của token hiện tại, mốc thời gian; `null` khi chưa biết. */
  expiresAt: (): number | null => {
    if (typeof window === 'undefined') return null;
    const raw = localStorage.getItem(EXPIRES_KEY);
    const at = raw ? Date.parse(raw) : Number.NaN;
    return Number.isNaN(at) ? null : at;
  },
  clear() {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    localStorage.removeItem(EXPIRES_KEY);
  },
  can(permission: string): boolean {
    return session.user()?.permissions.includes(permission) ?? false;
  },
};

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = session.token();
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');
  if (init.body) headers.set('Content-Type', 'application/json');
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const response = await fetch(`${API_BASE}${path}`, { ...init, headers, cache: 'no-store' });

  // ADR-001: token chỉ sống 15 phút và không có refresh token, nên hết hạn thì đưa về màn đăng nhập.
  if (response.status === 401 && typeof window !== 'undefined') {
    session.clear();
    if (!window.location.pathname.startsWith('/login')) {
      window.location.href = '/login?expired=1';
    }
  }

  // 204 phải trả về một giá trị KHÁC undefined: useAction dùng undefined làm tín hiệu thất
  // bại, nên call site kiểm `ok !== undefined` — trả undefined ở đây từng khiến xóa mềm không
  // điều hướng và gán role không refresh dù thao tác đã thành công.
  if (response.status === 204) return null as unknown as T;

  const text = await response.text();
  const payload = text ? JSON.parse(text) : {};

  if (!response.ok) {
    throw new ApiError(
      response.status,
      payload.title ?? tr('Lỗi'),
      payload.detail ?? payload.title ?? `HTTP ${response.status}`,
      payload,
    );
  }

  return payload as T;
}

function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value));
  }
  const qs = search.toString();
  return qs ? `?${qs}` : '';
}

/** Một project khách hàng có thể tham gia. `joined` là ô tick trên tab Projects. */
export interface CatalogProject {
  slug: string;
  name: string;
  description: string | null;
  joined: boolean;
}

export const api = {
  /**
   * Cấp lại token cho phiên đang chạy. Trả `false` khi máy chủ từ chối (tài khoản bị vô hiệu
   * hóa, hoặc phiên chạm trần tuyệt đối) — lúc đó phải đăng nhập lại.
   */
  async refreshSession(): Promise<boolean> {
    try {
      const result = await request<LoginResponse>('/api/auth/refresh', { method: 'POST' });
      const user: CurrentUser = {
        id: result.userId,
        email: result.email,
        login: result.login,
        displayName: result.displayName,
        roles: result.roles,
        permissions: result.permissions,
      };
      session.save(result.accessToken, user, result.expiresAt);
      return true;
    } catch {
      return false;
    }
  },

  /**
   * `identifier` là email **hoặc** username. Backend nhận diện bằng dấu `@`: username theo quy
   * tắc GitHub chỉ gồm chữ thường, số và gạch nối nên không bao giờ chứa nó.
   */
  async login(identifier: string, password: string): Promise<CurrentUser> {
    const result = await request<LoginResponse>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ identifier, password }),
    });

    const user: CurrentUser = {
      id: result.userId,
      email: result.email,
      login: result.login,
      displayName: result.displayName,
      roles: result.roles,
      permissions: result.permissions,
    };
    session.save(result.accessToken, user, result.expiresAt);
    return user;
  },

  /**
   * UC-08 — tự đăng ký. Tài khoản active thì API cấp luôn phiên làm việc, nên người dùng vào
   * thẳng được mà không phải đăng nhập lại.
   */
  async register(body: {
    email: string;
    username: string;
    displayName: string;
    password: string;
  }): Promise<{ requiresApproval: boolean; user: CurrentUser | null }> {
    const result = await request<RegisterResponse>('/api/auth/register', {
      method: 'POST',
      body: JSON.stringify(body),
    });

    if (!result.session) {
      return { requiresApproval: true, user: null };
    }

    const user: CurrentUser = {
      id: result.session.userId,
      email: result.session.email,
      login: result.session.login,
      displayName: result.session.displayName,
      roles: result.session.roles,
      permissions: result.session.permissions,
    };
    session.save(result.session.accessToken, user);
    return { requiresApproval: false, user };
  },

  registrationPolicy: () => request<RegistrationPolicy>('/api/auth/registration-policy'),

  /**
   * Đổi mật khẩu. Trả về phiên mới cho **chính** phiên đang đổi — mọi phiên khác của tài khoản
   * này chết ngay. Phải thay token đang giữ bằng token trả về, nếu không lượt gọi tiếp theo sẽ
   * bị chính thay đổi này đá ra.
   */
  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    const result = await request<LoginResponse>('/api/auth/password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    });
    session.save(result.accessToken, {
      id: result.userId,
      email: result.email,
      login: result.login,
      displayName: result.displayName,
      roles: result.roles,
      permissions: result.permissions,
    }, result.expiresAt);
  },

  profile: (login: string) => request<Profile>(`/api/profiles/${encodeURIComponent(login)}`),

  updateProfile: (body: Partial<{
    displayName: string;
    biography: string;
    phone: string;
    avatarKey: string;
    /** 'Online' | 'Snooze' | 'Invisible' | 'Offline' — tên enum của backend, viết hoa chữ đầu. */
    presenceStatus: string;
    emailVisible: boolean;
    phoneVisible: boolean;
  }>) => request<Profile>('/api/profiles/me', { method: 'PATCH', body: JSON.stringify(body) }),

  /**
   * Đồng bộ session với quyền hiện hành (PrincipalEnrichmentMiddleware nạp từ DB mỗi request).
   * Giữ nguyên access token; chỉ cập nhật roles/permissions trong localStorage.
   */
  async me(): Promise<CurrentUser | null> {
    const token = session.token();
    if (!token) return null;

    const result = await request<{
      id: string;
      email: string;
      login: string;
      displayName: string;
      roles: string[];
      permissions: string[];
    }>('/api/auth/me');

    const user: CurrentUser = {
      id: result.id,
      email: result.email,
      login: result.login,
      displayName: result.displayName,
      roles: result.roles,
      permissions: result.permissions,
    };
    session.save(token, user);
    return user;
  },

  /**
   * Sự cố và phản hồi giờ thuộc về một project, nên mọi lời gọi đều mang slug.
   *
   * Slug đặc biệt `uncategorized` trỏ tới những bản ghi chưa gắn project nào — nó là project ảo,
   * không có hàng trong bảng projects, và chỉ quản trị viên hay người quản lý project mới mở được.
   */
  listIncidents: (project: string, params: {
    q?: string;
    status?: string;
    assigneeId?: string;
    severity?: string;
    createdFrom?: string;
    createdTo?: string;
    slaBreachedOnly?: boolean;
    page?: number;
    pageSize?: number;
  }) => request<Paged<Incident>>(`/api/projects/${project}/incidents${query(params)}`),

  getIncident: (project: string, id: string) => request<Incident>(`/api/projects/${project}/incidents/${id}`),

  getHistory: (project: string, id: string) =>
    request<StatusHistoryEntry[]>(`/api/projects/${project}/incidents/${id}/history`),

  listComments: (project: string, id: string) =>
    request<IncidentComment[]>(`/api/projects/${project}/incidents/${id}/comments`),

  createComment: (project: string, id: string, body: string) =>
    request<IncidentComment>(`/api/projects/${project}/incidents/${id}/comments`, {
      method: 'POST',
      body: JSON.stringify({ body }),
    }),

  createIncident: (project: string, body: { title: string; description?: string; severity: IncidentSeverity }) =>
    request<Incident>(`/api/projects/${project}/incidents`, { method: 'POST', body: JSON.stringify(body) }),

  updateStatus: (project: string, id: string, targetStatus: IncidentStatus, note?: string) =>
    request<Incident>(`/api/projects/${project}/incidents/${id}/status`, {
      method: 'PATCH',
      body: JSON.stringify({ targetStatus, note }),
    }),

  assign: (project: string, id: string, userId: string) =>
    request<void>(`/api/projects/${project}/incidents/${id}/assignee/${userId}`, { method: 'PUT' }),

  deleteIncident: (project: string, id: string) =>
    request<void>(`/api/projects/${project}/incidents/${id}`, { method: 'DELETE' }),

  listFeedbacks: (project: string, params: { q?: string; unlinkedOnly?: boolean; incidentId?: string; page?: number; pageSize?: number }) =>
    request<Paged<Feedback>>(`/api/projects/${project}/feedbacks${query(params)}`),

  createFeedback: (project: string, body: {
    channel: FeedbackChannel;
    customerEmail?: string;
    content: string;
    incidentId?: string;
  }) => request<Feedback>(`/api/projects/${project}/feedbacks`, { method: 'POST', body: JSON.stringify(body) }),

  /**
   * Project đang mở cho vai trò của tôi, kèm cờ đã tham gia.
   *
   * Chỉ có ý nghĩa với khách hàng: đây là danh mục dịch vụ họ tự nhận mình đang dùng. Nhân viên
   * được cấp thẳng vào project nên danh sách này rỗng với họ.
   */
  projectCatalog: () => request<CatalogProject[]>('/api/project-catalog'),

  joinProject: (slug: string) =>
    request<null>(`/api/project-catalog/${slug}`, { method: 'PUT' }),

  leaveProject: (slug: string) =>
    request<null>(`/api/project-catalog/${slug}`, { method: 'DELETE' }),

  transferIncident: (project: string, id: string, toProject: string) =>
    request<Incident>(`/api/projects/${project}/incidents/${id}/transfer`, {
      method: 'POST',
      body: JSON.stringify({ toProject }),
    }),

  transferFeedback: (project: string, id: string, toProject: string) =>
    request<Feedback>(`/api/projects/${project}/feedbacks/${id}/transfer`, {
      method: 'POST',
      body: JSON.stringify({ toProject }),
    }),

  linkFeedback: (project: string, id: string, incidentId: string) =>
    request<Feedback>(`/api/projects/${project}/feedbacks/${id}/link`, {
      method: 'POST',
      body: JSON.stringify({ incidentId }),
    }),

  /** Gỡ phản hồi khỏi sự cố đang gắn. Idempotent — gỡ thứ vốn chưa gắn vẫn trả về bình thường. */
  unlinkFeedback: (project: string, id: string) =>
    request<Feedback>(`/api/projects/${project}/feedbacks/${id}/link`, { method: 'DELETE' }),

  listReplies: (project: string, id: string) =>
    request<FeedbackReply[]>(`/api/projects/${project}/feedbacks/${id}/replies`),

  replyFeedback: (project: string, id: string, body: string) =>
    request<FeedbackReply>(`/api/projects/${project}/feedbacks/${id}/replies`, {
      method: 'POST',
      body: JSON.stringify({ body }),
    }),

  listUsers: (params: { search?: string; isActive?: boolean; page?: number; pageSize?: number }) =>
    request<Paged<AppUser>>(`/api/users${query(params)}`),

  /**
   * Người nhận được sự cố. Không dùng `listUsers` cho ô chọn kỹ thuật viên: nó trả **mọi** tài
   * khoản đang hoạt động, trong đó có khách hàng — mà backend đòi người nhận phải có
   * `incident.update_status`, nên chọn khách hàng chỉ dẫn tới 409.
   */
  incidentAssignableUsers: (project: string, search?: string) =>
    request<{ id: string; login: string; displayName: string }[]>(
      `/api/projects/${project}/incidents/assignable-users${query({ search })}`),

  createUser: (body: {
    email: string;
    displayName: string;
    password: string;
    roles: string[];
  }) => request<AppUser>('/api/users', { method: 'POST', body: JSON.stringify(body) }),

  updateUser: (id: string, body: { displayName?: string; isActive?: boolean }) =>
    request<AppUser>(`/api/users/${id}`, { method: 'PATCH', body: JSON.stringify(body) }),

  deleteUser: (id: string) => request<void>(`/api/users/${id}`, { method: 'DELETE' }),

  assignRole: (userId: string, roleId: string) =>
    request<void>(`/api/users/${userId}/roles/${roleId}`, { method: 'PUT' }),

  removeRole: (userId: string, roleId: string) =>
    request<void>(`/api/users/${userId}/roles/${roleId}`, { method: 'DELETE' }),

  listRoles: () => request<Role[]>('/api/roles'),

  createRole: (body: { name: string; description?: string; rank: number }) =>
    request<Role>('/api/roles', { method: 'POST', body: JSON.stringify(body) }),

  updateRole: (id: string, body: { description?: string }) =>
    request<Role>(`/api/roles/${id}`, { method: 'PATCH', body: JSON.stringify(body) }),

  deleteRole: (id: string) => request<void>(`/api/roles/${id}`, { method: 'DELETE' }),

  assignPermission: (roleId: string, permissionId: string) =>
    request<void>(`/api/roles/${roleId}/permissions/${permissionId}`, { method: 'PUT' }),

  removePermission: (roleId: string, permissionId: string) =>
    request<void>(`/api/roles/${roleId}/permissions/${permissionId}`, { method: 'DELETE' }),

  listPermissions: () => request<Permission[]>('/api/permissions'),

  createPermission: (body: { code: string; description?: string }) =>
    request<Permission>('/api/permissions', { method: 'POST', body: JSON.stringify(body) }),

  updatePermission: (id: string, body: { description?: string }) =>
    request<Permission>(`/api/permissions/${id}`, {
      method: 'PATCH',
      body: JSON.stringify(body),
    }),

  deletePermission: (id: string) =>
    request<void>(`/api/permissions/${id}`, { method: 'DELETE' }),
};

export const STATUS_LABEL: Record<IncidentStatus, string> = {
  Investigating: 'Đang điều tra',
  Mitigating: 'Đang khắc phục',
  Resolved: 'Đã giải quyết',
};

export const SEVERITY_LABEL: Record<IncidentSeverity, string> = {
  Low: 'Thấp',
  Medium: 'Trung bình',
  High: 'Cao',
  Critical: 'Nghiêm trọng',
};

export const CHANNEL_LABEL: Record<FeedbackChannel, string> = {
  Email: 'Email',
  Hotline: 'Tổng đài',
  Web: 'Web',
  Other: 'Khác',
};

export const FEEDBACK_STATUS_LABEL: Record<FeedbackStatus, string> = {
  New: 'Mới',
  Acknowledged: 'Đã tiếp nhận',
  Responded: 'Đã trả lời',
};

/** Lưu ở UTC, hiển thị theo giờ địa phương (giả định mục 1.3). */
export function formatTime(value: string | null): string {
  if (!value) return '—';
  return new Date(value).toLocaleString('vi-VN', { dateStyle: 'short', timeStyle: 'medium' });
}

/** Khoảng thời gian giữa hai mốc, dùng để đọc nhanh thời gian mỗi bước xử lý. */
export function formatDuration(from: string | null, to: string | null): string {
  if (!from || !to) return '—';
  const ms = new Date(to).getTime() - new Date(from).getTime();
  if (ms < 0) return '—';
  const minutes = Math.floor(ms / 60000);
  if (minutes < 60) return tr('{v0} phút', { v0: minutes });
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h${String(minutes % 60).padStart(2, '0')}`;
  return tr('{v0} ngày {v1}h', { v0: Math.floor(hours / 24), v1: hours % 24 });
}
