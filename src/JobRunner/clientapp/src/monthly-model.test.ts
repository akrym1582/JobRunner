import { describe, expect, it } from "vitest";
import type { MonthlyEntry } from "./api/@types";
import { monthCells } from "./monthly-model";

const entry = (
  status: string,
  overrides: Partial<MonthlyEntry> = {},
): MonthlyEntry => ({
  status,
  calendarDate: "2030-07-01",
  comment: "",
  exitCode: null,
  executionId: "@1",
  networkPath: "/net",
  execution: { unitPath: "/net/job", due: "2030-06-30T15:00:00Z" },
  ...overrides,
});

describe("月間のセル集計", () => {
  it("同日複数回を保持し、異常・不明・実行中の優先順と実績記号を維持する", () => {
    const values = [
      entry("NORMAL"),
      entry("RUNNING"),
      entry("UNKNOWN"),
      entry("ABNORMAL", {
        execution: {
          unitPath: "/net/job",
          due: "2030-06-30T16:00:00Z",
          started: true,
        },
      }),
    ];
    const cell = monthCells(values, "Asia/Tokyo").get("/net/job|2030-07-01")!;
    expect(cell.entries).toEqual(values);
    expect(cell.status).toBe("ABNORMAL");
    expect(cell.started).toBe(true);
    expect(cell.title).toContain("2030/07/01 01:00:00 異常終了");
    expect(
      monthCells(values.slice(0, 3), "Asia/Tokyo").get("/net/job|2030-07-01")
        ?.status,
    ).toBe("UNKNOWN");
  });

  it("未開始取消を実績扱いにせず、日付とパスの異なる実行を混ぜない", () => {
    const values = [
      entry("BYPASS", {
        execution: { unitPath: "/net/job", finishedAt: "2030-07-01T00:00:00Z" },
      }),
      entry("TIMEWAIT", { calendarDate: "2030-07-02" }),
      entry("UNKNOWN", { execution: { unitPath: "/net/other" } }),
    ];
    const cells = monthCells(values, "Asia/Tokyo");
    expect(cells.size).toBe(3);
    expect(cells.get("/net/job|2030-07-01")?.started).toBe(false);
    expect(cells.get("/net/job|2030-07-01")?.entries).toEqual([values[0]]);
    expect(cells.get("/net/other|2030-07-01")?.status).toBe("UNKNOWN");
  });
});
