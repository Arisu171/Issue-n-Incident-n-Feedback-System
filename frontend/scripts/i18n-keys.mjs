/**
 * Liệt kê tập khoá dịch: đối số thứ nhất của mọi lời gọi `tr('…')` trong mã.
 *
 * `node scripts/i18n-keys.mjs`            -> in toàn bộ khoá dạng JSON
 * `node scripts/i18n-keys.mjs --missing`  -> chỉ in khoá chưa có trong messages/en.ts
 * `node scripts/i18n-keys.mjs --stale`    -> chỉ in khoá có trong en.ts mà mã không còn dùng
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const ROOT = new URL('..', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const DIRS = ['app', 'components', 'lib'];
const SKIP = new Set(['lib/i18n.tsx', 'lib/messages/en.ts']);
/** Có dấu tiếng Việt — dùng để nhận ra khoá dùng gián tiếp. */
const VI = /[À-ỹ]/;
/**
 * Chuỗi tiếng Việt cố ý không đi qua i18n. Metadata của trang dựng trên máy chủ, một lần cho
 * mọi người dùng, nên không thể theo ngôn ngữ của từng người nếu không định tuyến theo locale.
 */
const IGNORE = new Set([
  'Nền tảng xử lý sự cố hướng sự kiện, tái hiện trải nghiệm GitHub Issues (Architecture v3.1)',
]);

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) { walk(full, out); continue; }
    if (/\.tsx?$/.test(name)) out.push(full);
  }
  return out;
}

const keys = new Set();
for (const dir of DIRS) {
  for (const file of walk(join(ROOT, dir))) {
    const rel = relative(ROOT, file).replace(/\\/g, '/');
    if (SKIP.has(rel)) continue;
    // Bỏ chú thích trước khi quét: chú thích trong dự án viết bằng tiếng Việt và hay chứa ví dụ
    // (`label:"cần gấp"`), quét vào sẽ báo thiếu bản dịch cho thứ không hiển thị bao giờ.
    const src = readFileSync(file, 'utf8')
      .replace(/\/\*[\s\S]*?\*\//g, '')
      .replace(/(^|[^:'"`\\])\/\/[^\n]*/g, '$1');
    for (const m of src.matchAll(/tr\(\s*'((?:[^'\\]|\\.)*)'/g)) keys.add(m[1].replace(/\\'/g, "'"));
    for (const m of src.matchAll(/tr\(\s*"((?:[^"\\]|\\.)*)"/g)) keys.add(m[1].replace(/\\"/g, '"'));
    // Khoá dùng gián tiếp: các hằng nhãn (STATUS_LABEL, CHANNEL_LABEL, REASON_TEXT…) chỉ chứa
    // chuỗi nguồn và được bọc `tr()` ở nơi hiển thị, nên mọi literal tiếng Việt đều là khoá
    // tiềm năng. Không tính chúng thì `--stale` báo nhầm là thừa.
    for (const m of src.matchAll(/'((?:[^'\\\n]|\\.)*)'/g)) {
      if (VI.test(m[1])) keys.add(m[1].replace(/\\'/g, "'"));
    }
  }
}

for (const k of IGNORE) keys.delete(k);
const all = [...keys].sort((a, b) => a.localeCompare(b, 'vi'));

if (process.argv.includes('--missing') || process.argv.includes('--stale')) {
  const catalog = readFileSync(join(ROOT, 'lib/messages/en.ts'), 'utf8');
  const en = {};
  // Nhận cả nháy đơn lẫn nháy kép: chỉ cần ai chạy formatter với `singleQuote: false` là mọi
  // khoá đổi kiểu nháy, và bản chỉ khớp nháy đơn sẽ âm thầm báo "thiếu toàn bộ bản dịch".
  for (const m of catalog.matchAll(/^\s*'((?:[^'\\]|\\.)*)':/gm)) en[m[1].replace(/\\'/g, "'")] = true;
  for (const m of catalog.matchAll(/^\s*"((?:[^"\\]|\\.)*)":/gm)) en[m[1].replace(/\\"/g, '"')] = true;

  // Chốt an toàn: catalog rỗng gần như chắc chắn là do đọc hỏng chứ không phải quên dịch hết.
  // Báo lỗi rõ ràng còn hơn kết luận sai rồi để người ta đi dịch lại toàn bộ.
  if (Object.keys(en).length === 0) {
    console.error('Không đọc được khoá nào từ lib/messages/en.ts — kiểm tra định dạng tệp.');
    process.exit(2);
  }

  if (process.argv.includes('--missing')) {
    const missing = all.filter((k) => !(k in en));
    console.error(`${missing.length}/${all.length} khoá chưa có bản dịch English`);
    console.log(JSON.stringify(missing, null, 1));
  } else {
    const stale = Object.keys(en).filter((k) => !keys.has(k));
    console.error(`${stale.length} khoá trong en.ts mà mã không còn dùng`);
    console.log(JSON.stringify(stale, null, 1));
  }
} else {
  console.error(`${all.length} khoá`);
  console.log(JSON.stringify(all, null, 1));
}
