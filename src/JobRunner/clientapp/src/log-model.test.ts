import { describe, it, expect } from "vitest";
import { isLiveAttempt, mergeChunks } from "./log-model";
const chunks = (from: number, count: number) =>
  Array.from({ length: count }, (_, i) => ({
    sequence: from + i,
    text: String(from + i),
  }));
describe("ログの表示範囲", () => {
  it("追加分の重複を除き、末尾64片だけを保持する", () => {
    const result = mergeChunks(chunks(0, 60), chunks(56, 16), "append");
    expect(result).toEqual(chunks(8, 64));
    expect(mergeChunks(result, [], "append")).toEqual(result);
  });
  it("前ページでは古い側を残し、最新へ戻ると範囲を置き換える", () => {
    expect(mergeChunks(chunks(20, 64), chunks(4, 16), "prepend")).toEqual(
      chunks(4, 64),
    );
    expect(mergeChunks(chunks(0, 64), chunks(100, 16), "replace")).toEqual(
      chunks(100, 16),
    );
  });
  it("不明や終了した試行を追尾対象にしない", () => {
    expect(isLiveAttempt("Starting")).toBe(true);
    expect(isLiveAttempt("Running")).toBe(true);
    for (const state of [
      "Unknown",
      "Failed",
      "Succeeded",
      "Interrupted",
      undefined,
    ])
      expect(isLiveAttempt(state)).toBe(false);
  });
});
