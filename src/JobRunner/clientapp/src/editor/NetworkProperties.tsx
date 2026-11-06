import { useState } from "react";
import type { EditorDocument, EditorDraft } from "../api/@types";
import {
  addRoot,
  networkNode,
  removeHierarchyNode,
  subtreeIds,
  updateNetworkNode,
} from "./hierarchy";

export function NetworkProperties({
  base,
  draft,
  current,
  onChange,
  onNavigate,
}: {
  base: EditorDocument;
  draft: EditorDraft;
  current: string;
  onChange: (draft: EditorDraft) => void;
  onNavigate: (id: string) => void;
}) {
  const [target, setTarget] = useState(0);
  const [name, setName] = useState("新規ネット");
  if (draft.editorVersion !== 3) return null;
  const node = networkNode(draft, current);
  const containers = base.containers ?? [];
  return (
    <section className="editor-network-tools" aria-label="ネットの編集">
      <details>
        <summary>ルートネットを作成</summary>
        <div className="editor-network-fields">
          <label>
            追加先の原文とグループ
            <select
              aria-label="ルートの追加先"
              value={target}
              onChange={(e) => setTarget(Number(e.target.value))}
            >
              {containers.map((c, i) => (
                <option key={i} value={i}>
                  {base.sources[c.sourceIndex].fileName} · {c.parentPath} ·{" "}
                  {base.sources[c.sourceIndex].encoding}
                </option>
              ))}
            </select>
          </label>
          <label>
            新規ルート名
            <input value={name} onChange={(e) => setName(e.target.value)} />
          </label>
          <button
            disabled={!name.trim() || !containers[target]}
            onClick={() => {
              const placement = containers[target];
              const next = addRoot(
                draft,
                placement.sourceIndex,
                placement.parentPath,
                name,
              );
              onChange(next);
              onNavigate(next.networks.at(-1)!.path);
            }}
          >
            ルートネットを追加
          </button>
        </div>
        <p className="editor-muted">
          新規ルートは予定なし・保留ありで作成します。既存ファイルの文字コードを使い、採用・実行には反映しません。リカバリーネットは子として追加できます。
        </p>
      </details>
      {node && (
        <div className="editor-network-fields">
          <label>
            ネット名
            <input
              value={node.name}
              onChange={(e) =>
                onChange(
                  updateNetworkNode(draft, { ...node, name: e.target.value }),
                )
              }
            />
          </label>
          <label>
            ネットのコメント
            <input
              value={node.attributes.cm?.[0] ?? ""}
              onChange={(e) => {
                const attributes = { ...node.attributes };
                if (e.target.value) attributes.cm = [e.target.value];
                else delete attributes.cm;
                onChange(updateNetworkNode(draft, { ...node, attributes }));
              }}
            />
          </label>
          <button
            className="editor-delete"
            onClick={() => {
              if (
                window.confirm(
                  `「${node.name}」と子孫を含む${subtreeIds(draft, current).size}ユニット、親の関連接続を削除しますか？`,
                )
              ) {
                const next = removeHierarchyNode(draft, current);
                onChange(next);
                onNavigate(next.networks[0]?.path ?? "");
              }
            }}
          >
            このネットを削除
          </button>
        </div>
      )}
    </section>
  );
}
