import { beforeEach, describe, expect, it } from 'vitest';
import { session } from '@/lib/api';

const TOKEN = 'it.accessToken';
const USER = 'it.currentUser';

const profile = { id: 'u1', login: 'anh-tu', displayName: 'Võ Anh Tú', email: 'anh.tu@demo.local', roles: ['customer'], permissions: ['ticket.read'] };

beforeEach(() => localStorage.clear());

describe('session.isAuthenticated', () => {
  it('đủ token và hồ sơ thì coi là đã đăng nhập', () => {
    localStorage.setItem(TOKEN, 'abc');
    localStorage.setItem(USER, JSON.stringify(profile));
    expect(session.isAuthenticated()).toBe(true);
  });

  it('trống trơn thì chưa đăng nhập, và không xoá nhầm gì', () => {
    expect(session.isAuthenticated()).toBe(false);
    expect(localStorage.length).toBe(0);
  });

  /**
   * Hồi quy của vòng lặp điều hướng vô hạn.
   *
   * Còn token mà mất hồ sơ: `Guard` (hỏi hồ sơ) đẩy sang `/login`, trang đăng nhập (hỏi token)
   * đẩy về `/`, `/` chuyển tiếp vào màn hình có `Guard` — và vòng lặp khép lại, màn hình nháy
   * "Đang tải…" mãi. Cách duy nhất cắt vòng là cả hai bên hỏi CÙNG một câu, và nửa phiên bị
   * dọn đi thay vì để lại làm mồi cho lần sau.
   */
  it('còn token mà mất hồ sơ: chưa đăng nhập, và nửa phiên bị dọn sạch', () => {
    localStorage.setItem(TOKEN, 'abc');
    expect(session.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(TOKEN)).toBeNull();
  });

  it('còn hồ sơ mà mất token: chưa đăng nhập, và nửa phiên bị dọn sạch', () => {
    localStorage.setItem(USER, JSON.stringify(profile));
    expect(session.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(USER)).toBeNull();
  });

  it('hồ sơ hỏng không làm ném lỗi giữa lúc dựng giao diện', () => {
    localStorage.setItem(TOKEN, 'abc');
    localStorage.setItem(USER, '{ không phải JSON');
    expect(() => session.user()).not.toThrow();
    expect(session.user()).toBeNull();
    expect(session.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(TOKEN)).toBeNull();
  });
});

describe('session.can', () => {
  it('trả false khi chưa đăng nhập thay vì ném lỗi', () => {
    expect(session.can('ticket.read')).toBe(false);
  });

  it('đọc đúng permission của hồ sơ đang lưu', () => {
    localStorage.setItem(USER, JSON.stringify(profile));
    expect(session.can('ticket.read')).toBe(true);
    expect(session.can('user.read')).toBe(false);
  });
});
