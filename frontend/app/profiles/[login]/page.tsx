'use client';

import { useParams } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import { ActionFeedback, ErrorBox, Guard, NotFoundView, PageHead, PasswordInput, RequiredMark, Select, SubmitButton, isNotFound } from '@/components/ui';
import { api, type Profile } from '@/lib/api';
import { PresenceDot } from '@/components/tickets/Bits';
import { tickets } from '@/lib/tickets';
import { useAction } from '@/lib/useAction';
import { initials } from '@/lib/util';
import { tr } from '@/lib/i18n';

/**
 * Hồ sơ người dùng.
 *
 * Thứ tự trường theo chốt của chủ dự án: Avatar · Tên hiển thị · Biography · Username · Email ·
 * Phone · Password. (Status thuộc đợt sau.)
 *
 * Ba trường đầu và Username **luôn hiện**. Email và Phone có công tắc, **mặc định tắt** — người
 * dùng cũ chưa hề đồng ý công khai, bật sẵn rồi chờ họ tự tắt là làm ngược.
 *
 * Username cố ý **không** có công tắc: `@login` được in ra ở chip assignee, tác giả ticket, người
 * bình luận và cả cơ chế @mention. Ẩn nó ở đây thì mở bất kỳ ticket nào người đó từng đụng vào
 * vẫn đọc được — một công tắc như vậy chỉ tạo cảm giác riêng tư chứ không giữ được gì.
 */
function ProfileView() {
  const { login } = useParams<{ login: string }>();
  const [profile, setProfile] = useState<Profile | null>(null);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(
    () => api.profile(login).then((p) => { setProfile(p); setError(null); }).catch(setError),
    [login],
  );
  useEffect(() => { void load(); }, [load]);

  // Nhắc tên một người không có thật thì bộ render để nguyên chữ, không thành link — nhưng
  // đường dẫn vẫn gõ tay được, và tài khoản có thể đã bị xoá sau khi bình luận được viết.
  if (isNotFound(error) && !profile) {
    return (
      <NotFoundView
        title={tr('Không có người dùng “{v0}”', { v0: login })}
        hint={tr('Tài khoản này không tồn tại, hoặc đã bị xoá khỏi hệ thống.')}
        backHref="/projects"
        backLabel={tr('Về danh sách dự án')}
      />
    );
  }
  if (error && !profile) return <ErrorBox error={error} />;
  if (!profile) return <div className="empty">{tr('Đang tải…')}</div>;

  return (
    <>
      <PageHead kicker={tr('Hồ sơ')} title={profile.displayName} hint={`@${profile.login}`} />
      {error ? <ErrorBox error={error} /> : null}
      <div className="profile">
        <ProfileHeader profile={profile} onChanged={setProfile} />
        <ProfileFields profile={profile} onChanged={setProfile} onError={setError} />
        {profile.isSelf && <PasswordCard />}
      </div>
    </>
  );
}

function ProfileHeader({ profile, onChanged }: {
  profile: Profile;
  onChanged: (p: Profile) => void;
}) {
  // Qua `useAction` chứ không tự giữ cờ busy: nó là chỗ duy nhất trong dự án gom đủ ba trạng thái
  // đang chạy / xong / hỏng, và có test canh không cho màn hình nào tự dựng lại cặp cờ riêng.
  const upload = useAction<Profile>();

  /**
   * Chỉ nhận png/jpeg/webp. **Không nhận SVG** dù kho lưu trữ cho phép: chỗ phục vụ tệp đã phải
   * hạ SVG xuống `application/octet-stream` để script bên trong không chạy, nên SVG tải lên sẽ
   * không hiện ra ảnh — nhận vào chỉ để người dùng thấy một ô trống.
   */
  async function pick(file: File | null) {
    if (!file) return;
    const next = await upload.run(async () => {
      if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) {
        throw new Error(tr('Ảnh đại diện chỉ nhận PNG, JPEG hoặc WebP.'));
      }
      const { key } = await tickets.upload(file);
      return api.updateProfile({ avatarKey: key });
    }, tr('Đã đổi ảnh đại diện.'));
    if (next) onChanged(next);
  }

  return (
    <div className="profile-head">
      {profile.avatarUrl
        // eslint-disable-next-line @next/next/no-img-element
        ? <img className="profile-avatar" src={profile.avatarUrl} alt="" />
        : <span className="profile-avatar none">{initials(profile.displayName || profile.login)}</span>}

      <div className="grow">
        <div className="profile-name">
          {profile.displayName} <PresenceDot status={profile.status} />
        </div>
        <div className="muted">@{profile.login}</div>
        <div className="profile-roles">
          {profile.roles.length === 0
            ? <span className="tag tag-neutral">{tr('chưa có role')}</span>
            : profile.roles.map((r) => <span key={r} className="tag tag-outline">{r}</span>)}
        </div>
      </div>

      {profile.isSelf && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-2)', alignItems: 'flex-end' }}>
          <label className="btn btn-secondary" style={{ cursor: upload.isProcessing ? 'progress' : 'pointer' }}>
            {upload.isProcessing ? tr('Đang tải lên…') : tr('Đổi ảnh đại diện')}
            <input type="file" accept="image/png,image/jpeg,image/webp" hidden disabled={upload.isProcessing}
              onChange={(e) => void pick(e.target.files?.[0] ?? null)} />
          </label>
          <ActionFeedback action={upload} processingLabel={tr('Đang tải lên…')} />
        </div>
      )}
    </div>
  );
}

/**
 * Trạng thái server trả về là chữ thường (`online`), còn khi gửi lên thì backend nhận tên enum
 * (`Online`). Một bảng tra ở đây thay vì viết hoa chữ đầu tại chỗ: `invisible` chỉ tồn tại theo
 * chiều gửi lên, chiều nhận về không bao giờ có nó với người khác.
 */
const STATUS_TO_ENUM: Record<string, string> = {
  online: 'Online',
  snooze: 'Snooze',
  invisible: 'Invisible',
  offline: 'Offline',
};

function ProfileFields({ profile, onChanged, onError }: {
  profile: Profile;
  onChanged: (p: Profile) => void;
  onError: (e: unknown) => void;
}) {
  const save = useAction<Profile>();
  const [displayName, setDisplayName] = useState(profile.displayName);
  const [biography, setBiography] = useState(profile.biography ?? '');
  const [phone, setPhone] = useState(profile.phone ?? '');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const next = await save.run(
      () => api.updateProfile({ displayName, biography, phone }),
      tr('Đã lưu hồ sơ.'),
    );
    if (next) { onChanged(next); onError(null); }
  }

  async function toggle(field: 'emailVisible' | 'phoneVisible', value: boolean) {
    const next = await save.run(() => api.updateProfile({ [field]: value }), tr('Đã lưu hồ sơ.'));
    if (next) onChanged(next);
  }

  if (!profile.isSelf) {
    return (
      <dl className="profile-facts">
        <dt>Status</dt>
        <dd><PresenceDot status={profile.status} withText /></dd>
        <dt>Biography</dt>
        <dd>{profile.biography || <span className="muted">{tr('chưa có')}</span>}</dd>
        <dt>Username</dt>
        <dd>@{profile.login}</dd>
        <dt>Email</dt>
        <dd>{profile.email ?? <span className="muted">{tr('không công khai')}</span>}</dd>
        <dt>Phone</dt>
        <dd>{profile.phone ?? <span className="muted">{tr('không công khai')}</span>}</dd>
      </dl>
    );
  }

  return (
    <form onSubmit={submit} className="profile-form">
      <ActionFeedback action={save} processingLabel={tr('Đang lưu…')} />

      <div className="field">
        <label htmlFor="p-name">{tr('Tên hiển thị')}<RequiredMark /></label>
        <input id="p-name" className="input" required maxLength={150}
          value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
      </div>

      <div className="field">
        <label htmlFor="p-status">Status</label>
        <Select id="p-status" value={STATUS_TO_ENUM[profile.status] ?? 'Offline'}
          ariaLabel="Status"
          onChange={(v) => void save.run(() => api.updateProfile({ presenceStatus: v }), tr('Đã lưu hồ sơ.'))
            .then((next) => { if (next) onChanged(next); })}
          options={[
            { value: 'Online', label: tr('Đang hoạt động') },
            { value: 'Snooze', label: tr('Bận — đừng làm phiền') },
            { value: 'Invisible', label: tr('Ẩn (người khác thấy là ngoại tuyến)') },
            { value: 'Offline', label: tr('Để hệ thống tự quyết') },
          ]} />
        <span className="hint">
          {tr('“Ẩn” được chặn ở server: người khác nhận đúng chữ ngoại tuyến, không có cách nào suy ra.')}
        </span>
      </div>

      <div className="field">
        <label htmlFor="p-bio">Biography</label>
        <textarea id="p-bio" className="input" maxLength={1000} rows={3}
          value={biography} onChange={(e) => setBiography(e.target.value)} />
      </div>

      <div className="field">
        <label htmlFor="p-login">Username</label>
        <input id="p-login" className="input" value={profile.login} readOnly disabled />
        <span className="hint">{tr('Username hiện ở mọi nơi có @mention nên không ẩn được và không đổi ở đây.')}</span>
      </div>

      <div className="field">
        <label htmlFor="p-email">Email</label>
        <input id="p-email" className="input" value={profile.email ?? ''} readOnly disabled />
        <label className="radio">
          <input type="checkbox" checked={profile.emailVisible ?? false}
            onChange={(e) => void toggle('emailVisible', e.target.checked)} />
          <span className="dot" />
          {tr('Cho người khác thấy email trên hồ sơ')}
        </label>
      </div>

      <div className="field">
        <label htmlFor="p-phone">Phone</label>
        <input id="p-phone" className="input" maxLength={32} inputMode="tel"
          value={phone} onChange={(e) => setPhone(e.target.value)} />
        <label className="radio">
          <input type="checkbox" checked={profile.phoneVisible ?? false}
            onChange={(e) => void toggle('phoneVisible', e.target.checked)} />
          <span className="dot" />
          {tr('Cho người khác thấy số điện thoại trên hồ sơ')}
        </label>
      </div>

      <div className="form-actions">
        <SubmitButton action={save} processingLabel={tr('Đang lưu…')} className="btn btn-primary">
          {tr('Lưu hồ sơ')}
        </SubmitButton>
      </div>
    </form>
  );
}

/**
 * Đổi mật khẩu.
 *
 * Chỉ chủ tài khoản thấy khối này — với người khác nó không tồn tại trong dữ liệu trả về, không
 * phải bị che bằng CSS.
 */
function PasswordCard() {
  const change = useAction<void>();
  const [open, setOpen] = useState(false);
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [mismatch, setMismatch] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (next !== confirm) { setMismatch(true); return; }
    setMismatch(false);
    const ok = await change.run(
      () => api.changePassword(current, next),
      tr('Đã đổi mật khẩu. Các phiên đăng nhập khác của bạn đã bị đăng xuất.'),
    );
    if (ok !== undefined) { setCurrent(''); setNext(''); setConfirm(''); setOpen(false); }
  }

  return (
    <div className="profile-password">
      <div className="field">
        <label htmlFor="p-pass">{tr('Mật khẩu')}</label>
        <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center' }}>
          <input id="p-pass" className="input" type="password" value="••••••••" readOnly disabled
            style={{ flex: 1, minWidth: 0 }} />
          <button type="button" className="btn btn-secondary" onClick={() => setOpen((v) => !v)}>
            {open ? tr('Bỏ') : tr('Đổi mật khẩu')}
          </button>
        </div>
      </div>

      {open && (
        <form onSubmit={submit}>
          <ActionFeedback action={change} processingLabel={tr('Đang đổi…')} />
          {mismatch && <div className="alert error" role="alert">{tr('Hai mật khẩu không khớp.')}</div>}

          <div className="field">
            <label htmlFor="p-cur">{tr('Mật khẩu hiện tại')}<RequiredMark /></label>
            <PasswordInput id="p-cur" className="input" required autoComplete="current-password"
              value={current} onChange={setCurrent} />
          </div>
          <div className="field">
            <label htmlFor="p-new">{tr('Mật khẩu mới')}<RequiredMark /></label>
            <PasswordInput id="p-new" className="input" required autoComplete="new-password"
              value={next} onChange={setNext} />
          </div>
          <div className="field">
            <label htmlFor="p-cfm">{tr('Nhập lại mật khẩu')}<RequiredMark /></label>
            <PasswordInput id="p-cfm" className="input" required autoComplete="new-password"
              value={confirm} onChange={setConfirm} />
          </div>

          <span className="hint">
            {tr('Đổi mật khẩu sẽ đăng xuất mọi thiết bị khác. Thiết bị này thì không.')}
          </span>

          <div className="form-actions">
            <SubmitButton action={change} processingLabel={tr('Đang đổi…')} className="btn btn-primary">
              {tr('Đổi mật khẩu')}
            </SubmitButton>
          </div>
        </form>
      )}
    </div>
  );
}

export default function ProfilePage() {
  // Không khoá theo permission nào: mọi tài khoản đã đăng nhập đều mở được hồ sơ người khác. Thứ
  // được giữ kín là email và số điện thoại, và việc đó do backend quyết định chứ không phải màn
  // hình này.
  return <Guard><ProfileView /></Guard>;
}
