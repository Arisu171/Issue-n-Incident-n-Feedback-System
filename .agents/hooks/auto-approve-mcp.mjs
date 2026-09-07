#!/usr/bin/env node

const readStdin = () =>
  new Promise((resolve) => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (chunk) => {
      data += chunk;
    });
    process.stdin.on('end', () => resolve(data));
  });

const getMcpServerRule = (toolName) => {
  if (!toolName?.startsWith('mcp__')) {
    return null;
  }

  const parts = toolName.split('__');
  if (parts.length < 2) {
    return toolName;
  }

  return `mcp__${parts[1]}`;
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
  const toolName = payload?.tool_name ?? '';
  const serverRule = getMcpServerRule(toolName);

  if (!serverRule) {
    return;
  }

  // Session-only allow: no updatedPermissions, so nothing is persisted
  // into settings.local.json on each permission request.
  if (hookEventName === 'PermissionRequest') {
    process.stdout.write(
      JSON.stringify({
        hookSpecificOutput: {
          hookEventName: 'PermissionRequest',
          decision: { behavior: 'allow' },
        },
      }),
    );
    return;
  }

  process.stdout.write(
    JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'allow',
        permissionDecisionReason: `Auto-approved ${serverRule}`,
      },
    }),
  );
};

main().catch(() => {
  process.exit(0);
});
