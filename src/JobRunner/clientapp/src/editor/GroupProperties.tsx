import { useState } from "react";
import type { EditorDraft } from "../api/@types";
import { groupPath } from "./group-paths";
import { movedGroupConflict } from "./network-changes";
import { changeGroup } from "./groups";

export function GroupProperties({
  draft,
  disabled,
  onApply,
}: {
  draft: EditorDraft;
  disabled: boolean;
  onApply: (draft: EditorDraft) => Promise<void>;
}) {
  const [selected, setSelected] = useState("");
  const [name, setName] = useState("");
  if (![7, 8].includes(draft.editorVersion ?? 1)) return null;
  const groups = (draft.calendars ?? []).filter(
    (c) => groupPath(draft, c.id) !== null,
  );
  const group = groups.find((c) => c.id === selected) ?? groups[0];
  const moveConflict = group ? movedGroupConflict(draft, group.id) : false;
  return (
    <details className="editor-network-tools">
      <summary>グループの名前変更・削除</summary>
      <p>
        名前変更では子孫とカレンダー継承を保ち、jc参照と表示上の子グループ名を追従させます。コマンド・コメント内の文字列は変更しません。独立ファイルの配置先に関わる変更、外部参照が残る削除、空ファイルになる削除はできません。
      </p>
      {moveConflict && (
        <p>
          移動中のネットが元または先のグループにあるため、ネットの移動を取り消してからグループを削除してください。
        </p>
      )}
      <fieldset disabled={disabled || !group}>
        <div className="editor-network-fields">
          <label>
            操作するグループ
            <select
              aria-label="操作するグループ"
              value={group?.id ?? ""}
              onChange={(e) => {
                setSelected(e.target.value);
                setName("");
              }}
            >
              {groups.map((c) => (
                <option key={c.id} value={c.id}>
                  {groupPath(draft, c.id)}
                </option>
              ))}
            </select>
          </label>
          <label>
            変更後のグループ名
            <input value={name} onChange={(e) => setName(e.target.value)} />
          </label>
          <button
            disabled={!name.trim() || !group}
            onClick={() =>
              void onApply(changeGroup(draft, { id: group!.id, name }))
            }
          >
            グループ名を変更
          </button>
          <button
            disabled={!group || moveConflict}
            onClick={() => {
              if (
                window.confirm(
                  `${groupPath(draft, group!.id)} と子孫を下書きから削除しますか？元に戻す操作で復元できます。`,
                )
              )
                void onApply(
                  changeGroup(draft, { id: group!.id, delete: true }),
                );
            }}
          >
            グループと子孫を削除
          </button>
        </div>
      </fieldset>
    </details>
  );
}
