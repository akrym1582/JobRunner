import { useState } from "react";
import type { EditorDocument, EditorDraft } from "../api/@types";
import { networkNames, subtreeIds } from "./hierarchy";
import { client } from "../client";

export function MoveSelection({
  draft,
  current,
  selected,
  disabled,
  onMove,
}: {
  draft: EditorDraft;
  current: string;
  selected: string[];
  disabled: boolean;
  onMove: (
    load: () => Promise<EditorDocument>,
    confirmation: string,
  ) => Promise<void>;
}) {
  const [target, setTarget] = useState("");
  const names = networkNames(draft);
  const blocked = new Set(selected.flatMap((id) => [...subtreeIds(draft, id)]));
  const targets = draft.networks.filter(
    (n) => n.path !== current && !blocked.has(n.path),
  );
  const chosen = targets.some((n) => n.path === target)
    ? target
    : (targets[0]?.path ?? "");
  return (
    <section aria-label="ジョブと子ネットの移動" className="editor-copy">
      <label>
        選択の移動先ネット
        <select
          aria-label="選択の移動先ネット"
          value={chosen}
          disabled={disabled}
          onChange={(e) => setTarget(e.target.value)}
        >
          {targets.map((n) => (
            <option key={n.path} value={n.path}>
              {names.get(n.path) ?? n.path}
            </option>
          ))}
        </select>
      </label>
      <button
        disabled={disabled || !selected.length || !chosen}
        onClick={() =>
          void onMove(
            () =>
              client.ui.definition_editor.move_selection.$post({
                body: { draft, ids: selected, targetNetworkId: chosen },
              }),
            "選択を移動します。内部接続を保持し、以下の外部接続を切断します。継承・参照の変更も確認して下書きへ適用しますか？運用反映は別操作です。",
          )
        }
      >
        選択の移動候補を確認（{selected.length}件）
      </button>
      <small>
        Ctrl / ⌘ +
        クリックで同じ親のジョブ・子ネットを複数選択できます。座標と種別は保持します。
      </small>
    </section>
  );
}
