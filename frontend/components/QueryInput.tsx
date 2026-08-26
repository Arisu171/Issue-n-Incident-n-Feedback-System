'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { buildQuery, dslTokens, isFilterToken, splitQuery } from '@/lib/util';
import { tr } from '@/lib/i18n';

/**
 * Ô tìm kiếm hiểu cú pháp lọc.
 *
 * Token dạng `key:value` (kể cả phủ định `-key:value`) hiện thành chip đứng trước ô nhập; phần
 * còn lại giữ nguyên là chữ để gõ tự do. Khi gửi đi, chữ được gom lại và nối với các chip thành
 * một truy vấn — backend nối tất cả bằng AND nên chữ và bộ lọc lọc song song với nhau.
 *
 * Chip sinh ra ngay khi gõ dấu cách sau một token hợp lệ, nên không cần nút "thêm bộ lọc".
 * Backspace ở đầu ô nhập rỗng gỡ chip cuối, đúng thói quen của các ô nhập dạng thẻ.
 */
export function QueryInput({
  value,
  onSubmit,
  placeholder,
  ariaLabel,
  prefix,
  className = '',
  mono = false,
}: {
  value: string;
  onSubmit: (query: string) => void;
  placeholder?: string;
  ariaLabel?: string;
  /** Nội dung đứng trước chip — nhãn "DSL" hoặc icon kính lúp. */
  prefix?: React.ReactNode;
  className?: string;
  /** Ô nhập dùng phông đơn cách (màn hình Search). */
  mono?: boolean;
}) {
  const [filters, setFilters] = useState<string[]>([]);
  const [text, setText] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  // Truy vấn nằm ở URL nên nguồn sự thật là prop; mỗi lần nó đổi thì dựng lại chip và chữ.
  useEffect(() => {
    const parsed = splitQuery(value);
    setFilters(parsed.filters);
    setText(parsed.text);
  }, [value]);

  const submit = useCallback((nextFilters: string[], nextText: string) => {
    onSubmit(buildQuery(nextFilters, nextText));
  }, [onSubmit]);

  /** Gõ dấu cách sau một token lọc thì biến nó thành chip ngay. */
  function onChangeText(next: string) {
    if (!next.endsWith(' ')) { setText(next); return; }
    const tokens = dslTokens(next);
    const last = tokens[tokens.length - 1];
    if (last && isFilterToken(last) && !filters.includes(last)) {
      setFilters((f) => [...f, last]);
      setText(tokens.slice(0, -1).join(' '));
      return;
    }
    setText(next);
  }

  function removeFilter(token: string) {
    const next = filters.filter((f) => f !== token);
    setFilters(next);
    submit(next, text);
  }

  return (
    <form
      className={`query-input ${className}`.trim()}
      onSubmit={(e) => { e.preventDefault(); submit(filters, text); }}
      role="search"
    >
      {prefix}
      {filters.map((token) => (
        <span key={token} className={`qchip ${token.startsWith('-') ? 'negated' : ''}`.trim()}>
          {token}
          <button
            type="button"
            className="qchip-x"
            aria-label={tr('Bỏ điều kiện {v0}', { v0: token })}
            title={tr('Bấm để bỏ điều kiện này')}
            onClick={() => removeFilter(token)}
          >
            ×
          </button>
        </span>
      ))}
      <input
        ref={inputRef}
        value={text}
        aria-label={ariaLabel}
        placeholder={filters.length === 0 ? placeholder : undefined}
        style={mono ? { fontFamily: 'var(--font-mono)' } : undefined}
        onChange={(e) => onChangeText(e.target.value)}
        onKeyDown={(e) => {
          // Backspace ở đầu ô rỗng gỡ chip cuối — không gửi lại truy vấn, để người dùng gỡ
          // vài chip liền nhau rồi mới tìm.
          if (e.key === 'Backspace' && text === '' && filters.length > 0) {
            e.preventDefault();
            setFilters((f) => f.slice(0, -1));
          }
        }}
      />
    </form>
  );
}
