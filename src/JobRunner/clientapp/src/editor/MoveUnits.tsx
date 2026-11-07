import { useState } from "react";
import type { EditorDocument, EditorDraft, EditorSource } from "../api/@types";
import { client } from "../client";
import { rootParent } from "./network-changes";
import { groupPath } from "./group-paths";

export function MoveUnits({
  draft,
  sources,
  disabled,
  onMove,
}: {
  draft: EditorDraft;
  sources: EditorSource[];
  disabled: boolean;
  onMove: (
    load: () => Promise<EditorDocument>,
    confirmation: string,
  ) => Promise<void>;
}) {
  const [path, setPath] = useState("");
  const [parent, setParent] = useState("/");
  const [index, setIndex] = useState(0);
  const groups = [
    ...new Set(
      [
        ...(draft.calendars ?? []).map((c) => groupPath(draft, c.id)),
        ...(draft.newFiles ?? []).map((f) =>
          groupPath(draft, f.targetPath.replace(/\/$/, "") + "/" + f.groupName),
        ),
        ...(draft.newGroups ?? []).map((g) =>
          groupPath(draft, g.parentPath.replace(/\/$/, "") + "/" + g.name),
        ),
      ].filter((p): p is string => !!p && p !== "/"),
    ),
  ];
  const roots = (draft.roots ?? []).flatMap((r) => {
    const parent = groupPath(draft, rootParent(draft, r));
    return parent && r.node.type === "n"
      ? [parent.replace(/\/$/, "") + "/" + r.node.name]
      : [];
  });
  const files = sources;
  const targets = [...groups, ...roots];
  const selected = targets.includes(path) ? path : (targets[0] ?? "");
  return (
    <details className="editor-network-tools">
      <summary>グループ・ルートネットの移動</summary>
      <p>
        省略属性は移動先から継承します。参照と継承条件の変更を確認してから下書きへ適用します。
      </p>
      <fieldset disabled={disabled}>
        <label>
          移動するユニット
          <select
            aria-label="移動するユニット"
            value={selected}
            onChange={(e) => setPath(e.target.value)}
          >
            {targets.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </select>
        </label>
        <label>
          移動先グループ
          <select
            aria-label="移動先グループ"
            value={parent}
            onChange={(e) => setParent(e.target.value)}
          >
            {["/", ...groups].map((p) => (
              <option key={p}>{p}</option>
            ))}
          </select>
        </label>
        <label>
          移動先ファイル
          <select
            aria-label="移動先ファイル"
            value={index}
            onChange={(e) => setIndex(Number(e.target.value))}
          >
            {files.map((s, i) => (
              <option key={i} value={i}>
                {s.fileName} ({s.targetPath})
              </option>
            ))}
          </select>
        </label>
        <button
          disabled={!selected}
          onClick={() =>
            void onMove(
              () =>
                client.ui.definition_editor.move.$post({
                  body: {
                    draft,
                    path: selected,
                    parentPath: parent,
                    sourceIndex: index,
                    fileName: files[index]?.fileName,
                    fileTargetPath: files[index]?.targetPath,
                    fileEncoding: files[index]?.encoding,
                  },
                }),
              "移動候補を下書きへ適用しますか？運用反映は別操作です。",
            )
          }
        >
          移動候補を確認
        </button>
      </fieldset>
    </details>
  );
}
