# Create MR — Merge Request title and description

When the user invokes **`/create-mr-metadata`**, prepare a Merge Request title and description from the current git state. Do not do any other actions.

---

## Command signature

```
/create-mr-metadata
```

No arguments. Use the current working tree (staged and/or unstaged changes).

---

## Execution steps (in order)

1. **Get git state**
   - Run `git status` (and, if useful, `git diff --stat` or `git diff --staged --stat`) to see changed files.
   - Prefer staged changes for “what will be in the MR”; if nothing is staged, use unstaged changes.

2. **Infer scope and type**
   - From paths and change patterns, infer:
     - **Type**: Fix / Feature / Refactor (and combinations if needed).
     - **Scope**: e.g. module name, area (auth, infrastructure-space, api, etc.).
   - Follow project commit convention: `feat`, `fix`, `refactor`, etc. (see `.cursor/rules/commit.mdc`).

3. **Write MR title**
   - One line, imperative, no period.
   - Format: `<type>: <short description> (ready for release)` or similar, e.g.:
     - `fix: login 500 error (ready for release)`
     - `feat: infrastructure-space module with soft delete and list counts (ready for release)`

4. **Write MR description**
   - Use **exactly** this structure (copy the headers and checkboxes; fill the bullets and “Need release?”):

```markdown
## Type
- [ ] Fix
- [ ] Feature
- [ ] Refactor

## Impact
- <bullet 1: main change or fix>
- <bullet 2: secondary change>
- … (as many as needed for clarity)

## Need release?
- [ ] Yes
- [ ] No
```

   - **Type**: Check **only** the box(es) that apply (Fix / Feature / Refactor).
   - **Impact**: Short bullets summarizing what the MR does and what is affected (APIs, modules, config, DB, etc.). Use past tense or neutral (“Add …”, “Move …”, “Fix …”) as appropriate.
   - **Need release?**: Check **Yes** or **No** based on whether the change requires a new release (e.g. new APIs, breaking changes, config, DB migrations → usually Yes).

5. **Output**
   - Emit:
     1. **MR title** (single line, easy to copy).
     2. **MR description** (full block as above, ready to paste into the MR form).

---

## Output format (strict)

Present the result like this so the user can copy-paste:

```
MR title:
<one-line title>

MR description:
<full markdown block with Type / Impact / Need release?>
```

---

## Rules

- Base the title and description **only** on the current git state (status + diffs). Do not invent changes.
- If there are no changes or git is not available, say so and do not invent an MR.
- Prefer one clear MR title; if changes are mixed (e.g. feat + refactor), pick the dominant type or mention both in Impact.
- Keep Impact bullets concise; 3–7 bullets is usually enough unless the MR is very large.
- Use English for title and description.
- Do not add extra sections beyond Type, Impact, and Need release? unless the user asks for them.
- Do not commit or push unless explicitly told.