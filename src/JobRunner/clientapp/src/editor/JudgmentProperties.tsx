import type { EditorNode } from "../api/@types";

const conditions = [
  ["gt", "終了コード > 判定値"],
  ["ge", "終了コード ≥ 判定値"],
  ["lt", "終了コード < 判定値"],
  ["le", "終了コード ≤ 判定値"],
  ["eq", "終了コード = 判定値"],
  ["ne", "終了コード ≠ 判定値"],
  ["ri", "終了コードが範囲内"],
  ["ro", "終了コードが範囲外"],
  ["ef", "ファイルが存在する"],
  ["nf", "ファイルが存在しない"],
  ["vgt", "変数の数値 > 判定値"],
  ["vge", "変数の数値 ≥ 判定値"],
  ["vlt", "変数の数値 < 判定値"],
  ["vle", "変数の数値 ≤ 判定値"],
  ["veq", "変数の数値 = 判定値"],
  ["vne", "変数の数値 ≠ 判定値"],
  ["vri", "変数の数値が範囲内"],
  ["vro", "変数の数値が範囲外"],
  ["sce", "変数の文字列が一致"],
  ["spe", "変数の文字列が判定値を含む"],
  ["sne", "変数の文字列が不一致"],
  ["spn", "変数の文字列が判定値を含まない"],
  ["snn", "変数に値がある"],
  ["snl", "変数に値がない"],
];

export function JudgmentProperties({
  node,
  onChange,
}: {
  node: EditorNode;
  onChange: (node: EditorNode) => void;
}) {
  const value = (key: string) => node.attributes[key]?.[0] ?? "";
  const kind = value("ej") || "gt";
  const variable = kind.startsWith("v") || kind.startsWith("s");
  const file = kind === "ef" || kind === "nf";
  const range = ["ri", "ro", "vri", "vro"].includes(kind);
  const text = ["sce", "spe", "sne", "spn"].includes(kind);
  const presence = kind === "snn" || kind === "snl";
  const maximum = variable ? 2147483647 : 4294967295;
  function field(key: string, next: string, keepEmpty = false) {
    const attributes = { ...node.attributes };
    if (next === "" && !keepEmpty) delete attributes[key];
    else attributes[key] = [next];
    onChange({ ...node, attributes });
  }
  function number(key: string, label: string, required = false) {
    return (
      <label>
        {label}
        <input
          type="number"
          min={0}
          max={maximum}
          step={1}
          value={value(key)}
          placeholder={required ? "必須" : "省略時は0"}
          onChange={(e) => field(key, e.target.value)}
        />
      </label>
    );
  }
  return (
    <>
      <label>
        判定方式
        <select
          value={kind}
          onChange={(e) => {
            const attributes: EditorNode["attributes"] = {
              ...node.attributes,
              ej: [e.target.value],
            };
            // ファイル判定と併記できないejcだけは、明示確認して取り除きます。
            if (["ef", "nf"].includes(e.target.value) && "ejc" in attributes) {
              if (
                !window.confirm(
                  "ファイル判定では終了コードの判定値（ejc）を指定できません。判定値を削除して方式を変更しますか？",
                )
              )
                return;
              delete attributes.ejc;
            }
            if (
              ["sce", "spe", "sne", "spn"].includes(e.target.value) &&
              !("ejt" in attributes)
            )
              attributes.ejt = [""];
            onChange({ ...node, attributes });
          }}
        >
          {conditions.map(([key, label]) => (
            <option key={key} value={key}>
              {label}
            </option>
          ))}
        </select>
      </label>
      {variable && (
        <>
          <label>
            判定変数名
            <input
              value={value("ejv")}
              placeholder="?AJS2RESULT?"
              onChange={(e) => field("ejv", e.target.value)}
            />
          </label>
          <small>
            先行ジョブがJOBRUNNER_OUTPUT_FILEへ保存した変数を参照します。JP1のAJSVAR方式には対応していません。
          </small>
        </>
      )}
      {file ? (
        <label>
          判定ファイル
          <input
            value={value("ejf")}
            placeholder="実行ホストの絶対パス"
            onChange={(e) => field("ejf", e.target.value)}
          />
        </label>
      ) : text ? (
        <label>
          文字列の判定値
          <textarea
            value={value("ejt")}
            onChange={(e) => field("ejt", e.target.value, true)}
          />
        </label>
      ) : (
        !presence &&
        (range ? (
          <>
            {number(variable ? "ejs" : "ejl", "判定値の下限", true)}
            <label>
              下限の境界
              <select
                value={value("ejm")}
                onChange={(e) => field("ejm", e.target.value)}
              >
                <option value="">省略（以上）</option>
                <option value="ge">以上</option>
                <option value="gt">より大きい</option>
              </select>
            </label>
            {number(variable ? "ejg" : "ejh", "判定値の上限", true)}
            <label>
              上限の境界
              <select
                value={value("eju")}
                onChange={(e) => field("eju", e.target.value)}
              >
                <option value="">省略（以下）</option>
                <option value="le">以下</option>
                <option value="lt">より小さい</option>
              </select>
            </label>
          </>
        ) : (
          number(variable ? "eji" : "ejc", "判定値")
        ))
      )}
      <label>
        判定ジョブの保留
        <select
          value={value("ha")}
          onChange={(e) => field("ha", e.target.value)}
        >
          <option value="">省略（保留なし）</option>
          <option value="n">保留なし</option>
          <option value="y">保留あり</option>
        </select>
      </label>
      <small>
        先行はコマンドジョブ1つ、条件成立の接続先は最大1つです。方式を変えても、使用しない既存の条件属性は原文に保持します。
      </small>
    </>
  );
}
