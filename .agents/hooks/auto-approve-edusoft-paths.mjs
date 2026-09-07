#!/usr/bin/env node
// Auto-approve Edusoft workspace paths on Windows.
// PreToolUse alone is not enough for "Allow glob search in ..." prompts.

const MARKERS = [
  'edusoft-lms-api',
  'lms.edusoft.vn/edusoft-lms-api',
  'lms.edusoft.vn/edusoft-lms',
  'd:/edusoft',
];

const readStdin = () =>
  new Promise((resolve) => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => {
      data += chunk;
    });
    process.stdin.on('end', () => resolve(data));
  });

const normalize = (value) => String(value).replace(/\\/g, '/').toLowerCase();

const matchesEdusoft = (value) => {
  if (!value) {
    return false;
  }

  const normalized = normalize(value);
  return MARKERS.some((marker) => normalized.includes(marker));
};

const collectValues = (payload) => {
  const values = [];
  const toolInput = payload?.tool_input ?? {};

  for (const key of ['file_path', 'pattern', 'path', 'command', 'description']) {
    if (toolInput[key]) {
      values.push(toolInput[key]);
    }
  }

  for (const suggestion of payload?.permission_suggestions ?? []) {
    if (Array.isArray(suggestion?.directories)) {
      values.push(...suggestion.directories);
    }

    if (Array.isArray(suggestion?.rules)) {
      for (const rule of suggestion.rules) {
        if (rule?.ruleContent) {
          values.push(rule.ruleContent);
        }
      }
    }
  }

  values.push(JSON.stringify(payload));
  return values;
};

const shouldAllow = (payload) => collectValues(payload).some(matchesEdusoft);

// Session-only allow: never emit updatedPermissions here — that would persist
// rules into settings.local.json on every permission request (duplicates).
const buildAllowOutput = (hookEventName) => {
  if (hookEventName === 'PermissionRequest') {
    return {
      hookSpecificOutput: {
        hookEventName,
        decision: { behavior: 'allow' },
      },
    };
  }

  return {
    hookSpecificOutput: {
      hookEventName,
      permissionDecision: 'allow',
      permissionDecisionReason: 'Auto-approved Edusoft workspace path',
    },
  };
};

const main = async () => {
  const raw = await readStdin();
  if (!raw.trim()) {
    return;
  }

  let payload;
  try {
    payload = JSON.parse(raw);
  } catch {
    return;
  }

  const hookEventName = payload?.hook_event_name ?? 'PreToolUse';

  if (shouldAllow(payload)) {
    process.stdout.write(JSON.stringify(buildAllowOutput(hookEventName)));
  }
};

main().catch(() => {
  process.exit(0);
});
