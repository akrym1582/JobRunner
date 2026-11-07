import { memo, useMemo, useRef, useState } from "react";
import { Maximize, Minus, Plus } from "lucide-react";
import type { EditorNetwork, EditorNode } from "../api/@types";
import { fixedLayout, NODE_HEIGHT, NODE_WIDTH } from "../layout";
import { snap } from "./model";

export function EditorGraph({
  network,
  typeOf = (n) => n.type,
  selected,
  selection = [selected],
  connecting,
  onSelect,
  onMove,
  onOpen,
}: {
  network: EditorNetwork;
  typeOf?: (node: EditorNode) => string;
  selected: string;
  selection?: string[];
  connecting: boolean;
  onSelect: (id: string, multiple?: boolean) => void;
  onMove: (node: EditorNode, x: number, y: number) => void;
  onOpen: (id: string) => void;
}) {
  const svg = useRef<SVGSVGElement>(null);
  const [zoom, setZoom] = useState<number | null>(null);
  const [moving, setMoving] = useState<{
    id: string;
    dx: number;
    dy: number;
  }>();
  const drag = useRef<{
    node: EditorNode;
    x: number;
    y: number;
    moved: boolean;
  }>(undefined);
  const result = useMemo(() => {
    try {
      const missing = network.nodes.find(
        (n) =>
          n.x === null ||
          n.y === null ||
          n.x < 0 ||
          n.y < 0 ||
          n.x > 16000 ||
          n.y > 10000,
      );
      if (missing)
        return {
          error: `${missing.name}: 表示座標がありません、または範囲外です。ユニット一覧から選択し、座標を修正してください。`,
        };
      return {
        layout: fixedLayout({
          nodes: network.nodes.map((n) => ({ path: n.id, x: n.x!, y: n.y! })),
          edges: network.edges.map((e, i) => ({
            id: String(i),
            from: e.from,
            to: e.to,
          })),
        }),
      };
    } catch (e) {
      return { error: String(e) };
    }
  }, [network]);
  const layout = result.layout;
  const nodesById = useMemo(
    () => new Map(network.nodes.map((node) => [node.id, node])),
    [network.nodes],
  );
  const nodeType = (id: string) => {
    const node = nodesById.get(id);
    return node ? typeOf(node) : "";
  };
  const selectedIds = useMemo(() => new Set(selection), [selection]);
  // 描画部品は表示値だけを持ち、操作時は最新のノード・コールバックを参照します。
  // コマンド変更後の移動で、描画を省略した時点の属性を戻さないためです。
  function eventNode(target: EventTarget) {
    if (!(target instanceof Element)) return undefined;
    const element = target.closest("[data-editor-node]");
    return element && svg.current?.contains(element)
      ? nodesById.get(element.getAttribute("data-editor-node")!)
      : undefined;
  }
  function point(clientX: number, clientY: number) {
    const matrix = svg.current?.getScreenCTM();
    return matrix
      ? new DOMPoint(clientX, clientY).matrixTransform(matrix.inverse())
      : new DOMPoint();
  }
  return (
    <div className="editor-canvas">
      <div className="editor-canvas-label">
        DEFINITION MAP <span>{network.nodes.length} ユニット</span>
      </div>
      {result.error ? (
        <div role="alert" className="empty">
          {result.error}
        </div>
      ) : (
        layout && (
          <div className="editor-canvas-scroll">
            <svg
              ref={svg}
              className="editor-graph"
              aria-label="定義編集の図"
              viewBox={`0 0 ${layout.width} ${Math.max(layout.height, 320)}`}
              style={{
                width: zoom ? layout.width * zoom : "100%",
                height: zoom ? Math.max(layout.height, 320) * zoom : 440,
              }}
              onPointerDown={(e) => {
                const node = eventNode(e.target);
                if (!node || e.button !== 0) return;
                if (connecting) {
                  onSelect(node.id);
                  return;
                }
                const p = point(e.clientX, e.clientY);
                drag.current = { node, x: p.x, y: p.y, moved: false };
                (e.target as Element)
                  .closest("[data-editor-node]")!
                  .setPointerCapture(e.pointerId);
              }}
              onDoubleClick={(e) => {
                const node = eventNode(e.target);
                if (node) onOpen(node.id);
              }}
              onKeyDown={(e) => {
                const node = eventNode(e.target);
                if (!node) return;
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  onSelect(node.id, e.ctrlKey || e.metaKey);
                } else if (e.key.startsWith("Arrow")) {
                  e.preventDefault();
                  const next = snap(
                    node.x! +
                      (e.key === "ArrowRight"
                        ? 160
                        : e.key === "ArrowLeft"
                          ? -160
                          : 0),
                    node.y! +
                      (e.key === "ArrowDown"
                        ? 96
                        : e.key === "ArrowUp"
                          ? -96
                          : 0),
                  );
                  onMove(node, next.x, next.y);
                }
              }}
              onPointerMove={(e) => {
                if (!drag.current) return;
                const p = point(e.clientX, e.clientY),
                  dx = p.x - drag.current.x,
                  dy = p.y - drag.current.y;
                if (Math.abs(dx) + Math.abs(dy) > 4) drag.current.moved = true;
                setMoving({ id: drag.current.node.id, dx, dy });
              }}
              onPointerUp={(e) => {
                const current = drag.current;
                drag.current = undefined;
                setMoving(undefined);
                if (!current) return;
                if (current.moved) {
                  const p = point(e.clientX, e.clientY);
                  const next = snap(
                    current.node.x! + (p.x - current.x) / 1.5,
                    current.node.y! + (p.y - current.y) / 1.5,
                  );
                  onMove(current.node, next.x, next.y);
                } else onSelect(current.node.id, e.ctrlKey || e.metaKey);
              }}
              onPointerCancel={() => {
                drag.current = undefined;
                setMoving(undefined);
              }}
            >
              <defs>
                <marker
                  id="editor-arrow"
                  markerWidth="8"
                  markerHeight="8"
                  refX="7"
                  refY="4"
                  orient="auto"
                >
                  <path d="M0,0 L8,4 L0,8 Z" fill="context-stroke" />
                </marker>
              </defs>
              {layout.edges.map((edge, index) => {
                const kind = network.edges[index].conditional
                  ? "condition"
                  : ["rj", "rp", "rn", "rjdj"].includes(
                        nodeType(network.edges[index].to),
                      )
                    ? "recovery"
                    : "normal";
                return (
                  <GraphEdge
                    key={edge.id}
                    kind={kind}
                    path={edge.sections
                      .map((section) =>
                        [
                          section.startPoint,
                          ...section.bendPoints,
                          section.endPoint,
                        ]
                          .map((p, i) => `${i ? "L" : "M"}${p.x},${p.y}`)
                          .join(" "),
                      )
                      .join(" ")}
                  />
                );
              })}
              {layout.children.map((position) => {
                const node = nodesById.get(position.id)!;
                return (
                  <GraphNode
                    key={node.id}
                    id={node.id}
                    name={node.name}
                    type={node.type}
                    displayType={typeOf(node)}
                    x={node.x}
                    y={node.y}
                    selected={selectedIds.has(node.id)}
                    px={position.x + (moving?.id === node.id ? moving.dx : 0)}
                    py={position.y + (moving?.id === node.id ? moving.dy : 0)}
                  />
                );
              })}
            </svg>
          </div>
        )
      )}
      <div className="editor-zoom">
        <button
          aria-label="編集図を縮小"
          onClick={() => setZoom((z) => Math.max(0.2, (z ?? 1) / 1.2))}
        >
          <Minus size={16} />
        </button>
        <button
          aria-label="編集図を拡大"
          onClick={() => setZoom((z) => Math.min(2, (z ?? 0.7) * 1.2))}
        >
          <Plus size={16} />
        </button>
        <button aria-label="編集図を全体表示" onClick={() => setZoom(null)}>
          <Maximize size={16} />
        </button>
      </div>
      <div className="editor-canvas-help">
        {connecting
          ? "接続元 → 接続先の順に選択"
          : "ドラッグで配置 · 矢印キーで1マス移動 · 子ネットをダブルクリック"}
      </div>
    </div>
  );
}

// 属性本文やイベント関数は渡さず、図に出す値が変わったノードだけ描画します。
const GraphNode = memo(function GraphNode({
  id,
  name,
  type,
  displayType,
  x,
  y,
  px,
  py,
  selected,
}: {
  id: string;
  name: string;
  type: string;
  displayType: string;
  x: number | null;
  y: number | null;
  px: number;
  py: number;
  selected: boolean;
}) {
  return (
    <g
      data-editor-node={id}
      role="button"
      tabIndex={0}
      aria-label={`編集 ${name}`}
      aria-pressed={selected}
      className={`editor-node ${selected ? "selected" : ""}`}
      transform={`translate(${px},${py})`}
    >
      <rect width={NODE_WIDTH} height={NODE_HEIGHT} rx="10" />
      <rect
        className="editor-node-icon"
        x="13"
        y="17"
        width="30"
        height="30"
        rx="7"
      />
      <text x="28" y="37" textAnchor="middle" className="editor-node-type">
        {displayType.toUpperCase()}
      </text>
      <text x="54" y="31" className="editor-node-name">
        {name.length > 9 ? name.slice(0, 9) + "…" : name}
      </text>
      <text x="54" y="51" className="editor-node-caption">
        {type === "n" || type === "rn"
          ? "ジョブネット"
          : type === "jdj"
            ? "判定ジョブ"
            : type === "rjdj"
              ? "リカバリー判定"
              : type === "rj" || type === "rp"
                ? "リカバリージョブ"
                : "ジョブ"}
      </text>
      <text x="14" y="73" className="editor-node-caption">
        {x}, {y}
      </text>
      <title>{name}</title>
    </g>
  );
});
const GraphEdge = memo(function GraphEdge({
  kind,
  path,
}: {
  kind: string;
  path: string;
}) {
  return (
    <g className="editor-edge" data-kind={kind}>
      <path
        d={path}
        fill="none"
        stroke={
          kind === "condition"
            ? "#b47a10"
            : kind === "recovery"
              ? "#d45c6a"
              : "#6f829a"
        }
        strokeWidth="2"
        strokeDasharray={kind === "condition" ? "6 4" : undefined}
        markerEnd="url(#editor-arrow)"
      />
    </g>
  );
});
