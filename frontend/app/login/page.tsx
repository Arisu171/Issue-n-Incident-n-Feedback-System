'use client';

import { Suspense, useCallback, useEffect, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { api, session, type RegistrationPolicy } from '@/lib/api';
import { useAction } from '@/lib/useAction';
import { ActionFeedback, PasswordInput, RequiredMark, SubmitButton } from '@/components/ui';
import { tr } from '@/lib/i18n';

function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const expired = params.get('expired') === '1';

  const [identifier, setIdentifier] = useState('');
  const [password, setPassword] = useState('');
  const [policy, setPolicy] = useState<RegistrationPolicy | null>(null);
  const action = useAction();
  const loadPolicy = useAction<RegistrationPolicy>();

  // Hỏi API xem có mở đăng ký không, thay vì luôn hiện một liên kết có thể dẫn tới ngõ cụt.
  const runLoadPolicy = useCallback(async () => {
    const result = await loadPolicy.run(() => api.registrationPolicy());
    if (result) setPolicy(result);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    void runLoadPolicy();
  }, [runLoadPolicy]);

  // Đang có phiên thì không có việc gì ở đây. `replace` chứ không `push` để nút Quay lại
  // không đưa người dùng trở lại chính cái form vừa bị bỏ qua.
  useEffect(() => {
    // Cùng phép kiểm với `Guard`. Hỏi mỗi token là nửa còn lại của vòng lặp: còn token mà mất
    // hồ sơ thì trang này đẩy về `/`, `Guard` ở đầu kia đẩy ngược lại, và không bên nào chịu dừng.
    if (session.isAuthenticated()) router.replace('/');
  }, [router]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    const user = await action.run(
      () => api.login(identifier, password),
      tr('Đăng nhập thành công, đang chuyển tới danh sách sự cố…'),
    );
    // Chỉ điều hướng khi thao tác thực sự thành công; thất bại thì ở lại để đọc lỗi.
    if (user) {
      router.push('/');
    }
  }

  return (
    <div style={{ maxWidth: 420, margin: '64px auto' }}>
      <div className="card">
        <div className="kicker">{tr('Tài khoản')}</div>
        <h2>{tr('Đăng nhập')}</h2>
        <p className="card-hint">
          {tr('Incident &amp; Feedback Tracker — dùng tài khoản nội bộ do quản trị viên cấp.')}
        </p>

        {expired && (
          <div className="alert info">
            {tr('Phiên làm việc đã kết thúc. Phiên tự gia hạn trong lúc bạn còn thao tác, nên việc này thường xảy ra sau một lúc không dùng tới. Đăng nhập lại để tiếp tục.')}
          </div>
        )}

        <ActionFeedback action={action} processingLabel={tr('Đang xác thực…')} />

        <form onSubmit={submit}>
          <div className="field">
            <label htmlFor="identifier">{tr('Email hoặc username')}<RequiredMark /></label>
            <input
              id="identifier"
              // `type="text"` chứ không phải `email`: để `email` thì trình duyệt tự chặn username
              // ngay trước khi gửi, và người dùng chỉ thấy một bong bóng đỏ không giải thích gì.
              type="text"
              autoComplete="username"
              required
              value={identifier}
              onChange={(e) => setIdentifier(e.target.value)}
            />
          </div>
          <div className="field">
            <label htmlFor="password">{tr('Mật khẩu')}<RequiredMark /></label>
            <PasswordInput
              id="password"
              autoComplete="current-password"
              required
              value={password}
              onChange={setPassword}
            />
          </div>
          <SubmitButton action={action} processingLabel={tr('Đang đăng nhập…')} className="btn btn-primary">
            {tr('Đăng nhập')}
          </SubmitButton>
        </form>

        {policy?.enabled && (
          <p className="muted" style={{ marginTop: 16 }}>
            {tr('Chưa có tài khoản?')} <Link href="/register">{tr('Tạo tài khoản mới')}</Link>
          </p>
        )}
      </div>
    </div>
  );
}

export default function LoginPage() {
  return (
    <Suspense fallback={<div className="empty">{tr('Đang tải…')}</div>}>
      <LoginForm />
    </Suspense>
  );
}
