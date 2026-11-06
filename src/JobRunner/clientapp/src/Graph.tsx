import { useEffect, useMemo, useRef, useState } from "react";
import { Maximize, Minus, Plus } from "lucide-react";
import type { Graph as GraphData } from "./api/@types";
import { label, tone } from "./status";
import { fixedLayout } from "./layout";

export function Graph({
  data,
  selected,
  onSelect,
}: {
  data: GraphData;
  selected: string;
  onSelect: (id: string) => void;
}) {
  const [camera, setCamera] = useState({ x: 0, y: 0, scale: 1 });
  const svg = useRef<SVGSVGElement>(null);
  const drag = useRef<
    { x: number; y: number; cx: number; cy: number } | undefined
  >(undefined);
  const structure = JSON.stringify({
    nodes: data.nodes.map((n) => ({ path: n.path, x: n.x, y: n.y })),
    edges: data.edges,
  });
  const { layout, error } = useMemo(() => {
    try {
      return { layout: fixedLayout(JSON.parse(structure)), error: "" };
    } catch (e) {
      return { layout: undefined, error: String(e) };
    }
  }, [structure]);
  useEffect(() => {
    if (!layout || !svg.current) return;
    const size = svg.current.getBoundingClientRect();
    const scale = Math.min(
      1,
      size.width / layout.width,
      size.height / layout.height,
    );
    setCamera({
      x: (size.width - layout.width * scale) / 2,
      y: (size.height - layout.height * scale) / 2,
      scale,
    });
  }, [layout]);
  function fit() {
    if (!layout || !svg.current) return;
    const size = svg.current.getBoundingClientRect();
    const scale = Math.min(
      1.5,
      size.width / (layout.width ?? 1),
      size.height / (layout.height ?? 1),
    );
    setCamera({
      x: (size.width - (layout.width ?? 0) * scale) / 2,
      y: (size.height - (layout.height ?? 0) * scale) / 2,
      scale,
    });
  }
  function zoom(factor: number, x?: number, y?: number) {
    const size = svg.current?.getBoundingClientRect();
    const px = x ?? (size?.width ?? 800) / 2,
      py = y ?? (size?.height ?? 400) / 2;
    setCamera((c) => {
      const scale = Math.max(0.2, Math.min(2.5, c.scale * factor));
      return {
        scale,
        x: px - ((px - c.x) * scale) / c.scale,
        y: py - ((py - c.y) * scale) / c.scale,
      };
    });
  }
  return (
    <div className="graph-area">
      <div className="canvas-label">
        DEPENDENCY MAP <span>{data.nodes?.length ?? 0} ユニット</span>
      </div>
      {error && <div role="alert">定義座標を表示できません: {error}</div>}
      <svg
        ref={svg}
        className="graph"
        aria-label="ジョブネット依存関係"
        data-ready={Boolean(layout)}
        onWheel={(e) => {
          const r = e.currentTarget.getBoundingClientRect();
          zoom(
            e.deltaY < 0 ? 1.1 : 1 / 1.1,
            e.clientX - r.left,
            e.clientY - r.top,
          );
        }}
        onPointerDown={(e) => {
          if (e.button !== 0) return;
          drag.current = {
            x: e.clientX,
            y: e.clientY,
            cx: camera.x,
            cy: camera.y,
          };
          e.currentTarget.setPointerCapture(e.pointerId);
        }}
        onPointerMove={(e) => {
          const d = drag.current;
          if (d)
            setCamera((c) => ({
              ...c,
              x: d.cx + e.clientX - d.x,
              y: d.cy + e.clientY - d.y,
            }));
        }}
        onPointerUp={() => {
          drag.current = undefined;
        }}
        onPointerCancel={() => {
          drag.current = undefined;
        }}
      >
        <defs>
          {["normal", "condition", "recovery"].map((kind) => (
            <marker
              key={kind}
              id={"arrow-" + kind}
              markerWidth="9"
              markerHeight="9"
              refX="8"
              refY="4.5"
              orient="auto"
            >
              <path
                d="M0 0 L9 4.5 L0 9 Z"
                fill={kind === "recovery" ? "#db6571" : "#8b9bb0"}
              />
            </marker>
          ))}
        </defs>
        <g
          transform={`translate(${camera.x},${camera.y}) scale(${camera.scale})`}
          data-testid="graph-camera"
        >
          {layout?.edges?.map((edge) => {
            const kind =
              data.edges?.find((e) => e.id === edge.id)?.kind ?? "normal";
            return (
              <g key={edge.id}>
                {edge.sections?.map((section, i) => {
                  const pts = [
                    section.startPoint,
                    ...(section.bendPoints ?? []),
                    section.endPoint,
                  ];
                  return (
                    <path
                      key={i}
                      d={pts
                        .map((p, j) => `${j ? "L" : "M"} ${p.x} ${p.y}`)
                        .join(" ")}
                      fill="none"
                      stroke={kind === "recovery" ? "#db6571" : "#8b9bb0"}
                      strokeWidth="1.7"
                      strokeDasharray={kind === "normal" ? undefined : "5 4"}
                      markerEnd={`url(#arrow-${kind})`}
                    />
                  );
                })}
                {kind !== "normal" && edge.sections?.[0] && (
                  <text
                    x={
                      (edge.sections[0].startPoint.x +
                        edge.sections[0].endPoint.x) /
                        2 +
                      8
                    }
                    y={
                      (edge.sections[0].startPoint.y +
                        edge.sections[0].endPoint.y) /
                        2 -
                      8
                    }
                    fontSize="10"
                    fill="#8c6471"
                  >
                    {kind === "condition" ? "条件成立" : "異常時"}
                  </text>
                )}
              </g>
            );
          })}
          {layout?.children?.map((node) => {
            const info = data.nodes?.find((n) => n.path === node.id);
            if (!info) return null;
            const color = tone(info.status);
            return (
              <g
                role="button"
                tabIndex={0}
                aria-label={`${info.name} ${label(info.status)}`}
                aria-pressed={selected === info.path}
                key={node.id}
                className={`job-node ${color} ${selected === info.path ? "selected" : ""}`}
                transform={`translate(${node.x},${node.y})`}
                onPointerDown={(e) => e.stopPropagation()}
                onClick={() => onSelect(info.path)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    onSelect(info.path);
                  }
                }}
              >
                <title>
                  {info.path} — {label(info.status)}
                </title>
                <rect className="node-body" width="190" height="88" rx="9" />
                <rect
                  className="node-icon"
                  x="14"
                  y="14"
                  width="27"
                  height="27"
                  rx="6"
                />
                <text
                  x="27.5"
                  y="33"
                  textAnchor="middle"
                  fontSize="14"
                  fill="white"
                >
                  {info.isNetwork ? "▦" : info.isRecovery ? "R" : "›_"}
                </text>
                <text x="51" y="25" className="node-kind">
                  {info.isNetwork
                    ? "JOBNET"
                    : info.isRecovery
                      ? "RECOVERY"
                      : "JOB"}{" "}
                  · {info.type}
                </text>
                <text x="51" y="42" className="node-name">
                  {info.name.length > 9
                    ? info.name.slice(0, 9) + "…"
                    : info.name}
                </text>
                <line x1="14" y1="55" x2="176" y2="55" stroke="#e9edf3" />
                <circle cx="20" cy="71" r="3.5" className="state-dot" />
                <text x="30" y="75" className="node-status">
                  {label(info.status)}
                </text>
                {info.isNetwork && (
                  <text x="167" y="75" fill="#64748b">
                    ↗
                  </text>
                )}
              </g>
            );
          })}
        </g>
      </svg>
      {!layout && !error && (
        <div className="canvas-loading">定義座標を読み込んでいます…</div>
      )}
      <div className="canvas-bottom">
        <span>ドラッグで移動 · スクロールで拡大縮小</span>
        <div className="zoom-controls">
          <button aria-label="縮小" onClick={() => zoom(1 / 1.2)}>
            <Minus size={16} />
          </button>
          <span>{Math.round(camera.scale * 100)}%</span>
          <button aria-label="拡大" onClick={() => zoom(1.2)}>
            <Plus size={16} />
          </button>
          <button aria-label="全体表示" onClick={fit}>
            <Maximize size={16} />
          </button>
        </div>
      </div>
    </div>
  );
}
