import type { MonthlyEntry, MonthlyUnit } from "./api/@types";

export const executionStatuses = [
  "NORMAL",
  "NORMALFALSE",
  "WARNING",
  "RUNNING",
  "ABCONT",
  "WACONT",
  "EXECWAIT",
  "TIMEWAIT",
  "TERMWAIT",
  "HOLDING",
  "ABNORMAL",
  "FAIL",
  "UNKNOWN",
  "BYPASS",
  "UNEXEC",
  "KILL",
  "INTERRUPT",
];
// 空配列は「すべて」。結果不明などの状態を別の状態にまとめません。
export const matchesStatus = (status: string, selected: string[]) =>
  selected.length === 0 || selected.includes(status);

export function filterMonth(
  units: MonthlyUnit[],
  entries: MonthlyEntry[],
  selected: string[],
) {
  if (!selected.length) return { units, entries };
  const filtered = entries.filter((entry) =>
    matchesStatus(entry.status, selected),
  );
  const parents = new Map(units.map((unit) => [unit.path, unit.parent]));
  const keep = new Set<string>();
  for (const entry of filtered) {
    let path = entry.execution.unitPath;
    while (path && !keep.has(path)) {
      keep.add(path);
      path = parents.get(path);
    }
  }
  return {
    units: units.filter((unit) => keep.has(unit.path)),
    entries: filtered,
  };
}
