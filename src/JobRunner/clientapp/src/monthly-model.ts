import type { MonthlyEntry } from "./api/@types";
import { formatDate, label, tone } from "./status";

export type MonthCell = {
  entries: MonthlyEntry[];
  status: string;
  started: boolean;
  title: string;
};

const priority = (status: string) =>
  ({ danger: 3, warning: 2, running: 1, success: 0, neutral: 0 })[tone(status)];

// 取得データが変わったときだけ集計し、選択・階層開閉ではこの索引を再利用します。
export function monthCells(entries: MonthlyEntry[], zone: string) {
  const cells = new Map<string, MonthCell>();
  for (const entry of entries) {
    const key = entry.execution.unitPath + "|" + entry.calendarDate;
    let cell = cells.get(key);
    if (!cell) {
      cell = { entries: [], status: entry.status, started: false, title: "" };
      cells.set(key, cell);
    }
    cell.entries.push(entry);
    if (priority(entry.status) > priority(cell.status))
      cell.status = entry.status;
    cell.started ||= !!(entry.execution.started || entry.execution.startedAt);
  }
  for (const cell of cells.values()) {
    cell.title = cell.entries
      .map(
        (entry) =>
          `${formatDate(entry.execution.due, zone)} ${label(entry.status)}`,
      )
      .join("\n");
  }
  return cells;
}
