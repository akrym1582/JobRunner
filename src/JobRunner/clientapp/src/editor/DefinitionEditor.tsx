import { useCallback, useEffect, useRef, useState } from "react";
import useAspidaSWR from "@aspida/swr";
import {
  CheckCircle2,
  Download,
  FileCode2,
  GitBranch,
  Link2,
  Plus,
  Redo2,
  RotateCcw,
  Undo2,
  X,
} from "lucide-react";
import type {
  EditorDocument,
  EditorDraft,
  EditorNetwork,
  EditorPreview,
} from "../api/@types";
import { client, errorMessage } from "../client";
import { EditorGraph } from "./EditorGraph";
import { EditorProperties } from "./EditorProperties";
import { NetworkProperties } from "./NetworkProperties";
import { networkNames, removeHierarchyNode, subtreeIds } from "./hierarchy";
import {
  change,
  canEdit,
  isNetwork,
  newJob,
  redo,
  replaceNetwork,
  replaceNode,
  undo,
  upgradeDraft,
  type History,
} from "./model";
import {
  DraftConflictError,
  readDraft,
  writeDraft,
  type SavedDraft,
} from "./storage";
import "./editor.css";

async function upgradeSaved(saved: SavedDraft): Promise<SavedDraft> {
  if (saved.draft.editorVersion === 3) return saved;
  const base = await client.ui.definition_editor.$get({
    query: { definitionHash: saved.draft.definitionHash },
  });
  return { ...saved, base, draft: upgradeDraft(saved.draft, base.draft) };
}

export function DefinitionEditor({
  activeHash,
  path,
  onNavigate,
}: {
  activeHash?: string;
  path: string;
  onNavigate: (path: string) => void;
}) {
  const source = useAspidaSWR(client.ui.definition_editor, {
    revalidateOnFocus: false,
    revalidateOnReconnect: false,
  });
  const [base, setBase] = useState<EditorDocument>();
  const [history, setHistory] = useState<History>();
  const [loaded, setLoaded] = useState(false);
  const [saving, setSaving] = useState("");
  const [saveError, setSaveError] = useState("");
  const [saveAllowed, setSaveAllowed] = useState(true);
  const [conflicted, setConflicted] = useState(false);
  const [selected, setSelected] = useState("");
  const [kind, setKind] = useState("j");
  const [connecting, setConnecting] = useState(false);
  const [connectFrom, setConnectFrom] = useState("");
  const [conditional, setConditional] = useState(false);
  const [preview, setPreview] = useState<EditorPreview>();
  const [showPreview, setShowPreview] = useState(false);
  const [previewFile, setPreviewFile] = useState(0);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [loadError, setLoadError] = useState("");
  const revision = useRef(0);
  const savedRevision = useRef<string | null>(null);
  const savedContent = useRef("");
  const saveQueue = useRef<Promise<void>>(Promise.resolve());
  const conflict = useRef(false);
  const draft = history?.present;
  const network =
    draft?.networks.find((n) => n.path === path) ??
    draft?.networks[0] ??
    (draft ? { path: "", nodes: [], edges: [] } : undefined);
  const names = draft ? networkNames(draft) : new Map<string, string>();
  const node = network?.nodes.find((n) => n.id === selected);
  const changed =
    !!draft && JSON.stringify(base?.draft) !== JSON.stringify(draft);

  const persist = useCallback(
    (value: SavedDraft, compareEvenIfUnchanged = false) => {
      const content = JSON.stringify({
        version: 1,
        base: value.base,
        draft: value.draft,
      });
      // 同じタブの連続入力は順番に保存し、成功した版を次の比較に使います。
      const pending = saveQueue.current.then(async () => {
        if (conflict.current) throw new DraftConflictError();
        if (!compareEvenIfUnchanged && content === savedContent.current) return;
        savedRevision.current = await writeDraft(value, savedRevision.current);
        savedContent.current = content;
      });
      saveQueue.current = pending.catch((error) => {
        if (error instanceof DraftConflictError) {
          conflict.current = true;
          setConflicted(true);
        }
        setSaving("");
        setSaveError(`自動保存できません: ${String(error)}`);
      });
      return pending;
    },
    [],
  );

  useEffect(() => {
    let current = true;
    void readDraft()
      .then(async (saved) => {
        if (!current) return;
        if (saved) {
          savedRevision.current = saved.revision ?? null;
          savedContent.current = JSON.stringify({
            version: 1,
            base: saved.base,
            draft: saved.draft,
          });
          let restored = saved;
          try {
            restored = await upgradeSaved(saved);
          } catch (error) {
            if (!current) return;
            setSaveAllowed(false);
            setSaveError(
              `旧下書きの編集情報を読み込めません。編集内容は保持しています: ${String(error)}`,
            );
          }
          if (!current) return;
          setBase(restored.base);
          setHistory({ past: [], present: restored.draft, future: [] });
        }
        setLoaded(true);
      })
      .catch((e) => {
        if (!current) return;
        setSaveError(`下書きを復元できません: ${String(e)}`);
        setSaveAllowed(false);
        setLoaded(true);
      });
    return () => {
      current = false;
    };
  }, []);
  useEffect(() => {
    if (loaded && !base && source.data) {
      setBase(source.data);
      setHistory({ past: [], present: source.data.draft, future: [] });
    }
  }, [loaded, base, source.data]);
  useEffect(() => {
    let current = true;
    if (source.error)
      void errorMessage(source.error).then((text) => {
        if (current) setLoadError(text);
      });
    else setLoadError("");
    return () => {
      current = false;
    };
  }, [source.error]);
  useEffect(() => {
    if (!base || !draft || !saveAllowed) return;
    let current = true;
    setSaving("下書きを保存中…");
    void persist({ version: 1, base, draft })
      .then(() => {
        if (current) {
          setSaving("ブラウザーに保存済み");
          setSaveError("");
        }
      })
      .catch(() => {});
    return () => {
      current = false;
    };
  }, [base, draft, saveAllowed, persist]);
  useEffect(() => {
    setSelected("");
    setConnectFrom("");
  }, [network?.path]);

  function update(next: EditorDraft) {
    revision.current++;
    setPreview(undefined);
    setMessage("");
    setHistory((h) => (h ? change(h, next) : h));
  }
  function edit(next: EditorNetwork) {
    if (draft) update(replaceNetwork(draft, next));
  }
  function travel(direction: "undo" | "redo") {
    revision.current++;
    setPreview(undefined);
    setMessage("");
    setConnectFrom("");
    setHistory((h) => (h ? (direction === "undo" ? undo(h) : redo(h)) : h));
  }
  function select(id: string) {
    if (!network) return;
    setSelected(id);
    if (!connecting) return;
    if (!connectFrom) {
      setConnectFrom(id);
      return;
    }
    if (connectFrom === id) {
      setMessage("別の接続先を選択してください。");
      return;
    }
    if (network.edges.some((e) => e.from === connectFrom && e.to === id)) {
      setMessage("この接続は既に存在します。");
      return;
    }
    edit({
      ...network,
      edges: [...network.edges, { from: connectFrom, to: id, conditional }],
    });
    setConnectFrom("");
    setConnecting(false);
  }
  async function validate() {
    if (!draft) return;
    const current = revision.current;
    setBusy(true);
    setMessage("");
    try {
      const result = await client.ui.definition_editor.preview.$post({
        body: draft,
      });
      if (current === revision.current) {
        setPreview(result);
        setShowPreview(true);
        setPreviewFile(0);
      }
    } catch (e) {
      const detail = await errorMessage(e);
      if (current === revision.current) setMessage(detail);
    } finally {
      setBusy(false);
    }
  }
  async function download() {
    if (!draft || !base) return;
    const current = revision.current;
    setBusy(true);
    setMessage("");
    try {
      const blob = await client.ui.definition_editor.export.$post({
        body: draft,
      });
      if (current !== revision.current) {
        setMessage(
          "編集内容が変わりました。再度検証してダウンロードしてください。",
        );
        return;
      }
      const url = URL.createObjectURL(blob),
        link = document.createElement("a");
      link.href = url;
      link.download =
        base.sources.length === 1
          ? base.sources[0].fileName.split("/").at(-1)!
          : "jobrunner-definitions.zip";
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
      setMessage("定義をダウンロードしました。");
    } catch (e) {
      const detail = await errorMessage(e);
      if (current === revision.current) setMessage(detail);
    } finally {
      setBusy(false);
    }
  }
  async function discard() {
    if (
      !window.confirm(
        "保存した下書きを破棄し、現在の採用定義からやり直しますか？",
      )
    )
      return;
    const started = revision.current;
    setBusy(true);
    try {
      if (!saveAllowed)
        throw new Error(
          "保存領域を確認できないため破棄できません。保存済み下書きを読み直してください。",
        );
      const current = await source.mutate();
      if (!current) throw new Error("現在の定義を取得できません。");
      if (started !== revision.current) {
        setMessage("再取得中に編集されたため、下書きの破棄を中止しました。");
        return;
      }
      // 未編集のタブからの破棄でも、別タブの保存版との比較は省略しません。
      await persist({ version: 1, base: current, draft: current.draft }, true);
      if (started !== revision.current) return;
      revision.current++;
      setBase(current);
      setHistory({ past: [], present: current.draft, future: [] });
      setPreview(undefined);
      setShowPreview(false);
      setSaveAllowed(true);
      setSelected("");
    } catch (e) {
      setMessage(await errorMessage(e));
    } finally {
      setBusy(false);
    }
  }
  async function reloadSaved() {
    if (
      !window.confirm(
        "保存済み下書きでこのタブの編集を置き換えますか？保存済み下書きがない場合は、現在の編集を保存します。必要な編集は先にダウンロードしてください。",
      )
    )
      return;
    const started = revision.current;
    setBusy(true);
    try {
      await saveQueue.current;
      const stored = await readDraft();
      const saved = stored ? await upgradeSaved(stored) : undefined;
      if (started !== revision.current) {
        setMessage("読み直し中に編集されたため、現在の編集を保持しました。");
        return;
      }
      if (!saved) {
        if (!base || !draft)
          throw new Error("編集する定義を取得できていません。");
        // 空の確認後に別タブが保存する可能性もあるため、復旧時も版を比較します。
        savedRevision.current = null;
        savedContent.current = "";
        conflict.current = false;
        setConflicted(false);
        await persist({ version: 1, base, draft });
        setSaveAllowed(true);
        setSaveError("");
        setSaving(
          started === revision.current
            ? "ブラウザーに保存済み"
            : "下書きを保存中…",
        );
        setMessage(
          "保存済み下書きがないため、現在の編集の保存を再開しました。",
        );
        return;
      }
      savedRevision.current = saved.revision ?? null;
      savedContent.current = JSON.stringify({
        version: 1,
        base: stored!.base,
        draft: stored!.draft,
      });
      conflict.current = false;
      setConflicted(false);
      revision.current++;
      setBase(saved.base);
      setHistory({ past: [], present: saved.draft, future: [] });
      setPreview(undefined);
      setShowPreview(false);
      setSelected("");
      setSaveError("");
      setSaveAllowed(true);
      setMessage("");
    } catch (e) {
      setMessage(await errorMessage(e));
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="definition-editor">
      <div className="page-heading">
        <div>
          <span className="eyebrow">DEFINITION WORKSPACE</span>
          <h2>
            定義編集 <span className="outline-pill">下書き</span>
          </h2>
          <p>ジョブと接続を編集して、JP1形式でダウンロード</p>
        </div>
        <span
          className={`editor-save ${saveError ? "error" : ""}`}
          role="status"
        >
          {saveError ? "自動保存エラー" : saving}
        </span>
      </div>
      {loadError && (
        <div className="alert" role="alert">
          {loadError}
          <button onClick={() => void source.mutate()}>定義を再取得</button>
        </div>
      )}
      {saveError && (
        <div className="alert" role="alert">
          {saveError}
          {conflicted || !saveAllowed ? (
            <button disabled={busy} onClick={() => void reloadSaved()}>
              保存済み下書きを読み直す
            </button>
          ) : (
            <button
              onClick={() => {
                if (base && draft) {
                  void persist({ version: 1, base, draft })
                    .then(() => {
                      setSaveAllowed(true);
                      setSaveError("");
                      setSaving("ブラウザーに保存済み");
                    })
                    .catch(() => {});
                }
              }}
            >
              保存を再試行
            </button>
          )}
        </div>
      )}
      {draft && activeHash && draft.definitionHash !== activeHash && (
        <div className="notice" role="status">
          採用定義が更新されています。この下書きは開始時の版{" "}
          {draft.definitionHash.slice(0, 12)} に固定しています。
        </div>
      )}
      <div className="editor-scope-note">
        <FileCode2 size={16} />
        この下書きは実行中の定義や予定に反映されません。ダウンロード後、既存の取込・採用手順で適用します。
      </div>
      {message && (
        <div className="notice" role="status">
          {message}
        </div>
      )}
      {draft && network && history ? (
        <>
          {base && (
            <NetworkProperties
              base={base}
              draft={draft}
              current={network.path}
              onChange={update}
              onNavigate={onNavigate}
            />
          )}
          <div className="editor-toolbar">
            <div>
              <button
                aria-label="元に戻す"
                disabled={!history.past.length}
                onClick={() => travel("undo")}
              >
                <Undo2 size={17} />
              </button>
              <button
                aria-label="やり直す"
                disabled={!history.future.length}
                onClick={() => travel("redo")}
              >
                <Redo2 size={17} />
              </button>
              <span className="editor-toolbar-separator" />
              <select
                aria-label="追加するジョブ種別"
                value={kind}
                disabled={!network.path}
                onChange={(e) => setKind(e.target.value)}
              >
                <option value="j">UNIXジョブ</option>
                <option value="pj">PCジョブ</option>
                <option value="rj">リカバリー UNIX</option>
                <option value="rp">リカバリー PC</option>
                <option value="jdj" disabled={(draft.editorVersion ?? 1) < 2}>
                  判定ジョブ
                </option>
                <option value="rjdj" disabled={(draft.editorVersion ?? 1) < 2}>
                  リカバリー判定
                </option>
                <option value="n" disabled={draft.editorVersion !== 3}>
                  子ネット
                </option>
                <option value="rn" disabled={draft.editorVersion !== 3}>
                  リカバリーネット
                </option>
              </select>
              <button
                disabled={!network.path}
                onClick={() => {
                  try {
                    if (!network.path) return;
                    if (!canEdit(kind, draft.editorVersion ?? 1))
                      throw new Error(
                        "編集情報を読み直してから追加してください。",
                      );
                    const added = newJob(network, kind);
                    const next = replaceNetwork(draft, {
                      ...network,
                      nodes: [...network.nodes, added],
                    });
                    if (isNetwork(added.type))
                      next.networks = [
                        ...next.networks,
                        { path: added.id, nodes: [], edges: [] },
                      ];
                    update(next);
                    setSelected(added.id);
                  } catch (e) {
                    setMessage(String(e));
                  }
                }}
              >
                <Plus size={16} />
                {isNetwork(kind) ? "子ネット追加" : "ジョブ追加"}
              </button>
              <button
                disabled={!network.path}
                aria-pressed={connecting}
                onClick={() => {
                  setConnecting(!connecting);
                  setConnectFrom("");
                }}
              >
                <Link2 size={16} />
                接続
              </button>
              {connecting && (
                <select
                  aria-label="接続種別"
                  value={conditional ? "condition" : "normal"}
                  onChange={(e) =>
                    setConditional(e.target.value === "condition")
                  }
                >
                  <option value="normal">通常順序</option>
                  <option value="condition">条件成立</option>
                </select>
              )}
            </div>
            <div>
              <button disabled={busy} onClick={() => void validate()}>
                <CheckCircle2 size={16} />
                {busy ? "処理中…" : "検証・プレビュー"}
              </button>
              <button
                className="primary"
                disabled={busy || !preview?.valid}
                onClick={() => void download()}
              >
                <Download size={16} />
                ダウンロード
              </button>
            </div>
          </div>
          <div className="editor-network-bar">
            <GitBranch size={16} />
            <label>
              編集ネット
              <select
                aria-label="編集ネット"
                value={network.path}
                onChange={(e) => onNavigate(e.target.value)}
              >
                {draft.networks.map((n) => (
                  <option key={n.path} value={n.path}>
                    {names.get(n.path) ?? n.path}
                  </option>
                ))}
              </select>
            </label>
            <span>{changed ? "変更あり" : "変更なし"}</span>
            {connecting && (
              <strong>
                {connectFrom
                  ? `${network.nodes.find((n) => n.id === connectFrom)?.name} → 接続先を選択`
                  : "接続元を選択"}
              </strong>
            )}
          </div>
          {!network.path && (
            <p className="empty">
              ネットがありません。ルートネットを作成できます。
            </p>
          )}
          <div className="editor-workspace" hidden={!network.path}>
            <EditorGraph
              key={network.path}
              network={network}
              selected={selected}
              connecting={connecting}
              onSelect={select}
              onMove={(node, x, y) =>
                edit(replaceNode(network, { ...node, x, y }))
              }
              onOpen={(id) => {
                if (draft.networks.some((n) => n.path === id)) onNavigate(id);
              }}
            />
            <EditorProperties
              node={node}
              editorVersion={draft.editorVersion ?? 1}
              onChange={(next) => edit(replaceNode(network, next))}
              onOpen={() => onNavigate(selected)}
              onDelete={() => {
                if (!node || !canEdit(node.type, draft.editorVersion ?? 1))
                  return;
                const edges = network.edges.filter(
                  (e) => e.from === node.id || e.to === node.id,
                );
                if (
                  window.confirm(
                    `「${node.name}」${isNetwork(node.type) ? `と子孫を含む${subtreeIds(draft, node.id).size}ユニット` : ""}と関連する接続 ${edges.length} 件を削除しますか？`,
                  )
                ) {
                  update(removeHierarchyNode(draft, node.id));
                  setSelected("");
                }
              }}
            />
          </div>
          <div className="editor-lists">
            <section>
              <h3>
                ユニット一覧 <span>{network.nodes.length}</span>
              </h3>
              <div className="editor-unit-list">
                {network.nodes.map((n) => (
                  <button
                    key={n.id}
                    aria-pressed={selected === n.id}
                    onClick={() => select(n.id)}
                  >
                    <span className="outline-pill">{n.type}</span>
                    {n.name}
                    <small>
                      {n.x ?? "—"}, {n.y ?? "—"}
                    </small>
                  </button>
                ))}
              </div>
            </section>
            <section>
              <h3>
                接続一覧 <span>{network.edges.length}</span>
              </h3>
              <div className="editor-connection-list">
                {network.edges.map((edge, index) => (
                  <div key={`${edge.from}:${edge.to}:${index}`}>
                    <span>
                      {network.nodes.find((n) => n.id === edge.from)?.name}{" "}
                      <span className="editor-muted">→</span>{" "}
                      {network.nodes.find((n) => n.id === edge.to)?.name}
                    </span>
                    <small>{edge.conditional ? "条件成立" : "通常順序"}</small>
                    <button
                      aria-label={`接続 ${index + 1} を削除`}
                      onClick={() =>
                        edit({
                          ...network,
                          edges: network.edges.filter((_, i) => i !== index),
                        })
                      }
                    >
                      <X size={14} />
                    </button>
                  </div>
                ))}
                {!network.edges.length && (
                  <p className="editor-muted">接続はありません。</p>
                )}
              </div>
            </section>
          </div>
          {preview && (
            <section className="editor-preview" aria-label="検証結果">
              <div className="editor-preview-title">
                <h3>
                  {preview.valid
                    ? "検証OK — ダウンロードできます"
                    : "検証エラー — 修正してください"}
                </h3>
                <button onClick={() => setShowPreview(!showPreview)}>
                  {showPreview ? "原文を閉じる" : "原文を確認"}
                </button>
              </div>
              {preview.errors.map((error) => (
                <div key={error} role="alert" className="alert">
                  {error}
                </div>
              ))}
              {preview.layoutErrors.length > 0 && (
                <div className="notice">
                  表示座標の診断（実行定義の検証とは別）
                  <ul>
                    {preview.layoutErrors.map((error) => (
                      <li key={error}>{error}</li>
                    ))}
                  </ul>
                </div>
              )}
              {showPreview && preview.sources.length > 0 && (
                <>
                  <label className="editor-file-select">
                    原文ファイル
                    <select
                      value={Math.min(previewFile, preview.sources.length - 1)}
                      onChange={(e) => setPreviewFile(Number(e.target.value))}
                    >
                      {preview.sources.map((file, i) => (
                        <option key={file.fileName + file.targetPath} value={i}>
                          {file.fileName} · {file.encoding}{" "}
                          {file.changed ? "（変更あり）" : "（変更なし）"}
                        </option>
                      ))}
                    </select>
                  </label>
                  <div className="editor-diff">
                    <div>
                      <h4>変更前</h4>
                      <pre aria-label="変更前の原文">
                        {base?.sources[previewFile]?.text}
                      </pre>
                    </div>
                    <div>
                      <h4>変更後</h4>
                      <pre aria-label="変更後の原文">
                        {preview.sources[previewFile]?.text}
                      </pre>
                    </div>
                  </div>
                </>
              )}
            </section>
          )}
          <div className="editor-footer">
            <span>
              基点版 {draft.definitionHash.slice(0, 12)} ·{" "}
              {base?.sources.length} ファイル · このブラウザーに下書きを保存
            </span>
            <button disabled={busy} onClick={() => void discard()}>
              <RotateCcw size={14} />
              下書きを破棄
            </button>
          </div>
        </>
      ) : (
        <div className="empty">
          {draft
            ? "編集できるジョブネットがありません。"
            : "定義と保存済みの下書きを読み込んでいます…"}
        </div>
      )}
    </div>
  );
}
