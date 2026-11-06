import { useState } from "react";
import type {
  EditorDocument,
  EditorDraft,
  EditorFileCreation,
} from "../api/@types";
import { groupPath, originalGroupPath } from "./group-paths";
import { client } from "../client";
import { createFile, createGroup, creationDocument } from "./creations";

export function CreationProperties({
  base,
  draft,
  disabled,
  onAdd,
  onImport,
}: {
  base?: EditorDocument;
  draft?: EditorDraft;
  disabled: boolean;
  onAdd: (draft: EditorDraft) => Promise<void>;
  onImport: (load: () => Promise<EditorDocument>) => Promise<void>;
}) {
  const [target, setTarget] = useState(0);
  const [name, setName] = useState("新規グループ");
  const [file, setFile] = useState<EditorFileCreation>({
    fileName: "new.ajs",
    targetPath: "/",
    encoding: "utf-8",
    bom: false,
    groupName: "新規グループ",
  });
  const document = base && draft ? creationDocument(base, draft) : undefined;
  const containers = document?.containers ?? [];
  const enabled = [6, 7].includes(draft?.editorVersion ?? 0);
  return (
    <section
      className="editor-network-tools"
      aria-label="グループとファイルの作成"
    >
      <details>
        <summary>グループ・Unitファイルを作成</summary>
        <p>
          追加内容はブラウザーの下書きだけに保存します。実行中の定義や予定は変わりません。グループの移動は未対応です。作成は元に戻せます。
        </p>
        <fieldset disabled={disabled}>
          <div className="editor-network-fields">
            <label>
              グループの追加先
              <select
                aria-label="グループの追加先"
                value={target}
                onChange={(e) => setTarget(Number(e.target.value))}
              >
                {containers.map((c, i) => (
                  <option key={i} value={i}>
                    {document!.sources[c.sourceIndex].fileName} ·{" "}
                    {groupPath(draft!, c.parentPath)}
                  </option>
                ))}
              </select>
            </label>
            <label>
              新規グループ名
              <input value={name} onChange={(e) => setName(e.target.value)} />
            </label>
            <button
              disabled={!enabled || !containers[target] || !name.trim()}
              onClick={() =>
                void onAdd(createGroup(draft!, { ...containers[target], name }))
              }
            >
              グループを追加
            </button>
          </div>
          <div className="editor-network-fields">
            <label>
              新規ファイル名
              <input
                value={file.fileName}
                onChange={(e) => setFile({ ...file, fileName: e.target.value })}
              />
            </label>
            <label>
              新規ファイルの論理配置先
              <input
                value={file.targetPath}
                onChange={(e) =>
                  setFile({ ...file, targetPath: e.target.value })
                }
              />
            </label>
            <label>
              新規ファイルの文字コード
              <select
                aria-label="新規ファイルの文字コード"
                value={file.encoding}
                onChange={(e) =>
                  setFile({
                    ...file,
                    encoding: e.target.value,
                    bom: e.target.value === "shift_jis" ? false : file.bom,
                  })
                }
              >
                <option value="utf-8">UTF-8</option>
                <option value="utf-16">UTF-16LE</option>
                <option value="utf-16BE">UTF-16BE</option>
                <option value="shift_jis">Shift_JIS</option>
              </select>
            </label>
            <label>
              <input
                type="checkbox"
                checked={file.bom}
                disabled={file.encoding === "shift_jis"}
                onChange={(e) => setFile({ ...file, bom: e.target.checked })}
              />
              新規ファイルにBOMを付ける
            </label>
            <label>
              初期グループ名
              <input
                value={file.groupName}
                onChange={(e) =>
                  setFile({ ...file, groupName: e.target.value })
                }
              />
            </label>
            <button
              disabled={!enabled || !draft?.localSources}
              onClick={() =>
                void onAdd(
                  createFile(draft!, {
                    ...file,
                    targetPath: originalGroupPath(draft!, file.targetPath),
                  }),
                )
              }
            >
              下書きにファイルを追加
            </button>
            <button
              onClick={() =>
                void onImport(() =>
                  client.ui.definition_editor.local.new.$post({ body: file }),
                )
              }
            >
              新規ローカルファイルで開始
            </button>
          </div>
          <p>
            新規ファイルは空のグループから開始します。既存下書きへのファイル追加はローカル入力の下書きだけで利用できます。UTF-8/UTF-16のBOMは明示し、文字コードは推測しません。
          </p>
        </fieldset>
      </details>
    </section>
  );
}
