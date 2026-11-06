import { useState } from "react";
import type { EditorDocument, SourceEvidence } from "../api/@types";
import { client } from "../client";

type Input = {
  file: File;
  encoding: string;
  targetPath: string;
  blobPath: string;
};
const mib = 1024 * 1024;
async function base64(file: File) {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let text = "";
  for (let i = 0; i < bytes.length; i += 8192)
    text += String.fromCharCode(...bytes.subarray(i, i + 8192));
  return btoa(text);
}
export function LocalDefinitionInput({
  disabled,
  onImport,
}: {
  disabled: boolean;
  onImport: (load: () => Promise<EditorDocument>) => Promise<void>;
}) {
  const [inputs, setInputs] = useState<Input[]>([]);
  const zip =
    inputs.length === 1 && inputs[0].file.name.toLowerCase().endsWith(".zip");
  function change(index: number, patch: Partial<Input>) {
    setInputs((items) =>
      items.map((item, i) => (i === index ? { ...item, ...patch } : item)),
    );
  }
  async function load() {
    if (
      !inputs.length ||
      inputs.length > 32 ||
      inputs.some(
        (i) => i.file.size === 0 || i.file.size > (zip ? 8 : 2) * mib,
      ) ||
      inputs.reduce((n, i) => n + i.file.size, 0) > 8 * mib
    )
      throw new Error(
        "入力は1〜32ファイル、各2MiB・合計8MiBまでです。ZIPは単独8MiBまでです。",
      );
    if (zip)
      return client.ui.definition_editor.local.$post({
        body: { zipBase64: await base64(inputs[0].file) },
      });
    if (inputs.some((i) => i.file.name.toLowerCase().endsWith(".zip")))
      throw new Error("ZIPとUnitファイルは一緒に読み込めません。");
    const sources: SourceEvidence[] = await Promise.all(
      inputs.map(async (i) => ({
        mapping: {
          kind: "Unit",
          encoding: i.encoding,
          targetPath: i.targetPath,
          blobPath: i.blobPath,
        },
        base64: await base64(i.file),
      })),
    );
    return client.ui.definition_editor.local.$post({ body: { sources } });
  }
  return (
    <details className="editor-local-input">
      <summary>ローカル定義を開く</summary>
      <p>
        Unit定義をまとめて選択するか、JobRunnerが出力したZIPを1つ選択してください。原文は検証のためサーバーへ送信し、下書きはこのブラウザーに保存します。採用・登録は行いません。
      </p>
      <fieldset disabled={disabled}>
        <label>
          定義ファイル
          <input
            type="file"
            multiple
            aria-label="定義ファイル"
            onChange={(e) =>
              setInputs(
                Array.from(e.target.files ?? []).map((file) => ({
                  file,
                  encoding: "utf-8",
                  targetPath: "/",
                  blobPath: file.name,
                })),
              )
            }
          />
        </label>
        {zip ? (
          <p>ZIPの対応表から文字コード・元ファイル・配置先を読み込みます。</p>
        ) : (
          inputs.map((input, index) => (
            <fieldset key={index} className="editor-network-fields">
              <legend>
                {input.file.name}（{input.file.size} bytes）
              </legend>
              <label>
                文字コード
                <select
                  aria-label={`文字コード ${index + 1}`}
                  value={input.encoding}
                  onChange={(e) => change(index, { encoding: e.target.value })}
                >
                  <option value="utf-8">UTF-8</option>
                  <option value="utf-16">UTF-16LE</option>
                  <option value="utf-16BE">UTF-16BE</option>
                  <option value="shift_jis">Shift_JIS</option>
                </select>
              </label>
              <label>
                元ファイルの対応
                <input
                  aria-label={`元ファイルの対応 ${index + 1}`}
                  value={input.blobPath}
                  onChange={(e) => change(index, { blobPath: e.target.value })}
                />
              </label>
              <label>
                論理配置先
                <input
                  aria-label={`論理配置先 ${index + 1}`}
                  value={input.targetPath}
                  onChange={(e) =>
                    change(index, { targetPath: e.target.value })
                  }
                />
              </label>
            </fieldset>
          ))
        )}
        <p className="editor-muted">
          文字コードは自動推測しません。BOMがある場合も一致する文字コードを選択してください。最大32ファイル・各2MiB・合計8MiB。ZIP展開後も同じ上限です。
        </p>
        <button disabled={!inputs.length} onClick={() => void onImport(load)}>
          ローカル定義を読み込む
        </button>
      </fieldset>
    </details>
  );
}
