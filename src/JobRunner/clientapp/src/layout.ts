import type { Graph } from "./api/@types";
export const NODE_WIDTH = 190,
  NODE_HEIGHT = 88;
type Point = { x: number; y: number };
export function fixedLayout(data: Pick<Graph, "nodes" | "edges">) {
  // JP1のピクセル座標の相対位置を維持し、表示用カードに合わせて一律拡大します。
  const raw = data.nodes.map((n) => {
    if (!Number.isFinite(n.x) || !Number.isFinite(n.y))
      throw new Error(`${n.path}: 表示座標がありません。`);
    return {
      id: n.path,
      x: n.x * 1.5 - NODE_WIDTH / 2,
      y: n.y * 1.5 - NODE_HEIGHT / 2,
    };
  });
  const offsetX = Math.max(0, 28 - Math.min(28, ...raw.map((n) => n.x)));
  const offsetY = Math.max(0, 28 - Math.min(28, ...raw.map((n) => n.y)));
  const children = raw.map((n) => ({
    ...n,
    x: n.x + offsetX,
    y: n.y + offsetY,
  }));
  const byId = new Map(children.map((n) => [n.id, n]));
  const edges = data.edges.map((edge) => {
    const a = byId.get(edge.from),
      b = byId.get(edge.to);
    if (!a || !b) throw new Error("接続先のユニットがありません。");
    const dx = b.x - a.x,
      dy = b.y - a.y;
    let startPoint: Point, endPoint: Point, bendPoints: Point[];
    if (Math.abs(dx) >= Math.abs(dy)) {
      startPoint = {
        x: a.x + (dx >= 0 ? NODE_WIDTH : 0),
        y: a.y + NODE_HEIGHT / 2,
      };
      endPoint = {
        x: b.x + (dx >= 0 ? 0 : NODE_WIDTH),
        y: b.y + NODE_HEIGHT / 2,
      };
      const x = (startPoint.x + endPoint.x) / 2;
      bendPoints = [
        { x, y: startPoint.y },
        { x, y: endPoint.y },
      ];
    } else {
      startPoint = {
        x: a.x + NODE_WIDTH / 2,
        y: a.y + (dy >= 0 ? NODE_HEIGHT : 0),
      };
      endPoint = {
        x: b.x + NODE_WIDTH / 2,
        y: b.y + (dy >= 0 ? 0 : NODE_HEIGHT),
      };
      const y = (startPoint.y + endPoint.y) / 2;
      bendPoints = [
        { x: startPoint.x, y },
        { x: endPoint.x, y },
      ];
    }
    return { id: edge.id, sections: [{ startPoint, endPoint, bendPoints }] };
  });
  return {
    children,
    edges,
    width: Math.max(320, ...children.map((n) => n.x + NODE_WIDTH + 28)),
    height: Math.max(180, ...children.map((n) => n.y + NODE_HEIGHT + 28)),
  };
}
