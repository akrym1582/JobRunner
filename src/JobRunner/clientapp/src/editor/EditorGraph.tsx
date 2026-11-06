import { useMemo, useRef, useState } from "react";
import { Maximize, Minus, Plus } from "lucide-react";
import type { EditorNetwork, EditorNode } from "../api/@types";
import { fixedLayout, NODE_HEIGHT, NODE_WIDTH } from "../layout";
import { snap } from "./model";

export function EditorGraph({
  network,
  selected,
  connecting,
  onSelect,
  onMove,
  onOpen,
}: {
  network: EditorNetwork;
  selected: string;
  connecting: boolean;
  onSelect: (id: string) => void;
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
                } else onSelect(current.node.id);
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
              {layout.edges.map((edge, index) => (
                <g
                  key={edge.id}
                  className="editor-edge"
                  data-kind={
                    network.edges[index].conditional
                      ? "condition"
                      : ["rj", "rp", "rn", "rjdj"].includes(
                            nodesById.get(network.edges[index].to)?.type ?? "",
                          )
                        ? "recovery"
                        : "normal"
                  }
                >
                  {edge.sections.map((section, i) => (
                    <path
                      key={i}
                      d={[
                        section.startPoint,
                        ...section.bendPoints,
                        section.endPoint,
                      ]
                        .map((p, index) => `${index ? "L" : "M"}${p.x},${p.y}`)
                        .join(" ")}
                      fill="none"
                      stroke={
                        network.edges[index].conditional
                          ? "#b47a10"
                          : ["rj", "rp", "rn", "rjdj"].includes(
                                nodesById.get(network.edges[index].to)?.type ??
                                  "",
                              )
                            ? "#d45c6a"
                            : "#6f829a"
                      }
                      strokeWidth="2"
                      strokeDasharray={
                        network.edges[index].conditional ? "6 4" : undefined
                      }
                      markerEnd="url(#editor-arrow)"
                    />
                  ))}
                </g>
              ))}
              {layout.children.map((position) => {
                const node = nodesById.get(position.id)!;
                const shift =
                  moving?.id === node.id ? moving : { dx: 0, dy: 0 };
                return (
                  <g
                    key={node.id}
                    role="button"
                    tabIndex={0}
                    aria-label={`編集 ${node.name}`}
                    aria-pressed={selected === node.id}
                    className={`editor-node ${selected === node.id ? "selected" : ""}`}
                    transform={`translate(${position.x + shift.dx},${position.y + shift.dy})`}
                    onPointerDown={(e) => {
                      if (e.button !== 0) return;
                      if (connecting) {
                        onSelect(node.id);
                        return;
                      }
                      const p = point(e.clientX, e.clientY);
                      drag.current = { node, x: p.x, y: p.y, moved: false };
                      e.currentTarget.setPointerCapture(e.pointerId);
                    }}
                    onDoubleClick={() => onOpen(node.id)}
                    onKeyDown={(e) => {
                      if (e.key === "Enter" || e.key === " ") {
                        e.preventDefault();
                        onSelect(node.id);
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
                    <text
                      x="28"
                      y="37"
                      textAnchor="middle"
                      className="editor-node-type"
                    >
                      {node.type.toUpperCase()}
                    </text>
                    <text x="54" y="31" className="editor-node-name">
                      {node.name.length > 9
                        ? node.name.slice(0, 9) + "…"
                        : node.name}
                    </text>
                    <text x="54" y="51" className="editor-node-caption">
                      {node.type === "n" || node.type === "rn"
                        ? "ジョブネット"
                        : node.type === "rj" || node.type === "rp"
                          ? "リカバリージョブ"
                          : "ジョブ"}
                    </text>
                    <text x="14" y="73" className="editor-node-caption">
                      {node.x}, {node.y}
                    </text>
                    <title>{node.name}</title>
                  </g>
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
