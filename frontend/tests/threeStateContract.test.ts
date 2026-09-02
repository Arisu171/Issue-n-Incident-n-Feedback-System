import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Test kiến trúc cho yêu cầu "Tất cả events 3 status".
 *
 * Ba test hành vi ở hai file kia chứng minh hook và component làm đúng. Test này giải quyết
 * một rủi ro khác: sáu tháng sau, một người thêm màn hình mới và lại tự cài cặp
 * busy/notice/error riêng — đúng cái đã khiến 5 trên 9 thao tác thiếu trạng thái trước đây.
 *
 * Quét mã nguồn để bảo đảm điều đó không tái diễn mà không cần ai nhớ ra.
 */

const APP_DIR = join(process.cwd(), 'app');
const COMPONENTS_DIR = join(process.cwd(), 'components');

/** Các phương thức của api client làm thay đổi dữ liệu — mỗi lời gọi là một "event". */
const MUTATING_CALLS = [
  'api.login',
  'api.register',
  'api.createIncident',
  'api.updateStatus',
  'api.assign',
  'api.deleteIncident',
  'api.createFeedback',
  'api.linkFeedback',
  'api.replyFeedback',
  'api.createComment',
  'api.createUser',
  'api.updateUser',
  'api.assignRole',
  'api.removeRole',
  'api.createRole',
  'api.updateRole',
  'api.deleteRole',
  'api.assignPermission',
  'api.removePermission',
  'api.createPermission',
  'api.deletePermission',
  'api.transferIncident',
  'api.transferFeedback',
  'api.joinProject',
  'api.leaveProject',
];

function collectSources(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      out.push(...collectSources(full));
    } else if (/\.tsx?$/.test(entry)) {
      out.push(full);
    }
  }
  return out;
}

const sources = [...collectSources(APP_DIR), ...collectSources(COMPONENTS_DIR)].map((path) => ({
  path: path.replace(process.cwd(), '').replace(/\\/g, '/'),
  code: readFileSync(path, 'utf8'),
}));

describe('Hợp đồng ba trạng thái trên toàn bộ giao diện', () => {
  it('tìm thấy mã nguồn để quét', () => {
    expect(sources.length).toBeGreaterThan(5);
  });

  it('không màn hình nào tự cài lại cặp busy/notice riêng', () => {
    const offenders = sources
      .filter(({ code }) => /\bconst \[(busy|notice)\b|\bsetBusy\(|\bsetNotice\(/.test(code))
      .map(({ path }) => path);

    expect(offenders, 'Dùng useAction thay vì tự quản lý trạng thái').toEqual([]);
  });

  it('mọi lời gọi API làm thay đổi dữ liệu đều nằm trong một thao tác có ba trạng thái', () => {
    const offenders: string[] = [];

    for (const { path, code } of sources) {
      const used = MUTATING_CALLS.filter((call) => code.includes(`${call}(`));
      if (used.length === 0) {
        continue;
      }

      // File có gọi API ghi thì bắt buộc phải đi qua useAction.
      if (!code.includes('useAction')) {
        offenders.push(`${path} gọi ${used.join(', ')} nhưng không dùng useAction`);
      }
    }

    expect(offenders).toEqual([]);
  });

  it('mọi màn hình có thao tác ghi đều hiển thị phản hồi ba trạng thái cho người dùng', () => {
    const offenders: string[] = [];

    for (const { path, code } of sources) {
      if (!code.includes('useAction') || path.includes('/components/')) {
        continue;
      }
      if (!code.includes('<ActionFeedback')) {
        offenders.push(`${path} dùng useAction nhưng không render <ActionFeedback>`);
      }
    }

    expect(offenders).toEqual([]);
  });

  it('mỗi thao tác thành công đều kèm thông báo cho người dùng', () => {
    const offenders: string[] = [];

    for (const { path, code } of sources) {
      // Bỏ qua các lượt tải dữ liệu: chúng cố ý không có thông báo thành công vì
      // chính dữ liệu hiện ra đã là phản hồi.
      // GIỚI HẠN ĐÃ BIẾT: phép so kết thúc ở `\n  );`, nên một lời gọi kết thúc bằng
      // `}, message);` trên cùng một dòng sẽ không khớp ở đó mà nuốt tiếp phần mã phía sau —
      // và có thể cho qua vì đoạn nuốt được tình cờ chứa một chuỗi. Siết lại thì lộ ra vài chỗ
      // truyền thông báo dạng biến hoặc biểu thức điều kiện, đều là thông báo thật; muốn siết
      // cho đúng thì phải phân tích cú pháp chứ không so chuỗi. Chưa làm.
      const runCalls = [...code.matchAll(/(\w+)\.run\(([\s\S]*?)\n\s*\);/g)];

      for (const [, hookName, body] of runCalls) {
        if (hookName === 'load') {
          continue;
        }
        // Thông báo có thể là literal, `tr('…')`, hoặc một biến — thường là tham số mang giá trị
        // mặc định `message = tr('…')`.
        const hasMessage = /,\s*\n?\s*(tr\(\s*)?['"`]/.test(body) || /,\s*[A-Za-z_$][\w$]*,?\s*$/.test(body.trimEnd());
        if (!hasMessage) {
          offenders.push(`${path}: ${hookName}.run(...) thiếu thông báo thành công`);
        }
      }
    }

    expect(offenders).toEqual([]);
  });
});
