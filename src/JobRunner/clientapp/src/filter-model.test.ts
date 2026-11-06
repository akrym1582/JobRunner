import { describe, it, expect } from "vitest";
import type { MonthlyEntry, MonthlyUnit } from "./api/@types";
import { filterMonth, matchesStatus } from "./filter-model";
import { monthCells } from "./monthly-model";
const units: MonthlyUnit[] = [
  "/net",
  "/net/nested",
  "/net/nested/job",
  "/net/other",
].map((path) => ({
  path,
  name: path,
  parent: path.slice(0, path.lastIndexOf("/")),
  type: "j",
  isNetwork: false,
  comment: "",
}));
const entries: MonthlyEntry[] = ["NORMAL", "UNKNOWN", "ABNORMAL"].map(
  (status, i) => ({
    status,
    execution: { id: String(i), unitPath: "/net/nested/job" },
    calendarDate: "2030-01-01",
    networkPath: "/net/nested",
    executionId: String(i),
    comment: "",
    exitCode: null,
  }),
);
describe("状態絞り込み", () => {
  it("結果不明を異常終了と分け、複数選択とすべてを扱う", () => {
    expect(matchesStatus("UNKNOWN", ["ABNORMAL"])).toBe(false);
    expect(matchesStatus("UNKNOWN", ["ABNORMAL", "UNKNOWN"])).toBe(true);
    expect(matchesStatus("NORMAL", [])).toBe(true);
  });
  it("該当ジョブの祖先を保持し、セルの件数・状態も同じ集合を使う", () => {
    const filtered = filterMonth(units, entries, ["UNKNOWN"]);
    expect(filtered.units.map((u) => u.path)).toEqual([
      "/net",
      "/net/nested",
      "/net/nested/job",
    ]);
    expect(filtered.entries).toEqual([entries[1]]);
    const cell = monthCells(filtered.entries, "Asia/Tokyo").get(
      "/net/nested/job|2030-01-01",
    )!;
    expect(cell.entries).toHaveLength(1);
    expect(cell.status).toBe("UNKNOWN");
    expect(filterMonth(units, entries, ["RUNNING"]).units).toEqual([]);
    expect(filterMonth(units, entries, []).units).toBe(units);
  });
});
