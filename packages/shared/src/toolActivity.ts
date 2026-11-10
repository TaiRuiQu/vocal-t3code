import type { ToolLifecycleItemType } from "@t3tools/contracts";

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function asTrimmedString(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function claudeSkillInvocation(
  toolName: string | null | undefined,
  input: unknown,
): { readonly name: string; readonly args: string | undefined } | undefined {
  if (toolName !== "Skill") return undefined;
  const record = asRecord(input);
  const name = asTrimmedString(record?.skill);
  return name === undefined ? undefined : { name, args: asTrimmedString(record?.args) };
}

function isJavaScriptReplTool(toolName: string | null | undefined): boolean {
  return toolName === "js" || toolName === "cua_repl.js" || toolName === "node_repl.js";
}

export const COMPUTER_USE_TOOL_SOURCE = {
  key: "computer-use",
  name: "Computer Use",
  kind: "computer",
} as const;

export function dynamicToolSurface(toolName: string | null | undefined, input: unknown) {
  if (toolName === "cua_repl.js") return "computer" as const;
  const code = asRecord(input)?.code;
  return isJavaScriptReplTool(toolName) &&
    typeof code === "string" &&
    (/\bsky\s*\./u.test(code) || /["']@oai\/sky["']/u.test(code))
    ? ("computer" as const)
    : undefined;
}

export function dynamicToolTitle(
  toolName: string | null | undefined,
  input: unknown,
  retainedTitle?: string | null,
): string | undefined {
  if (isJavaScriptReplTool(toolName)) {
    const title = asTrimmedString(retainedTitle);
    const meaningfulTitle =
      title && title.toLowerCase() !== "js" && title.toLowerCase() !== toolName?.toLowerCase()
        ? title
        : undefined;
    return (
      asTrimmedString(asRecord(input)?.title) ??
      meaningfulTitle ??
      (dynamicToolSurface(toolName, input) === "computer" ? "Computer use" : "Run JavaScript")
    );
  }
  const skill = claudeSkillInvocation(toolName, input);
  return skill === undefined ? undefined : `Skill: ${skill.name}`;
}

function recordHasKeys(
  value: Record<string, unknown> | undefined,
): value is Record<string, unknown> {
  return value !== undefined && Object.keys(value).length > 0;
}

/** Preserve the first nonempty rawInput; later ACP updates often resend an empty object. */
export function mergeToolActivityData(
  previous: unknown,
  next: unknown,
): Record<string, unknown> | undefined {
  const previousRecord = asRecord(previous);
  const nextRecord = asRecord(next);
  if (!nextRecord) {
    return previousRecord;
  }
  if (!previousRecord) {
    return nextRecord;
  }
  const previousInput = asRecord(previousRecord.rawInput);
  const nextInput = asRecord(nextRecord.rawInput);
  const rawInput = recordHasKeys(nextInput)
    ? { ...previousInput, ...nextInput }
    : (previousInput ?? nextInput);
  const merged = { ...previousRecord, ...nextRecord };
  if (recordHasKeys(rawInput)) {
    merged.rawInput = rawInput;
  } else {
    delete merged.rawInput;
  }
  return merged;
}

export type ToolActivityAction = "command" | "read" | "file_change" | "search" | "other";

function toolNameToken(value: string | undefined): string | undefined {
  const trimmed = asTrimmedString(value);
  if (!trimmed || /__|[./]/u.test(trimmed)) {
    return undefined;
  }
  return trimmed.replace(/[_\s-]/gu, "").toLowerCase();
}

export function classifyToolActivity(input: {
  readonly itemType?: ToolLifecycleItemType | string | null | undefined;
  readonly requestKind?: string | null | undefined;
  readonly title?: string | undefined;
  readonly data?: Record<string, unknown> | undefined;
}): ToolActivityAction {
  const itemType = input.itemType ?? undefined;
  const requestKind = asTrimmedString(input.requestKind)?.toLowerCase();
  const kind = asTrimmedString(input.data?.kind)?.toLowerCase();
  const toolName = toolNameToken(
    asTrimmedString(input.data?.toolName) ?? asTrimmedString(asRecord(input.data?.item)?.tool),
  );

  if (itemType === "command_execution") {
    return "command";
  }
  if (itemType === "image_view") {
    return "read";
  }
  if (itemType === "file_change") {
    return "file_change";
  }
  if (itemType === "web_search") {
    return "search";
  }
  if (requestKind === "command" || kind === "execute") {
    return "command";
  }
  if (
    requestKind === "file-change" ||
    kind === "edit" ||
    kind === "move" ||
    kind === "delete" ||
    kind === "write"
  ) {
    return "file_change";
  }
  if (
    kind === "search" ||
    toolName === "find" ||
    toolName === "grep" ||
    toolName === "glob" ||
    toolName === "rg" ||
    toolName === "ls"
  ) {
    return "search";
  }
  if (requestKind === "file-read" || kind === "read") {
    return "read";
  }
  if (toolName === "terminal" || toolName === "bash" || toolName === "shell") {
    return "command";
  }
  if (toolName === "read" || toolName === "readfile") {
    return "read";
  }
  return "other";
}

const SEARCH_QUERY_KEYS = ["pattern", "query", "searchTerm", "regex", "grep", "needle"] as const;
const SEARCH_GLOB_KEYS = [
  "glob",
  "globPattern",
  "glob_pattern",
  "include",
  "filePattern",
  "file_pattern",
] as const;
const SEARCH_TARGET_KEYS = [
  "path",
  "target_directory",
  "targetDirectory",
  "directory",
  "cwd",
  "root",
] as const;

function firstInputString(
  record: Record<string, unknown> | undefined,
  keys: readonly string[],
): string | undefined {
  if (!record) {
    return undefined;
  }
  for (const key of keys) {
    const value = asTrimmedString(record[key]);
    if (value) {
      return value;
    }
  }
  return undefined;
}

function searchInputRecord(
  data: Record<string, unknown> | undefined,
): Record<string, unknown> | undefined {
  return (
    [data?.rawInput, data?.input, asRecord(data?.item)?.input]
      .map(asRecord)
      .find((record) => recordHasKeys(record ?? undefined)) ?? data
  );
}

function searchTargetName(value: string | undefined): string | undefined {
  if (!value) {
    return undefined;
  }
  return value.split(/[\\/]/u).findLast((part) => part.length > 0 && part !== ".");
}

export function formatSearchToolLabel(
  data: Record<string, unknown> | undefined,
): string | undefined {
  const input = searchInputRecord(data);
  const query = firstInputString(input, SEARCH_QUERY_KEYS);
  const glob = firstInputString(input, SEARCH_GLOB_KEYS);
  const target = searchTargetName(firstInputString(input, SEARCH_TARGET_KEYS));
  if (query && target) {
    return `Searched ${query} in ${target}`;
  }
  if (glob && target) {
    return `Searched files ${glob} in ${target}`;
  }
  if (glob) {
    return `Searched files ${glob}`;
  }
  if (query) {
    return `Searched ${query}`;
  }
  if (target) {
    return `Searched in ${target}`;
  }
  return undefined;
}

export function formatReadToolLabel(path: string, extraCount = 0): string {
  const trimmed = path.trim();
  const suffix = extraCount > 0 ? ` +${extraCount} more` : "";
  if (!trimmed) {
    return `Read file${suffix}`;
  }
  return `Read ${trimmed}${suffix}`;
}
