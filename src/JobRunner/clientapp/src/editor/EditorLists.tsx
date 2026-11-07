import { memo, useMemo } from "react";
import { X } from "lucide-react";
import type { EditorNetwork, EditorNode } from "../api/@types";

export function EditorLists({
  network,
  selected,
  typeOf,
  onSelect,
  onRemoveEdge,
}: {
  network: EditorNetwork;
  selected: string;
  typeOf: (node: EditorNode) => string;
  onSelect: (id: string) => void;
  onRemoveEdge: (index: number) => void;
}) {
  // 接続ごとの全ノード探索を避け、改名後の表示も同じ索引から取り出します。
  const names = useMemo(() => {
    const values = new Map<string, string>();
    for (const node of network.nodes)
      if (!values.has(node.id)) values.set(node.id, node.name);
    return values;
  }, [network.nodes]);
  return (
    <div className="editor-lists">
      <section>
        <h3>
          ユニット一覧 <span>{network.nodes.length}</span>
        </h3>
        <div
          className="editor-unit-list"
          onClick={(e) => {
            if (!(e.target instanceof Element)) return;
            const button = e.target.closest<HTMLButtonElement>(
              "button[data-unit-id]",
            );
            if (button && e.currentTarget.contains(button))
              onSelect(button.dataset.unitId!);
          }}
        >
          {network.nodes.map((n) => (
            <UnitRow
              key={n.id}
              id={n.id}
              name={n.name}
              type={typeOf(n)}
              x={n.x}
              y={n.y}
              selected={selected === n.id}
            />
          ))}
        </div>
      </section>
      <section>
        <h3>
          接続一覧 <span>{network.edges.length}</span>
        </h3>
        <div
          className="editor-connection-list"
          onClick={(e) => {
            if (!(e.target instanceof Element)) return;
            const button = e.target.closest<HTMLButtonElement>(
              "button[data-edge-index]",
            );
            if (button && e.currentTarget.contains(button))
              onRemoveEdge(Number(button.dataset.edgeIndex));
          }}
        >
          {network.edges.map((edge, index) => (
            <ConnectionRow
              key={`${edge.from}:${edge.to}:${index}`}
              index={index}
              from={names.get(edge.from)}
              to={names.get(edge.to)}
              conditional={edge.conditional}
            />
          ))}
          {!network.edges.length && (
            <p className="editor-muted">接続はありません。</p>
          )}
        </div>
      </section>
    </div>
  );
}

// 操作は親へ委譲し、表示部品が古い下書きやイベント関数を保持しないようにします。
const UnitRow = memo(function UnitRow({
  id,
  name,
  type,
  x,
  y,
  selected,
}: {
  id: string;
  name: string;
  type: string;
  x: number | null;
  y: number | null;
  selected: boolean;
}) {
  return (
    <button data-unit-id={id} aria-pressed={selected}>
      <span className="outline-pill">{type}</span>
      {name}
      <small>
        {x ?? "—"}, {y ?? "—"}
      </small>
    </button>
  );
});
const ConnectionRow = memo(function ConnectionRow({
  index,
  from,
  to,
  conditional,
}: {
  index: number;
  from?: string;
  to?: string;
  conditional: boolean;
}) {
  return (
    <div>
      <span>
        {from} <span className="editor-muted">→</span> {to}
      </span>
      <small>{conditional ? "条件成立" : "通常順序"}</small>
      <button aria-label={`接続 ${index + 1} を削除`} data-edge-index={index}>
        <X size={14} />
      </button>
    </div>
  );
});
