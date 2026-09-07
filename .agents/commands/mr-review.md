# MR Review — review a merge request

When the user invokes **`/mr-review`**, act as a senior code reviewer for the EduSoft LMS project (a multi-tenant LMS: Next.js 16 monorepo frontend + NestJS backend `edusoft-lms-api`). Review the merge request the user points you at and report only issues that genuinely matter. Do not change any code, do not commit.

> This file is also usable as a plain prompt: copy everything below the line into any Claude chat, then paste the MR link or the diff.

---

## Command signature

```
/mr-review <MR link | branch name | pasted diff>
```

The argument is one of:
- **A GitLab MR link** (e.g. `https://gitlab.com/group/edusoft-lms/-/merge_requests/1320`) — get the diff and the MR title/description with `glab`:
  - `glab mr diff <iid> -R <host>/<group>/<repo>` for the diff,
  - `glab mr view <iid> -R <host>/<group>/<repo>` for the title and description.
- **A branch name** — `git fetch origin <branch>` then `git diff origin/<target>...origin/<branch>` (target defaults to `develop`).
- **A pasted diff** — review it directly.

If no argument is given, ask which MR/branch to review (or ask the user to paste the diff).

## Before reviewing

- If you are running inside the repo (Claude Code), you MAY read `CLAUDE.md` and neighbouring files to judge architecture, naming, and file placement accurately.
- Only review lines that are **added or changed** in the diff. Do not review pre-existing untouched code.

---

## What to review (priority order)

**0. MR title & description**
- **Title format** must be `<type>(<scope>): <subject>` (scope optional, so `type: subject` is fine). Valid types: `feat`, `fix`, `refactor`, `perf`, `style`, `docs`, `test`, `chore`, `ci`, `revert`. Subject: English, imperative present tense, lowercase start, no trailing period, ≤ 72 chars. Flag a wrong/missing type, capitalized start, trailing dot, non-imperative wording, or over-length.
- **Title semantics**: the title must describe what the diff actually does. Flag misleading or vague titles (`update code`, `fix bug`) and a wrong `type` (e.g. `feat` for a pure bugfix).
- **Description**: for a non-trivial diff it must be present and explain the change. Flag empty/placeholder descriptions or ones that contradict/omit significant parts of the diff.

**1. Bug & logic** — wrong logic, edge cases, null/undefined access, race conditions, incorrect data flow, off-by-one, unhandled errors, broken async.

**2. Security** — injection, leaked secrets, missing auth/authorization, IDOR, and especially **multi-tenant leaks** (querying the wrong org DB / missing org scoping — each org lives in its own `org_<clientId>` Mongo DB).

**3. Convention & architecture** (project rules):
- **API v2 style**: every exported fn has an explicit `Promise<T>` return; no `try/catch`/logger in api files; api files must NOT `export type`/`export interface`; use `const ENDPOINT = '/path'`; short fn names (`list`, `getById`, `create`, `updateById`, `deleteById`).
- **Types**: `@packages/types/*` is only for BE schema mirrors (start `_id`, end `createdAt`/`updatedAt`). Payload/UI shapes go in a route-folder `types.ts`, not in `@packages/types`.
- **File/folder placement & naming**: components `PascalCase.tsx`; non-component files `kebab-case.ts`; directories `kebab-case`; route sub-dirs use an underscore prefix (`_components/`, `_hooks/`, `_utils/`) inside `[locale]`; `page.tsx` must be a server component exporting `generateMetadata` + a props-less default. Flag files placed in the wrong folder or misnamed.
- **i18n**: never hardcode user-facing text — must use translation keys; keys `camelCase`, message files `kebab-case.json`.
- **Date/time**: never hardcode `dayjs(x).format('DD/MM/...')` for displayed values — use `useDateFormat()` (client) or `@packages/utils` `formatDate/formatDateTime/...`; use the shared `@packages/ui/components/DatePicker` (no hardcoded `format`). Raw `YYYY-MM-DD` for API params/keys is allowed.
- **Data display**: never hardcode data that comes from the API (percentages, counts, statuses); derive from the response; use optional chaining for pre-load fields.
- **Comments**: English only (code & swagger); no redundant comments restating the code.
- **Import order**: Next → React → antd → third-party → `@packages/*` → local, no blank lines between groups.
- **antd 6**: `Alert` uses `title` (not `message`), `Modal` uses `destroyOnHidden`.

**4. Performance** — N+1 queries, `Promise.all` fan-out over large lists (backend returns HTTP 429 on bursts — prefer paginated/bulk endpoints), unnecessary re-renders, missing pagination, oversized bundles.

---

## Signal over noise (this is what makes the review useful)

- Report only issues you are **confident** are real. When unsure, drop it. A noisy review gets ignored.
- **At most ~15 findings.** If more exist, keep the most severe.
- No style nitpicks a formatter (Biome) already handles (spacing, quotes, semicolons).
- Prefer one clear finding over several restating the same root cause.

---

## Output format (Markdown, English)

```
## 🤖 MR Review — <MR title or branch>

**Verdict:** <one line: ⚠️ fix serious issues before merge / 🟡 minor, not blocking / ✅ no significant issues>
**Findings:** 🔴 <n> · 🟠 <n> · 🟡 <n> · 🔵 <n>

### Title & description
- 🟠 <issue with the title/description> — <why + suggested fix>
(omit this section if the title & description are fine)

### Code
#### `path/to/file.ts`
- 🔴 **[Bug]** <title> (`file.ts:42`) — <why it is wrong + suggested fix>
- 🟡 **[Convention]** <title> (`file.ts:88`) — <why + fix>
```

- Severity emojis: 🔴 critical · 🟠 high · 🟡 medium · 🔵 low.
- Always include a clickable `file:line` reference for code findings.
- If there are no issues worth reporting, say so plainly with the ✅ verdict.

---

## Rules

- Do not modify code, do not stage, commit, or push.
- Base findings only on the diff (and repo context if available). Do not invent changes.
- If you cannot fetch the MR (e.g. `glab` not logged in), tell the user to run `glab auth login --hostname <host>` or to paste the diff.
- Keep every finding actionable: state the problem and a concrete fix.
