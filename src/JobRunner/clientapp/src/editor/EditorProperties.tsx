import type { EditorNode } from "../api/@types";
import { canEdit, isJob, isJudgment, isNetwork } from "./model";
import { JudgmentProperties } from "./JudgmentProperties";

export function EditorProperties({
  node,
  onChange,
  onDelete,
  onOpen,
  editorVersion,
  displayType,
}: {
  node?: EditorNode;
  onChange: (node: EditorNode) => void;
  onDelete: () => void;
  onOpen: () => void;
  editorVersion: number;
  displayType?: string;
}) {
  if (!node)
    return (
      <aside className="editor-properties">
        <h3>プロパティ</h3>
        <p className="empty">
          図またはユニット一覧から
          <br />
          ジョブを選択してください。
        </p>
      </aside>
    );
  const editable = canEdit(node.type, editorVersion);
  const value = (key: string) => node.attributes[key]?.[0] ?? "";
  function field(key: string, text: string) {
    const attributes = { ...node!.attributes };
    if (text === "") delete attributes[key];
    else attributes[key] = [text];
    onChange({ ...node!, attributes });
  }
  return (
    <aside className="editor-properties" aria-label="ジョブのプロパティ">
      <div className="editor-properties-title">
        <h3>プロパティ</h3>
        <span className="outline-pill">{displayType ?? node.type}</span>
      </div>
      <label>
        {isNetwork(node.type) ? "子ネット名" : "ジョブ名"}
        <input
          disabled={!editable}
          value={node.name}
          onChange={(e) => onChange({ ...node, name: e.target.value })}
        />
      </label>
      <label>
        コメント
        <textarea
          disabled={!editable}
          rows={2}
          value={value("cm")}
          onChange={(e) => field("cm", e.target.value)}
        />
      </label>
      {isJob(node.type) && (
        <>
          <label>
            実行方法
            <select
              value={"sc" in node.attributes ? "script" : "command"}
              onChange={(e) => {
                const attributes = { ...node.attributes };
                if (e.target.value === "script") {
                  delete attributes.te;
                  attributes.sc = [""];
                } else {
                  delete attributes.sc;
                  delete attributes.prm;
                  attributes.te = [""];
                }
                onChange({ ...node, attributes });
              }}
            >
              <option value="command">コマンドテキスト (te)</option>
              <option value="script">実行ファイル (sc / prm)</option>
            </select>
          </label>
          {"sc" in node.attributes ? (
            <>
              <label>
                実行ファイル
                <input
                  value={value("sc")}
                  onChange={(e) =>
                    onChange({
                      ...node,
                      attributes: { ...node.attributes, sc: [e.target.value] },
                    })
                  }
                />
              </label>
              <label>
                引数
                <input
                  value={value("prm")}
                  onChange={(e) => field("prm", e.target.value)}
                />
              </label>
            </>
          ) : (
            <label>
              コマンド
              <textarea
                rows={3}
                spellCheck={false}
                value={value("te")}
                onChange={(e) => field("te", e.target.value)}
              />
            </label>
          )}
          <label>
            作業ディレクトリ
            <input
              value={value("wkp")}
              placeholder="実行プロセスの既定値"
              onChange={(e) => field("wkp", e.target.value)}
            />
          </label>
          <label>
            環境変数 <small>NAME=VALUE を1行に1件</small>
            <textarea
              rows={3}
              spellCheck={false}
              value={(node.attributes.env ?? []).join("\n")}
              onChange={(e) =>
                onChange({
                  ...node,
                  attributes: {
                    ...node.attributes,
                    env:
                      e.target.value === "" ? [] : e.target.value.split("\n"),
                  },
                })
              }
            />
          </label>
        </>
      )}
      {editable && isJudgment(node.type) && (
        <JudgmentProperties node={node} onChange={onChange} />
      )}
      <fieldset>
        <legend>配置座標 (el)</legend>
        <div className="editor-coordinates">
          <label>
            水平 H
            <input
              type="number"
              min={0}
              max={16000}
              value={node.x ?? ""}
              onChange={(e) =>
                onChange({
                  ...node,
                  x: e.target.value === "" ? null : Number(e.target.value),
                  y: e.target.value === "" ? null : (node.y ?? 48),
                })
              }
            />
          </label>
          <label>
            垂直 V
            <input
              type="number"
              min={0}
              max={10000}
              value={node.y ?? ""}
              onChange={(e) =>
                onChange({
                  ...node,
                  y: e.target.value === "" ? null : Number(e.target.value),
                  x: e.target.value === "" ? null : (node.x ?? 80),
                })
              }
            />
          </label>
        </div>
        <small>ドラッグするとJP1のグリッドに配置します。</small>
      </fieldset>
      {isNetwork(node.type) && <button onClick={onOpen}>子ネットを編集</button>}
      {editable && (
        <button className="editor-delete" onClick={onDelete}>
          {isNetwork(node.type) ? "子ネットを削除" : "ジョブを削除"}
        </button>
      )}
      {!editable && (
        <p className="editor-muted">
          この種別は配置を編集できます。属性は原文を保持します。
        </p>
      )}
    </aside>
  );
}
