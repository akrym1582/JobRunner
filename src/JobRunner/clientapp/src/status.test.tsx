import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { label, tone, formatDate } from "./status";
describe("状態と日時", () => {
  it("結果不明を異常や成功に丸めない", () => {
    render(<span>{label("UNKNOWN")}</span>);
    expect(screen.getByText("結果不明")).toBeTruthy();
    expect(tone("UNKNOWN")).toBe("warning");
  });
  it("UTCと業務タイムゾーンの日付境界を区別する", () => {
    expect(formatDate("2030-06-18T16:00:00Z", "Asia/Tokyo")).toContain(
      "2030/06/19",
    );
    expect(formatDate(null, "Asia/Tokyo")).toBe("—");
  });
});
