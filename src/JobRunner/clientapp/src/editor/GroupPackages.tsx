import { useState } from "react";
import type { EditorDocument, EditorDraft } from "../api/@types";
import { client } from "../client";
import { groupPath } from "./group-paths";

export function GroupPackages({
  draft,
  disabled,
  onImport,
  onExport,
}: {
  draft: EditorDraft;
  disabled: boolean;
  onImport: (
    load: () => Promise<EditorDocument>,
    confirmation: string,
  ) => Promise<void>;
  onExport: (path: string) => Promise<void>;
}) {
  const [selected, setSelected] = useState("");
  const [parent, setParent] = useState("/");
  const [name, setName] = useState("");
  const [mode, setMode] = useState("add");
  const [file, setFile] = useState<File>();
  const groups = [
    ...new Set(
      [
        ...(draft.calendars ?? []).map((g) => groupPath(draft, g.id)),
        ...(draft.newGroups ?? []).map((g) =>
          groupPath(draft, g.parentPath.replace(/\/$/, "") + "/" + g.name),
        ),
      ].filter((p): p is string => p !== null && p !== "/"),
    ),
  ].sort();
  const path = groups.includes(selected) ? selected : (groups[0] ?? "");
  async function load() {
    if (!file || !file.size || file.size > 8 * 1024 * 1024)
      throw new Error("グループZIPは8MiBまでです。");
    const bytes = new Uint8Array(await file.arrayBuffer());
    let text = "";
    for (let i = 0; i < bytes.length; i += 8192)
      text += String.fromCharCode(...bytes.subarray(i, i + 8192));
    return client.ui.definition_editor.groups.import.$post({
      body: {
        draft,
        path,
        zipBase64: btoa(text),
        parentPath: mode === "add" ? parent : null,
        name: mode === "add" ? name : null,
      },
    });
  }
  return (
    <details className="editor-network-tools">
      <summary>グループのエクスポート・インポート</summary>
      <p>
        選択グループと配下をZIPで持ち運びます。外部カレンダーへの依存がある出力は拒否します。取り込みは下書きだけに適用し、元に戻せます。運用へ反映するには差分確認が必要です。新規グループは運用反映後も停止中です。
      </p>
      <fieldset disabled={disabled}>
        <label>
          入出力グループ
          <select
            aria-label="入出力グループ"
            value={path}
            onChange={(e) => setSelected(e.target.value)}
          >
            {!groups.length && <option value="">グループなし</option>}
            {groups.map((g) => (
              <option key={g}>{g}</option>
            ))}
          </select>
        </label>
        <button disabled={!path} onClick={() => void onExport(path)}>
          グループZIPをダウンロード
        </button>
        <label>
          グループZIP
          <input
            aria-label="グループZIP"
            type="file"
            accept=".zip"
            onChange={(e) => setFile(e.target.files?.[0])}
          />
        </label>
        <label>
          取込方法
          <select
            aria-label="グループ取込方法"
            value={mode}
            onChange={(e) => setMode(e.target.value)}
          >
            <option value="add">新規追加</option>
            <option value="replace">選択グループを置換</option>
          </select>
        </label>
        {mode === "add" && (
          <>
            <label>
              追加先
              <select
                aria-label="グループ取込先"
                value={parent}
                onChange={(e) => setParent(e.target.value)}
              >
                <option>/</option>
                {groups.map((g) => (
                  <option key={g}>{g}</option>
                ))}
              </select>
            </label>
            <label>
              追加グループ名
              <input
                aria-label="追加グループ名"
                value={name}
                onChange={(e) => setName(e.target.value)}
              />
            </label>
          </>
        )}
        <button
          disabled={!file || (mode === "replace" ? !path : !name.trim())}
          onClick={() =>
            void onImport(
              load,
              mode === "replace"
                ? `${path} と配下の定義をZIPで置換しますか？削除と予定への影響はプレビューで確認してください。対象外のグループは維持します。`
                : `${parent.replace(/\/$/, "")}/${name} を下書きに追加しますか？省略属性は追加先から継承します。`,
            )
          }
        >
          グループを下書きへ取り込む
        </button>
      </fieldset>
    </details>
  );
}
