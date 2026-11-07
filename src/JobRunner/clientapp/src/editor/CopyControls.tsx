import { useCallback, useEffect, useRef, useState } from "react";
import type {
  EditorDocument,
  EditorDraft,
  EditorPasteResult,
} from "../api/@types";
import { groupPath } from "./group-paths";
import { client, errorMessage } from "../client";

export function CopyControls({
  draft,
  current,
  selected,
  base,
  disabled,
  onPaste,
}: {
  draft: EditorDraft;
  current: string;
  selected: string[];
  base: EditorDocument;
  disabled: boolean;
  onPaste: (draft: EditorDraft, path: string) => void;
}) {
  const [clipboard, setClipboard] = useState<{
    draft: EditorDraft;
    ids: string[];
    roots: boolean;
  }>();
  const [container, setContainer] = useState(0);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [candidate, setCandidate] = useState<{
    draft: EditorDraft;
    result: EditorPasteResult;
  }>();
  const latest = useRef(draft);
  latest.current = draft;
  const copy = useCallback(
    (whole = false) => {
      const ids = whole ? [current] : selected;
      if (!ids.length || disabled) return;
      setClipboard({
        draft: structuredClone(draft),
        ids,
        roots: !!draft.roots?.some((r) => r.node.id === ids[0]),
      });
      setMessage(
        `${ids.length}件をコピーしました。子孫・内部接続・原文を貼り付け候補へ含めます。`,
      );
    },
    [current, selected, disabled, draft],
  );
  const paste = useCallback(async () => {
    if (!clipboard || disabled || busy) return;
    if (clipboard.draft.definitionHash !== draft.definitionHash) {
      setMessage("別の下書きへは貼り付けできません。コピーし直してください。");
      return;
    }
    setBusy(true);
    setMessage("");
    setCandidate(undefined);
    try {
      const destination = base.containers?.[container];
      const result = await client.ui.definition_editor.paste.$post({
        body: {
          draft,
          copiedDraft: clipboard.draft,
          ids: clipboard.ids,
          targetNetworkId: clipboard.roots ? null : current,
          sourceIndex: clipboard.roots
            ? (destination?.sourceIndex ?? null)
            : null,
          parentPath:
            clipboard.roots && destination
              ? groupPath(draft, destination.parentPath)
              : null,
          targetFile:
            clipboard.roots && destination
              ? (base.sources[destination.sourceIndex]?.fileName ?? null)
              : null,
        },
      });
      if (latest.current === draft) setCandidate({ draft, result });
      else
        setMessage(
          "貼り付け候補の作成中に編集されたため、現在の内容を保持しました。",
        );
    } catch (error) {
      setMessage(await errorMessage(error));
    } finally {
      setBusy(false);
    }
  }, [
    clipboard,
    disabled,
    busy,
    draft,
    base.containers,
    base.sources,
    container,
    current,
  ]);
  useEffect(() => {
    function keydown(event: KeyboardEvent) {
      const target = event.target;
      if (
        !(event.ctrlKey || event.metaKey) ||
        (target instanceof Element &&
          target.closest("input,textarea,select,[contenteditable=true]")) ||
        window.getSelection()?.toString()
      )
        return;
      if (event.key.toLowerCase() === "c" && selected.length) {
        event.preventDefault();
        copy();
      }
      if (event.key.toLowerCase() === "v" && clipboard) {
        event.preventDefault();
        void paste();
      }
    }
    document.addEventListener("keydown", keydown);
    return () => document.removeEventListener("keydown", keydown);
  }, [copy, paste, selected.length, clipboard]);
  const shown = candidate?.draft === draft ? candidate.result : undefined;
  return (
    <section className="editor-copy" aria-label="ジョブとネットの複製">
      <button disabled={disabled || !selected.length} onClick={() => copy()}>
        選択をコピー（{selected.length}件）
      </button>
      <button disabled={disabled || !current} onClick={() => copy(true)}>
        このネットをコピー
      </button>
      {clipboard?.roots && (
        <label>
          ルートの貼り付け先{" "}
          <select
            aria-label="複製ルートの配置先"
            value={container}
            onChange={(e) => setContainer(Number(e.target.value))}
          >
            {base.containers?.map((c, i) => (
              <option key={i} value={i}>
                {base.sources[c.sourceIndex]?.fileName}: {c.parentPath}
              </option>
            ))}
          </select>
        </label>
      )}
      <button
        disabled={disabled || busy || !clipboard}
        onClick={() => void paste()}
      >
        {busy ? "複製を検証中…" : "貼り付け候補を確認"}
      </button>
      <small>
        Ctrl / ⌘ + クリックで複数選択。Ctrl / ⌘ +
        C、Vに対応します。同一下書き内でのみ使用できます。
      </small>
      {message && <p role="status">{message}</p>}
      {shown && (
        <div role="dialog" aria-label="貼り付けの確認">
          <p>複製先: {shown.clonedIds.join("、")}</p>
          <ul>
            {shown.warnings.map((w) => (
              <li key={w}>{w}</li>
            ))}
          </ul>
          <button
            onClick={() => {
              onPaste(shown.document.draft, shown.targetPath);
              setCandidate(undefined);
              setMessage("下書きへ貼り付けました。一度のundoで戻せます。");
            }}
          >
            確認して貼り付け
          </button>
          <button onClick={() => setCandidate(undefined)}>中止</button>
        </div>
      )}
    </section>
  );
}
