import { describe, expect, it } from "vitest";
import { changeCalendarDate } from "./CalendarMonth";

describe("month calendar edits", () => {
  it("changes only the selected day and retains order, weekdays and unrelated keys", () => {
    const original = {
      cl: ["su", "2028/02/29", "2028/12/31"],
      op: ["mo", "2028/2/29", "2028/1/1"],
      stt: ["06:00"],
    };
    expect(changeCalendarDate(original, "2028-02-29", "cl")).toEqual({
      cl: ["su", "2028/12/31", "2028/2/29"],
      op: ["mo", "2028/1/1"],
      stt: ["06:00"],
    });
    expect(changeCalendarDate(original, "2028-02-29", "inherit")).toEqual({
      cl: ["su", "2028/12/31"],
      op: ["mo", "2028/1/1"],
      stt: ["06:00"],
    });
    expect(original.cl).toContain("2028/02/29");
    expect(
      changeCalendarDate({ op: ["2028/2/29"] }, "2028-02-29", "inherit"),
    ).toEqual({});
  });
});
