# X.OS — Ticket System (Next.js export)

A runnable Next.js 15 App Router project containing the ticket-system design
exported from the HTML prototype, with the full light/dark colour system.

## Run

```bash
npm install
npm run dev      # http://localhost:3000
```

## Layout

| File | What it holds |
| --- | --- |
| `app/page.tsx` | The whole design: header, menubar and all ten screens, as one client component with local state (screen, theme, viewport, priority, settings toggles). |
| `app/globals.css` | The Modernist design-system stylesheet, then the page overrides — including `:root` (light) and `html[data-theme="dark"]` token sets: bg/surface/text/accent, the neutral + accent + accent-2 100–900 ramps, shadows, and the component-level dark patches (button ink, input ground, dialog backdrop, table hover, selection, grayscale). |
| `app/layout.tsx` | Document shell; sets `data-theme="light"` as the server-rendered default. |
| `lib/style.ts` | `s()` — parses the prototype's inline CSS strings into React style objects. |

## Theming

`page.tsx` writes `document.documentElement.dataset.theme` on mount and on
every toggle, so dark mode is one attribute on `<html>`. Nothing else needs to
know about the theme: every colour in the design resolves through a token, so
adding a theme means adding one `html[data-theme="…"]` block in
`globals.css`.

## Notes for integrating into an existing app

- This is a design reference in production-shaped code, not a drop-in feature:
  the screens hold static sample content and no data layer.
- Inline styles were kept verbatim so the export stays diffable against the
  prototype. When you fold it into your own codebase, move repeated groups
  (issue row, comment card, avatar, side panel section) into components and
  keep the tokens.
- Icons are inline Lucide paths. If your app already ships `lucide-react`,
  swap the raw `<svg>` blocks for the named components.
- Fonts load through the `@import` at the top of `globals.css`. For a
  production build prefer `next/font` with the Archivo family (400/600/800).
