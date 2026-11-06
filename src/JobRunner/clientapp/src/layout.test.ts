import { describe, it, expect } from "vitest";
import { fixedLayout } from "./layout";
import type { Node } from "./api/@types";
const node = (path: string, x: number, y: number): Node => ({
  path,
  name: path,
  type: "j",
  isNetwork: false,
  isRecovery: false,
  status: "DEFINITION",
  execution: null,
  exitCode: null,
  x,
  y,
});
describe("定義の座標", () => {
  it("定義順に並べ替えず、縦と逆向きの配置を維持する", () => {
    const result = fixedLayout({
      nodes: [
        node("right", 400, 48),
        node("left", 80, 48),
        node("below", 80, 144),
      ],
      edges: [{ id: "e", from: "left", to: "below", kind: "recovery" }],
    });
    expect(result.children[0].x - result.children[1].x).toBe(480);
    expect(result.children[2].y - result.children[1].y).toBe(144);
    expect(result.edges[0].sections[0].startPoint.x).toBe(
      result.edges[0].sections[0].endPoint.x,
    );
  });
  it("欠けた座標を自動配置しない", () => {
    expect(() =>
      fixedLayout({ nodes: [node("missing", NaN, 0)], edges: [] }),
    ).toThrow("表示座標がありません");
  });
});
