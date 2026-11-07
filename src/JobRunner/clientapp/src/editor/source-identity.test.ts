import { describe, expect, it } from "vitest";
import type { EditorSource } from "../api/@types";
import { findOriginalSource } from "./source-identity";

const alpha: EditorSource = {
  fileName: "input/shared.ajs",
  targetPath: "/alpha",
  encoding: "utf-8",
  kind: "Unit",
  text: "alpha original",
  changed: false,
};
const beta: EditorSource = {
  ...alpha,
  targetPath: "/beta",
  encoding: "shift_jis",
  text: "beta original",
};
describe("原文マッピングの対応", () => {
  it("同じファイルの別配置を順序や編集後の本文に依存せず対応付ける", () => {
    expect(
      findOriginalSource([alpha, beta], {
        ...beta,
        text: "edited",
        changed: true,
      }),
    ).toBe(beta);
    expect(
      findOriginalSource([beta, alpha], { ...alpha, text: "edited" }),
    ).toBe(alpha);
  });
  it("配置・文字コード・種別・ファイル名の異なるマッピングを流用しない", () => {
    for (const change of [
      { targetPath: "/elsewhere" },
      { encoding: "us-ascii" },
      { kind: "Calendar" },
      { fileName: "input/another.ajs" },
    ])
      expect(
        findOriginalSource([alpha, beta], { ...alpha, ...change }),
      ).toBeUndefined();
    expect(findOriginalSource([alpha, beta])).toBeUndefined();
  });
  it("種別のない旧基点をUnitとして照合し、明示された別種別とは混同しない", () => {
    const legacy = { ...alpha };
    delete legacy.kind;
    expect(findOriginalSource([legacy], alpha)).toBe(legacy);
    expect(findOriginalSource([alpha], legacy)).toBe(alpha);
    expect(
      findOriginalSource([legacy], { ...alpha, kind: "Calendar" }),
    ).toBeUndefined();
  });
});
