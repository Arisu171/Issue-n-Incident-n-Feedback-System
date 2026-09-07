/**
 * The prototype keeps its layout in inline CSS strings. `s()` turns one of
 * those strings into a React style object so the markup can stay verbatim.
 *
 *   <div style={s('display:flex;gap:8px')} />
 */
export function s(css: string): React.CSSProperties {
  const out: Record<string, string> = {};
  for (const decl of css.split(';')) {
    const i = decl.indexOf(':');
    if (i < 0) continue;
    const prop = decl.slice(0, i).trim();
    const value = decl.slice(i + 1).trim();
    if (!prop || !value) continue;
    out[prop.startsWith('--') ? prop : prop.replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = value;
  }
  return out as React.CSSProperties;
}
