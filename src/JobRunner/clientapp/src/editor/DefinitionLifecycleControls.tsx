import { useRef, useState } from "react";
import useAspidaSWR from "@aspida/swr";
import type {
  EditorDocument,
  EditorDraft,
  Operation,
  PublicationPreview,
  SharedEditorDraft,
} from "../api/@types";
import { client, errorMessage } from "../client";

async function readShared(): Promise<SharedEditorDraft | undefined> {
  const response = await fetch("/api/jobrunner/ui/definition-editor/shared");
  if (response.status === 204) return undefined;
  if (!response.ok)
    throw Object.assign(new Error("共有下書きを取得できません"), { response });
  return response.json();
}
export function DefinitionLifecycleControls({
  base,
  draft,
  disabled,
  onRestore,
}: {
  base?: EditorDocument;
  draft?: EditorDraft;
  disabled: boolean;
  onRestore: (saved: SharedEditorDraft) => Promise<void>;
}) {
  const runtime = useAspidaSWR(client.ui.runtime, { refreshInterval: 5000 });
  const [sharedRevision, setSharedRevision] = useState<number>();
  const [baseRevision, setBaseRevision] = useState<number>();
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [preview, setPreview] = useState<{
    draft: EditorDraft;
    value: PublicationPreview;
  }>();
  const latest = useRef(draft);
  latest.current = draft;
  const [request, setRequest] = useState<Operation | undefined>(() => {
    try {
      return (
        JSON.parse(sessionStorage.getItem("jobrunner-publication") || "null") ??
        undefined
      );
    } catch {
      return undefined;
    }
  });
  const result = useAspidaSWR(client.operations._id(request?.id || "_"), {
    enabled: !!request,
    refreshInterval: (value) =>
      !value || value.status === "Pending" ? 1000 : 0,
  });
  const pending =
    !!request && !["Completed", "Rejected"].includes(result.data?.status || "");
  async function save() {
    if (!base || !draft) return;
    setBusy(true);
    setMessage("");
    try {
      let expected = sharedRevision;
      if (expected === undefined) {
        const stored = await readShared();
        if (latest.current !== draft) return;
        if (
          stored &&
          !window.confirm(
            `Storageの共有下書き（版${stored.revision}、基点${stored.draft.definitionHash.slice(0, 12)}）を、現在の編集で置き換えますか？`,
          )
        )
          return;
        expected = stored?.revision ?? 0;
      }
      const saved = await client.ui.definition_editor.shared.$put({
        body: {
          revision: expected,
          baseRevision: baseRevision ?? runtime.data?.active?.revision ?? 0,
          base,
          draft,
        },
      });
      setSharedRevision(saved.revision);
      setMessage(
        latest.current === draft
          ? `Storageへ下書き版${saved.revision}を保存しました。運用には反映していません。`
          : `操作時点の下書きをStorage版${saved.revision}へ保存しました。続けた編集は再度保存してください。`,
      );
    } catch (error) {
      setMessage(await errorMessage(error));
    } finally {
      setBusy(false);
    }
  }
  async function load() {
    if (
      draft &&
      !window.confirm(
        "現在の下書きをStorageの共有下書きで置き換えますか？ 必要な編集は先にダウンロードしてください。",
      )
    )
      return;
    const started = latest.current;
    setBusy(true);
    setMessage("");
    try {
      const saved = await readShared();
      if (started !== latest.current)
        throw new Error("読込中に編集されたため、現在の編集を保持しました。");
      if (!saved) {
        setSharedRevision(0);
        setMessage(
          "Storageの共有下書きはまだありません。現在の編集を保存できます。",
        );
        return;
      }
      await onRestore(saved);
      setSharedRevision(saved.revision);
      setBaseRevision(saved.baseRevision);
      setMessage(`Storageの下書き版${saved.revision}を読み込みました。`);
    } catch (error) {
      setMessage(await errorMessage(error));
    } finally {
      setBusy(false);
    }
  }
  async function confirmPreview() {
    if (!draft) return;
    setBusy(true);
    setMessage("");
    setPreview(undefined);
    try {
      const value = await client.ui.definition_editor.publication_preview.$post(
        { body: draft },
      );
      if (latest.current === draft) setPreview({ draft, value });
    } catch (error) {
      setMessage(await errorMessage(error));
    } finally {
      setBusy(false);
    }
  }
  async function publish(value: Operation) {
    sessionStorage.setItem("jobrunner-publication", JSON.stringify(value));
    setRequest(value);
    setBusy(true);
    setMessage("");
    try {
      await client.ui.definition_editor.publish.$post({ body: value });
      await result.mutate();
      setPreview(undefined);
    } catch (error) {
      setMessage(await errorMessage(error));
    } finally {
      setBusy(false);
    }
  }
  const shown = preview && preview.draft === draft ? preview.value : undefined;
  return (
    <section className="editor-lifecycle" aria-label="下書き保存と運用反映">
      <p>
        ブラウザーの自動保存・Storageの共有保存・運用反映は別の操作です。アップロードは定義一式を置き換えます。
      </p>
      <div className="editor-network-fields">
        <button
          disabled={disabled || busy || !draft}
          onClick={() => void save()}
        >
          Storageへ下書きを保存
        </button>
        <button disabled={disabled || busy} onClick={() => void load()}>
          Storageの下書きを読み込む
        </button>
        <button
          disabled={disabled || busy || pending || !draft}
          onClick={() => void confirmPreview()}
        >
          運用反映の差分を確認
        </button>
      </div>
      {baseRevision !== undefined &&
        baseRevision !== (runtime.data?.active?.revision ?? 0) && (
          <p role="status">
            下書きの基点以後に運用版が変わっています。反映前に一式の差分を確認してください。
          </p>
        )}
      {message && <p role="status">{message}</p>}
      {pending && (
        <p role="status">
          反映の受付・適用結果を確認中… {request?.id}
          <button onClick={() => request && void publish(request)}>
            同じ操作IDで再照会・再送
          </button>
        </p>
      )}
      {result.data?.status === "Completed" && (
        <p role="status">
          運用版{result.data.result}
          への反映が完了しました。全体の停止状態は維持されています。
        </p>
      )}
      {result.data?.status === "Rejected" && (
        <p role="alert">反映を拒否しました: {result.data.result}</p>
      )}
      {shown && (
        <div
          className="editor-publication-preview"
          role="dialog"
          aria-label="運用反映の確認"
        >
          <h3>運用版{shown.expectedRevision}への変更</h3>
          <p>
            ルート追加 {shown.addedRoots.length}件 / 削除{" "}
            {shown.removedRoots.length}件。通常予定{" "}
            {shown.generations.filter((g) => !g.reason).length}
            件を再計算します。
          </p>
          <p>
            新しい定義一式で置き換えます。削除したルートの補充は停止し、開始済み・個別操作済みの実行回は旧定義で保持します。
          </p>
          <ul>
            {shown.addedRoots.map((p) => (
              <li key={`add:${p}`}>追加: {p}</li>
            ))}
            {shown.removedRoots.map((p) => (
              <li key={`remove:${p}`}>削除: {p}</li>
            ))}
          </ul>
          <details>
            <summary>実行回への影響（{shown.generations.length}件）</summary>
            <ul>
              {shown.generations.map((g) => (
                <li key={g.id}>
                  {g.path} / {g.due}: {g.reason || "通常予定を再計算"}
                </li>
              ))}
            </ul>
          </details>
          <details>
            <summary>反映前のファイル一式（削除対象を含む）</summary>
            {shown.previousSources.map((s) => (
              <details key={s.fileName}>
                <summary>
                  {shown.sources.some(
                    (next) =>
                      next.fileName === s.fileName &&
                      next.targetPath === s.targetPath,
                  )
                    ? "反映前"
                    : "削除"}
                  : {s.fileName} → {s.targetPath}
                </summary>
                <pre>{s.text}</pre>
              </details>
            ))}
          </details>
          <details>
            <summary>反映後のファイル一式</summary>
            {shown.sources.map((s) => (
              <details key={s.fileName}>
                <summary>
                  {s.changed ? "変更" : "同一"}: {s.fileName} → {s.targetPath}
                </summary>
                <pre>{s.text}</pre>
              </details>
            ))}
          </details>
          <button
            className="primary"
            disabled={busy || pending}
            onClick={() =>
              void publish({
                id: crypto.randomUUID(),
                kind: "Activate",
                target: shown.definitionHash,
                expectedDefinitionRevision: shown.expectedRevision,
                expectedImpactHash: shown.impactHash,
              } as Operation)
            }
          >
            確認して運用に反映
          </button>
          <button onClick={() => setPreview(undefined)}>閉じる</button>
        </div>
      )}
    </section>
  );
}
