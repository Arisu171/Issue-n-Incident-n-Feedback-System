#!/usr/bin/env bash
# Đổi môi trường đang chạy: chép `.env.<tên>` đè lên `.env`.
#
# Đừng gọi thẳng tệp này — dùng `use-test.sh` hoặc `use-prod.sh`.
#
# Mô hình: `.env` là tệp SINH RA, không phải nguồn. Nguồn là `.env.test` và `.env.prod`.
# Sửa `.env` trực tiếp thì lần đổi môi trường sau sẽ mất, nên script kiểm tra và chặn
# trước khi ghi đè — trừ khi bạn nói rõ `--force`.
#
# Dòng đầu của `.env` là dấu mốc cho biết nó sinh từ đâu, nên lúc nào cũng trả lời được
# câu "máy này đang chạy môi trường nào":
#
#     head -1 .env
#
set -euo pipefail

MARKER_PREFIX="# === MÔI TRƯỜNG:"

# docker-compose.yml khai báo ba biến này dạng ${VAR:?...} — thiếu là compose dừng hẳn.
# Bắt ở đây để lỗi hiện ra lúc đổi môi trường, không phải lúc `up` giữa buổi deploy.
REQUIRED_KEYS="POSTGRES_PASSWORD JWT_SIGNING_KEY SEED_ADMIN_PASSWORD"

TARGET="${1:-}"
FORCE=0
shift || true
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    *) echo "Tham số không hiểu: $arg" >&2; exit 2 ;;
  esac
done

case "$TARGET" in
  test|prod) ;;
  *) echo "Cách dùng: $0 <test|prod> [--force]" >&2; exit 2 ;;
esac

cd "$(git rev-parse --show-toplevel)"
SRC=".env.$TARGET"
CALLER="./infra/scripts/use-$TARGET.sh"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if [ ! -f "$SRC" ]; then
  echo "Không có $SRC." >&2
  echo "Tạo nó từ mẫu rồi điền bí mật thật:  cp .env.example $SRC" >&2
  exit 1
fi

# --- 1. Khoá bắt buộc phải có giá trị thật -----------------------------------
missing=""
for key in $REQUIRED_KEYS; do
  value="$(grep -E "^${key}=" "$SRC" | head -1 | cut -d= -f2- || true)"
  case "$value" in
    ""|doi-*|thay-*|CHANGEME*|"<"*) missing="$missing $key" ;;
  esac
done

if [ -n "$missing" ]; then
  echo "$SRC chưa điền giá trị thật cho:$missing" >&2
  echo "Đây là ba biến docker-compose.yml bắt buộc; để nguyên placeholder thì stack không lên được." >&2
  exit 1
fi

# --- 2. Không nuốt mất chỉnh sửa tay trong .env ------------------------------
current=""
if [ -f .env ]; then
  first_line="$(head -1 .env)"
  case "$first_line" in
    "$MARKER_PREFIX"*)
      current="$(printf '%s' "$first_line" | sed -E 's/^# === MÔI TRƯỜNG: ([a-z]+) ===.*/\1/')"
      ;;
  esac
fi

if [ "$FORCE" -eq 0 ] && [ -n "$current" ] && [ -f ".env.$current" ]; then
  if ! diff -q <(tail -n +2 .env) ".env.$current" >/dev/null 2>&1; then
    echo ".env đang có chỉnh sửa tay chưa lưu về .env.$current — ghi đè là mất." >&2
    echo >&2
    echo "  Giữ lại:  tail -n +2 .env > .env.$current" >&2
    echo "  Bỏ đi:    $CALLER --force" >&2
    exit 1
  fi
fi

# --- 3. Khác nhau ở đâu (chỉ in TÊN khoá, không bao giờ in giá trị) ----------
added=""; removed=""; changed=""
if [ -f .env ]; then
  tail -n +2 .env | grep -E '^[A-Z]' | sort > "$tmp/old" || true
  grep -E '^[A-Z]' "$SRC" | sort > "$tmp/new" || true
  cut -d= -f1 "$tmp/old" | sort -u > "$tmp/kold"
  cut -d= -f1 "$tmp/new" | sort -u > "$tmp/knew"

  added="$(comm -13 "$tmp/kold" "$tmp/knew" | tr '\n' ' ')"
  removed="$(comm -23 "$tmp/kold" "$tmp/knew" | tr '\n' ' ')"

  # Chỉ những khoá CÓ Ở CẢ HAI mà giá trị khác nhau mới là "đổi giá trị"; khoá mới xuất
  # hiện thì thuộc nhóm "thêm". Gộp chung hai nhóm làm danh sách dài vô nghĩa.
  for key in $(comm -12 "$tmp/kold" "$tmp/knew"); do
    old_line="$(grep -E "^${key}=" "$tmp/old" | head -1 || true)"
    new_line="$(grep -E "^${key}=" "$tmp/new" | head -1 || true)"
    [ "$old_line" = "$new_line" ] || changed="$changed $key"
  done
fi

# --- 4. Ghi đè, giữ một bản lùi ---------------------------------------------
if [ -f .env ]; then
  cp .env .env.bak
fi

{
  printf '%s %s === sinh từ %s lúc %s — sửa %s, đừng sửa tệp này\n' \
    "$MARKER_PREFIX" "$TARGET" "$SRC" "$(date '+%Y-%m-%d %H:%M')" "$SRC"
  cat "$SRC"
} > .env

echo "Đã chuyển sang môi trường: $TARGET   (nguồn: $SRC)"
[ -f .env.bak ] && echo "Bản .env cũ giữ ở .env.bak"

show() {  # show <nhãn> <danh sách khoá>
  [ -n "${2// /}" ] || return 0
  echo
  echo "$1"
  printf '  %s\n' $2
}

show "Đổi giá trị:" "$changed"
show "Thêm mới:" "$added"
show "Không còn:" "$removed"

# --- 5. Nhắc dựng lại web nếu địa chỉ API đổi -------------------------------
# NEXT_PUBLIC_* được NHÚNG VÀO BUNDLE lúc build, không đọc lại lúc chạy. Đổi biến này
# mà chỉ `up` không `--build` thì trình duyệt vẫn gọi vào địa chỉ cũ, và triệu chứng là
# "web gọi sai API" rất khó đoán vì tệp .env trông đã đúng.
case " $changed $added " in
  *" NEXT_PUBLIC_API_BASE_URL "*)
    echo
    echo "⚠ NEXT_PUBLIC_API_BASE_URL đã đổi — PHẢI dựng lại ảnh web, không chỉ khởi động lại:"
    echo "    docker compose up -d --build --wait web"
    ;;
esac

echo
echo "Bước tiếp theo:  docker compose up -d --build --wait"
