import { useState } from "react";
import type { EditorDocument, EditorDraft } from "../api/@types";
import { groupPath } from "./group-paths";
import { networkNames, networkNode } from "./hierarchy";
import { changeNetwork, networkType, rootParent } from "./network-changes";

export function NetworkChangeProperties({
  base,
  draft,
  current,
  disabled,
  onApply,
}: {
  base: EditorDocument;
  draft: EditorDraft;
  current: string;
  disabled: boolean;
  onApply: (next: EditorDraft, confirmation: string) => Promise<void>;
}) {
  const [target, setTarget] = useState("");
  if ((draft.editorVersion ?? 0) < 8) return null;
  const node = networkNode(draft, current);
  if (!node) return null;
  const root = draft.roots?.find((r) => r.node.id === current);
  const change = draft.networkChanges?.find((c) => c.id === current);
  const name = networkNames(draft).get(current) ?? node.name;
  const type = networkType(draft, node);
  const containers = (base.containers ?? []).filter(
    (c) => c.sourceIndex === root?.sourceIndex,
  );
  const destination =
    containers.find((c) => c.parentPath === target) ?? containers[0];
  return (
    <details className="editor-network-tools">
      <summary>ネットの移動・種別切替</summary>
      <p>
        現在: {name} · {type === "rn" ? "リカバリーネット" : "通常ネット"}
      </p>
      <p>
        属性・子孫・接続は削除しません。コマンドやコメント内のパスは自動変更しません。操作を検証した後に確認画面を表示します。
      </p>
      <fieldset disabled={disabled}>
        {root ? (
          <>
            <p>
              同じファイル内で、カレンダー・実行先・所有者等の継承条件が同じグループに移せます。ブロック外のコメントは元の位置に残ります。子ネットの別親移動、ルートと子の相互変換は未対応です。
            </p>
            <label>
              ルートの移動先
              <select
                aria-label="ルートの移動先"
                value={destination?.parentPath ?? ""}
                onChange={(e) => setTarget(e.target.value)}
              >
                {containers.map((c) => (
                  <option key={c.parentPath} value={c.parentPath}>
                    {groupPath(draft, c.parentPath)}
                  </option>
                ))}
              </select>
            </label>
            <button
              disabled={
                !destination ||
                destination.parentPath === rootParent(draft, root)
              }
              onClick={() => {
                const parent = destination!.parentPath;
                const nextName = `${groupPath(draft, parent)?.replace(/\/$/, "")}/${node.name}`;
                void onApply(
                  changeNetwork(draft, {
                    ...change,
                    id: current,
                    parentPath: parent,
                  }),
                  `ルートネットを移動しますか？\n${name} → ${nextName}\n同じファイル・同じ継承条件です。属性・接続・子孫の削除はありません。完全名は子孫も変わります。コマンド内のパスは手動で確認してください。`,
                );
              }}
            >
              移動を検証
            </button>
          </>
        ) : (
          <>
            <p>
              同じ親の中で通常ネットとリカバリーネットを切り替えます。条件接続・先行なしのリカバリー化・不正な後続接続は拒否します。子孫の種別は変わりません。
            </p>
            <button
              onClick={() => {
                const next = type === "rn" ? "n" : "rn";
                const meaning =
                  next === "rn"
                    ? "通常先行が異常・タイムアウト・中断したときに開始するリカバリーに変わります。リカバリー先行には成功を要求します。"
                    : "通常ネットの成功条件で開始する方式に変わります。異常時だけの起動ではなくなります。";
                void onApply(
                  changeNetwork(draft, { ...change, id: current, type: next }),
                  `${name}: ${type} → ${next} に切り替えますか？\n${meaning}\n属性・接続の削除はありません。子孫の種別は変更しません。`,
                );
              }}
            >
              {type === "rn"
                ? "通常ネットへの切替を検証"
                : "リカバリーへの切替を検証"}
            </button>
          </>
        )}
        {change && (
          <button
            onClick={() =>
              void onApply(
                {
                  ...draft,
                  networkChanges: draft.networkChanges!.filter(
                    (c) => c.id !== current,
                  ),
                },
                `${name}の移動・種別切替を取り消しますか？属性編集は保持します。`,
              )
            }
          >
            このネットの移動・切替を取り消す
          </button>
        )}
      </fieldset>
    </details>
  );
}
