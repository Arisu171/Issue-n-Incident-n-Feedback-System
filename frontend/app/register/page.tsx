'use client';

import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { api, session, type RegistrationPolicy } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ActionFeedback, ErrorBox, PasswordInput, RequiredMark, SubmitButton } from '@/components/ui';
import { tr } from '@/lib/i18n';

/**
 * UC-08 · Tự đăng ký tài khoản.
 *
 * Trang đọc chính sách đăng ký từ API trước khi hiện biểu mẫu, thay vì đoán: tên miền cho
 * phép, độ dài mật khẩu tối thiểu và việc có phải chờ duyệt hay không đều là cấu hình phía
 * máy chủ, nên giao diện phải hỏi chứ không được hardcode.
 */
export default function RegisterPage() {
  const router = useRouter();

  const [policy, setPolicy] = useState<RegistrationPolicy | null>(null);
  const [email, setEmail] = useState('');
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [mismatch, setMismatch] = useState(false);
  const [pendingApproval, setPendingApproval] = useState(false);

  const loadPolicy = useAction<RegistrationPolicy>();
  const register = useAction<{ requiresApproval: boolean }>();

  const runLoad = useCallback(async () => {
    const result = await loadPolicy.run(() => api.registrationPolicy());
    if (result) setPolicy(result);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Đang có phiên thì không cần đăng ký nữa; xem ghi chú ở trang đăng nhập.
  useEffect(() => {
    if (session.token()) router.replace('/');
  }, [router]);

  useEffect(() => {
    void runLoad();
  }, [runLoad]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();

    // Kiểm tra tại chỗ: gửi lên rồi mới báo lệch mật khẩu là lãng phí một vòng mạng.
    if (password !== confirm) {
      setMismatch(true);
      return;
    }
    setMismatch(false);

    const result = await register.run(
      () => api.register({ email, username, displayName, password }),
      tr('Đăng ký thành công.'),
    );

    if (!result) return;

    if (result.requiresApproval) {
      setPendingApproval(true);
    } else {
      router.push('/');
    }
  }

  if (loadPolicy.isProcessing) {
    return <div className="empty">{tr('Đang tải…')}</div>;
  }

  if (policy && !policy.enabled) {
    return (
      <div style={{ maxWidth: 460, margin: '64px auto' }}>
        <div className="card">
          <div className="kicker">{tr('Tài khoản')}</div>
          <h2>{tr('Tự đăng ký đang tắt')}</h2>
          <p className="card-hint">
            {tr('Hệ thống này không mở đăng ký. Liên hệ quản trị viên để được cấp tài khoản.')}
          </p>
          <Link href="/login">{tr('← Quay lại đăng nhập')}</Link>
        </div>
      </div>
    );
  }

  if (pendingApproval) {
    return (
      <div style={{ maxWidth: 460, margin: '64px auto' }}>
        <div className="card">
          <div className="kicker">{tr('Tài khoản')}</div>
          <h2>{tr('Đã ghi nhận đăng ký')}</h2>
          <div className="alert info">
            {tr('Tài khoản')} <strong>{email}</strong> {tr('đã được tạo nhưng cần quản trị viên kích hoạt trước khi đăng nhập được. Bạn sẽ dùng được ngay khi được duyệt.')}
          </div>
          <Link href="/login">{tr('← Quay lại đăng nhập')}</Link>
        </div>
      </div>
    );
  }

  return (
    <div style={{ maxWidth: 460, margin: '64px auto' }}>
      <div className="card">
        <div className="kicker">{tr('Tài khoản')}</div>
        <h2>{tr('Tạo tài khoản')}</h2>
        <p className="card-hint">
          {tr('Không cần liên hệ quản trị viên. Tài khoản mới là')} <strong>{tr('khách hàng')}</strong>: gửi
          được sự cố và phản hồi, theo dõi được tiến độ xử lý, và chỉ nhìn thấy đúng những gì
          mình đã gửi. Việc xử lý sự cố thuộc về đội hỗ trợ; muốn tham gia xử lý thì quản trị
          viên cấp thêm vai trò sau.
        </p>

        {loadPolicy.status === 'failed' && <ErrorBox error={loadPolicy.error} />}
        <ActionFeedback action={register} processingLabel={tr('Đang tạo tài khoản…')} />

        {mismatch && <div className="alert error">{tr('Hai lần nhập mật khẩu không khớp nhau.')}</div>}

        {policy && policy.allowedEmailDomains.length > 0 && (
          <div className="alert info">
            {tr('Chỉ nhận email thuộc tên miền:')} <strong>{policy.allowedEmailDomains.join(', ')}</strong>
          </div>
        )}

        <form onSubmit={submit}>
          <div className="field">
            <label htmlFor="email">Email<RequiredMark /></label>
            <input
              id="email"
              type="email"
              autoComplete="username"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </div>

          <div className="field">
            <label htmlFor="username">Username<RequiredMark /></label>
            <input
              id="username"
              required
              maxLength={39}
              // Cùng quy tắc với LoginNames.ValidLogin() ở backend. Kiểm ở đây chỉ để báo sớm;
              // backend vẫn là nơi quyết định, nên hai bên phải cùng một biểu thức.
              pattern="[a-z0-9]([a-z0-9]|-(?=[a-z0-9])){0,38}"
              autoComplete="username"
              value={username}
              onChange={(e) => setUsername(e.target.value.toLowerCase())}
            />
            <span className="hint">
              {tr('Chữ thường, số và gạch nối. Dùng cho @mention và để đăng nhập.')}
            </span>
          </div>

          <div className="field">
            <label htmlFor="displayName">{tr('Tên hiển thị')}<RequiredMark /></label>
            <input
              id="displayName"
              required
              maxLength={150}
              autoComplete="name"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
            />
          </div>

          <div className="field">
            <label htmlFor="password">
              Mật khẩu {policy && tr('(tối thiểu {v0} ký tự)', { v0: policy.minPasswordLength })}
            <RequiredMark /></label>
            <PasswordInput
              id="password"
              autoComplete="new-password"
              required
              minLength={policy?.minPasswordLength ?? 8}
              value={password}
              onChange={setPassword}
            />
          </div>

          <div className="field">
            <label htmlFor="confirm">{tr('Nhập lại mật khẩu')}<RequiredMark /></label>
            <PasswordInput
              id="confirm"
              autoComplete="new-password"
              required
              value={confirm}
              onChange={(v) => {
                setConfirm(v);
                setMismatch(false);
              }}
            />
          </div>

          <SubmitButton action={register} processingLabel={tr('Đang tạo…')} className="btn btn-primary">
            {tr('Tạo tài khoản')}
          </SubmitButton>
        </form>

        <p className="muted" style={{ marginTop: 16 }}>
          {tr('Đã có tài khoản?')} <Link href="/login">{tr('Đăng nhập')}</Link>
        </p>
      </div>
    </div>
  );
}
