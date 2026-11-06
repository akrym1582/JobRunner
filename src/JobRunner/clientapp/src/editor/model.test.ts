import { describe, expect, it } from "vitest";
import { snap } from "./model";

describe("JP1の100列・100行グリッド", () => {
  it("座標上限でも100番目のセルへスナップする", () => {
    expect(snap(16000, 10000)).toEqual({ x: 15920, y: 9552 });
    expect(snap(15920, 9552)).toEqual({ x: 15920, y: 9552 });
    expect(snap(-1, -1)).toEqual({ x: 80, y: 48 });
  });
});
