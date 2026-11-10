export function formatMalformedUsageWarning(count: number): string {
  return `${count} malformed or inconsistent usage record(s) were skipped; usage may be incomplete.`;
}
