import { useMemo, useState } from "react";
import type { EditorDraft } from "../api/@types";
import { executionTargets } from "./execution";

type Field = {
  key: string;
  label: string;
  help: string;
  choices?: [string, string][];
  multiple?: boolean;
};
const hold: Field = {
  key: "ha",
  label: "保留",
  help: "省略は保留なし。登録時・運用中の指定とは別の定義属性です。",
  choices: [
    ["y", "保留あり"],
    ["n", "保留なし"],
  ],
};
const agent: Field = {
  key: "ex",
  label: "ローカル実行先の対応付け名",
  help: "LocalAgentsに設定済みの名称だけ指定できます。現在のホスト・OSユーザーで実行し、リモート転送やユーザー切替は行いません。",
};
const jobFields: Field[] = [
  hold,
  {
    key: "jd",
    label: "終了判定",
    help: "省略は終了コード判定。ファイルによる判定は未対応です。",
    choices: [
      ["cod", "終了コードで判定"],
      ["nm", "正常終了とする"],
      ["ab", "異常終了とする"],
    ],
  },
  {
    key: "wth",
    label: "警告しきい値",
    help: "0〜2147483647、tho以下。省略時はthoと同じ値です。",
  },
  {
    key: "tho",
    label: "異常しきい値",
    help: "0〜2147483647。省略は0。終了コードがこの値を超えると異常です。",
  },
  {
    key: "etm",
    label: "実行打ち切り時間（分）",
    help: "1〜1440。省略時はこの属性による打ち切りを指定しません。",
  },
  agent,
];
const networkFields: Field[] = [
  hold,
  {
    key: "mp",
    label: "多重起動",
    help: "ルート世代の多重起動を許可します。省略は許可しない。",
    choices: [
      ["y", "許可する"],
      ["n", "許可しない"],
    ],
  },
  ...[
    ["sy", "開始遅延監視"],
    ["ey", "終了遅延監視"],
  ].map(([key, label]) => ({
    key,
    label,
    multiple: true,
    help: "1行に1ルール。例: 1,09:00 / 2,C30。ルール1〜144、時刻00:00〜47:59、M/U/Cの相対分1〜2879。M/Uはネストだけ。",
  })),
  {
    key: "fd",
    label: "終了遅延監視の所要時間（分）",
    help: "1〜2879。省略時は所要時間による監視を指定しません。",
  },
  agent,
];

export function ExecutionProperties({
  draft,
  current,
  onChange,
}: {
  draft: EditorDraft;
  current: string;
  onChange: (draft: EditorDraft) => void;
}) {
  const [selected, setSelected] = useState(current);
  const targets = useMemo(() => executionTargets(draft), [draft]);
  if (![5, 6, 7, 8].includes(draft.editorVersion ?? 1) || !draft.execution)
    return null;
  const target = targets.find((t) => t.id === selected) ?? targets[0];
  const entry = draft.execution.find((e) => e.id === target?.id);
  if (!target || !entry) return null;
  const fields = ["n", "rn"].includes(target.type)
    ? networkFields
    : ["j", "pj", "rj", "rp"].includes(target.type)
      ? jobFields
      : [agent];
  function change(attributes: Record<string, string[]>) {
    onChange({
      ...draft,
      execution: draft.execution!.map((e) =>
        e.id === entry!.id ? { ...e, attributes } : e,
      ),
    });
  }
  return (
    <details className="editor-timing" aria-label="実行属性の編集">
      <summary>実行属性</summary>
      <label>
        実行属性の対象
        <select
          aria-label="実行属性の対象"
          value={target.id}
          onChange={(e) => setSelected(e.target.value)}
        >
          {targets.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name} ({t.type})
            </option>
          ))}
        </select>
      </label>
      <p className="editor-muted">
        下書きの定義を編集します。検証・ダウンロードでは採用・登録・実行データを変更しません。
      </p>
      <div className="editor-timing-fields">
        {fields.map((field) => {
          const defined = Object.hasOwn(entry.attributes, field.key);
          const label = `${field.label} (${field.key})`;
          return (
            <div className="editor-timing-field" key={field.key}>
              <label className="editor-timing-toggle">
                <input
                  type="checkbox"
                  checked={defined}
                  onChange={(e) => {
                    const attributes = { ...entry.attributes };
                    if (e.target.checked) attributes[field.key] = [""];
                    else delete attributes[field.key];
                    change(attributes);
                  }}
                />
                {label}を指定
              </label>
              {defined ? (
                <label>
                  {label}
                  {field.choices ? (
                    <select
                      aria-label={label}
                      value={entry.attributes[field.key][0] ?? ""}
                      onChange={(e) =>
                        change({
                          ...entry.attributes,
                          [field.key]: [e.target.value],
                        })
                      }
                    >
                      <option value="">値を選択</option>
                      {field.choices.map(([value, text]) => (
                        <option key={value} value={value}>
                          {text} ({value})
                        </option>
                      ))}
                    </select>
                  ) : field.multiple ? (
                    <textarea
                      aria-label={label}
                      value={entry.attributes[field.key].join("\n")}
                      onChange={(e) =>
                        change({
                          ...entry.attributes,
                          [field.key]: e.target.value.split("\n"),
                        })
                      }
                    />
                  ) : (
                    <input
                      aria-label={label}
                      value={entry.attributes[field.key][0] ?? ""}
                      onChange={(e) =>
                        change({
                          ...entry.attributes,
                          [field.key]: [e.target.value],
                        })
                      }
                    />
                  )}
                </label>
              ) : (
                <span className="editor-muted">省略（既定値）</span>
              )}
              <small>{field.help}</small>
            </div>
          );
        })}
      </div>
    </details>
  );
}
