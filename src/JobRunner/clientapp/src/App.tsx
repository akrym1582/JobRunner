import { useEffect, useRef, useState } from "react";
import useAspidaSWR from "@aspida/swr";
import {
  Activity,
  ArrowRight,
  CalendarDays,
  Check,
  ChevronRight,
  CircleHelp,
  Clock3,
  FolderTree,
  FilePenLine,
  GitBranch,
  Layers3,
  RefreshCw,
  Search,
  Settings2,
  X,
} from "lucide-react";
import type { Occurrence, Operation } from "./api/@types";
import { client, errorMessage } from "./client";
import { GroupControls } from "./GroupControls";
import { RuntimeControls } from "./RuntimeControls";
import { DefinitionEditor } from "./editor/DefinitionEditor";
import { Graph } from "./Graph";
import { Monthly } from "./Monthly";
import { LogPanel } from "./LogPanel";
import { StatusFilter } from "./StatusFilter";
import { GraphJobs } from "./GraphJobs";
import { formatDate, label, tone } from "./status";

function initialOperation(): Operation | null {
  try {
    return JSON.parse(sessionStorage.getItem("jobrunner-operation") || "null");
  } catch {
    return null;
  }
}
export function App() {
  const [view, setView] = useState<"graph" | "monthly" | "editor">("graph");
  const [readError, setReadError] = useState("");
  const [network, setNetwork] = useState("");
  const [editorNetwork, setEditorNetwork] = useState("");
  const [rootId, setRootId] = useState("");
  const [selected, setSelected] = useState("");
  const [statuses, setStatuses] = useState<string[]>([]);
  const [focus, setFocus] = useState<{ path: string; revision: number }>();
  const [logTarget, setLogTarget] = useState<Occurrence>();
  const [search, setSearch] = useState("");
  const [groupId, setGroupId] = useState("");
  const groupCatalog = useAspidaSWR(client.ui.groups, {
    refreshInterval: 1500,
  });
  const selectedGroup = groupCatalog.data?.find((g) => g.id === groupId);
  const [page, setPage] = useState(0);
  const [from, setFrom] = useState("");
  const [until, setUntil] = useState("");
  const [modal, setModal] = useState("");
  const [due, setDue] = useState("");
  const [operation, setOperation] = useState<Operation | null>(
    initialOperation,
  );
  const [message, setMessage] = useState("");
  const [sending, setSending] = useState(false);
  const [lastUpdated, setLastUpdated] = useState("");
  const form = useRef<HTMLFormElement>(null);
  const catalog = useAspidaSWR(client.ui.jobnets, { refreshInterval: 5000 });
  const networks = catalog.data?.networks ?? [];
  const path = network || networks[0]?.path || "";
  const navigationPath = view === "editor" ? editorNetwork || path : path;
  const rootPath =
    networks
      .filter((n) => path === n.path || path.startsWith(n.path + "/"))
      .sort((a, b) => a.path.length - b.path.length)[0]?.path || path;
  const zone = catalog.data?.timeZone || "Asia/Tokyo";
  const executions = useAspidaSWR(client.ui.executions, {
    query: {
      rootPath,
      page,
      from: from ? `${from}T00:00:00Z` : undefined,
      until: until ? `${until}T23:59:59Z` : undefined,
    },
    enabled: !!path && view === "graph",
    refreshInterval: 5000,
  });
  const graph = useAspidaSWR(client.ui.graph, {
    query: { unitPath: path, rootId: rootId || undefined },
    enabled: !!path && view === "graph",
    refreshInterval: 5000,
    onSuccess: () => setLastUpdated(new Date().toLocaleTimeString("ja-JP")),
  });
  const result = useAspidaSWR(client.operations._id(operation?.id || "_"), {
    enabled: !!operation?.id,
    refreshInterval: (data) =>
      data?.status === "Completed" || data?.status === "Rejected" ? 0 : 1000,
    shouldRetryOnError: true,
    errorRetryInterval: 1000,
  });
  const completed =
    result.data?.status === "Completed" || result.data?.status === "Rejected";
  useEffect(() => {
    if (!completed) return;
    void graph.mutate();
    void executions.mutate();
    setMessage(
      result.data?.status === "Completed"
        ? "操作が完了しました。"
        : `操作が拒否されました: ${result.data?.result}`,
    );
    sessionStorage.removeItem("jobrunner-operation");
    // 完了した操作ごとに一度、表示データを再取得します。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [completed, result.data?.id]);
  useEffect(() => {
    if (modal)
      form.current?.querySelector<HTMLElement>("input,button")?.focus();
  }, [modal]);
  const selectedNode = graph.error
    ? undefined
    : graph.data?.nodes.find((n) => n.path === selected);
  const error =
    catalog.error || (view === "graph" && (graph.error || executions.error));
  useEffect(() => {
    let current = true;
    if (error)
      void errorMessage(error).then((value) => {
        if (current) setReadError(value);
      });
    else setReadError("");
    return () => {
      current = false;
    };
  }, [error]);
  function changeNetwork(value: string) {
    if (view === "editor") {
      setEditorNetwork(value);
      return;
    }
    setNetwork(value);
    setSelected("");
    if (!value.startsWith(rootPath + "/")) {
      setRootId("");
      setPage(0);
    }
  }
  function open(kind: string) {
    setModal(kind);
    setDue(graph.data?.root?.due || new Date().toISOString());
    setMessage("");
  }
  async function send(payload: Operation) {
    setSending(true);
    setModal("");
    setMessage("");
    setOperation(payload);
    sessionStorage.setItem("jobrunner-operation", JSON.stringify(payload));
    try {
      await client.operations.$post({ body: payload });
      setModal("");
      await result.mutate();
    } catch (e) {
      setMessage(await errorMessage(e));
    } finally {
      setSending(false);
    }
  }
  const actionName =
    modal === "Move"
      ? "日時変更"
      : modal === "Cancel"
        ? "実行予定の取消"
        : "変更解除";
  const busy = sending || (!!operation && !completed);
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-symbol">
            <Layers3 size={23} />
          </div>
          <div>
            JobRunner<small>OPERATIONS CONSOLE</small>
          </div>
        </div>
        <div className="workspace-label">WORKSPACE</div>
        <button
          onClick={() => setView("graph")}
          className={`view-nav ${view === "graph" ? "nav-active" : ""}`}
        >
          <GitBranch size={18} />
          ジョブネット
          <ChevronRight size={16} />
        </button>
        <button
          onClick={() => setView("monthly")}
          className={`view-nav ${view === "monthly" ? "nav-active" : ""}`}
        >
          <CalendarDays size={18} />
          マンスリー
          <ChevronRight size={16} />
        </button>
        <button
          onClick={() => setView("editor")}
          className={`view-nav ${view === "editor" ? "nav-active" : ""}`}
        >
          <FilePenLine size={18} />
          定義編集
          <ChevronRight size={16} />
        </button>
        <div className="sidebar-heading">
          <span>ジョブネット一覧</span>
          <span>{networks.length}</span>
        </div>
        <label className="search">
          <Search size={15} />
          <input
            aria-label="ジョブネットを検索"
            placeholder="名前で検索…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </label>
        <nav aria-label="ジョブグループ一覧">
          <button
            className={`network-item ${!groupId ? "active" : ""}`}
            onClick={() => setGroupId("")}
          >
            ジョブルート /
          </button>
          {groupCatalog.data
            ?.filter((g) => !g.retired && g.path.includes(search))
            .map((g) => (
              <button
                key={g.id}
                className={`network-item ${groupId === g.id ? "active" : ""}`}
                style={{
                  paddingLeft:
                    18 + Math.max(0, g.path.split("/").length - 2) * 12,
                }}
                onClick={() => setGroupId(g.id)}
                title={g.path}
              >
                <FolderTree size={16} />
                <span>{g.path.split("/").at(-1)}</span>
                <span>{g.pausedBy.length ? "⏸" : ""}</span>
              </button>
            ))}
        </nav>
        <nav aria-label="ジョブネット一覧">
          {networks
            .filter(
              (n) =>
                n.path.includes(search) &&
                (!selectedGroup || n.path.startsWith(selectedGroup.path + "/")),
            )
            .map((n) => (
              <button
                key={n.path}
                className={`network-item ${navigationPath === n.path ? "active" : ""}`}
                style={{
                  paddingLeft:
                    18 + Math.max(0, n.path.split("/").length - 2) * 12,
                }}
                onClick={() => changeNetwork(n.path)}
              >
                <FolderTree size={16} />
                <span>{n.name}</span>
                {navigationPath === n.path && <span className="active-dot" />}
              </button>
            ))}
        </nav>
        <div className="sidebar-foot">
          <span className="live-dot" />
          {catalog.data?.phase === "Ready"
            ? "スケジューラー稼働中"
            : catalog.data?.phase || "接続しています"}
          <small>{zone}</small>
        </div>
      </aside>
      <main>
        <header className="topbar">
          <div>
            <span className="eyebrow">
              {view === "editor" ? "DEFINITION" : "MONITORING"}
            </span>
            <h1>
              {view === "editor" ? "ジョブ定義の編集" : "ジョブネット監視"}
            </h1>
          </div>
          <div className="topbar-actions">
            <span className="live-pill">
              <span className="live-dot" />
              自動更新 5秒
            </span>
            <button
              className="icon-button"
              aria-label="再取得"
              onClick={() => {
                void catalog.mutate();
                void graph.mutate();
                void executions.mutate();
              }}
            >
              <RefreshCw size={17} />
            </button>
          </div>
        </header>
        <RuntimeControls />
        <GroupControls selected={groupId} onSelect={setGroupId} />
        <div className="content">
          {catalog.data && !catalog.data.definitionHash && (
            <div className="notice" role="status">
              運用定義は未登録です。定義編集からファイルを読み込むか新規作成し、差分を確認して運用へ反映してください。
            </div>
          )}
          {view === "editor" ? (
            <DefinitionEditor
              activeHash={catalog.data?.definitionHash}
              path={editorNetwork || path}
              onNavigate={setEditorNetwork}
            />
          ) : (
            <>
              <div className="breadcrumb">
                <span>ジョブネット</span>
                {networks
                  .filter(
                    (n) => path === n.path || path.startsWith(n.path + "/"),
                  )
                  .map((n) => (
                    <span key={n.path}>
                      <ChevronRight size={13} />
                      <button onClick={() => changeNetwork(n.path)}>
                        {n.name}
                      </button>
                    </span>
                  ))}
              </div>
              <div className="page-heading">
                <div>
                  <h2>
                    {path.split("/").pop() || "ジョブネット"}
                    <span className="outline-pill">
                      {rootId ? "実行状況" : "定義表示"}
                    </span>
                  </h2>
                  <p>{path || "ジョブネットを読み込んでいます"}</p>
                </div>
                <div className="heading-meta">
                  <span>
                    <Clock3 size={14} />
                    最終取得 {lastUpdated || "—"}
                  </span>
                  <small>時刻表示: {zone}</small>
                </div>
              </div>
              {error && (
                <div role="alert" className="alert">
                  {readError || "データを取得できません。再取得してください。"}{" "}
                  <button
                    onClick={() => {
                      void catalog.mutate();
                      void graph.mutate();
                      void executions.mutate();
                    }}
                  >
                    再取得
                  </button>
                </div>
              )}
              {message && (
                <div role="status" className="notice">
                  <Check size={16} />
                  {message}
                  <button
                    aria-label="通知を閉じる"
                    onClick={() => setMessage("")}
                  >
                    <X size={15} />
                  </button>
                </div>
              )}
              {operation && !completed && (
                <div className="notice">
                  操作ID: {operation.id} —{" "}
                  {result.error
                    ? "結果を確認できません"
                    : "処理結果を確認しています"}
                  <button
                    disabled={sending}
                    onClick={() => {
                      void send(operation);
                    }}
                  >
                    同じIDで再送・照会
                  </button>
                </div>
              )}
              <StatusFilter
                selected={statuses}
                onChange={setStatuses}
                disabled={view === "graph" && !graph.data?.root}
              />
              {view === "monthly" ? (
                <Monthly
                  key={rootPath}
                  rootPath={rootPath}
                  zone={zone}
                  statuses={statuses}
                  onLog={setLogTarget}
                  onOpen={(entry) => {
                    setNetwork(entry.networkPath);
                    setRootId(entry.execution.rootId || "");
                    setSelected(entry.execution.unitPath || "");
                    setView("graph");
                  }}
                />
              ) : (
                <>
                  <section className="monitor-panel">
                    <div className="panel-toolbar">
                      <div className="tab-label">
                        <GitBranch size={17} />
                        フロー表示
                      </div>
                      <div className="legend">
                        <span>
                          <i className="success" />
                          正常終了
                        </span>
                        <span>
                          <i className="running" />
                          実行中
                        </span>
                        <span>
                          <i className="warning" />
                          保留・不明
                        </span>
                        <span>
                          <i className="danger" />
                          異常終了
                        </span>
                      </div>
                    </div>
                    <div className="monitor-body">
                      {graph.data && !graph.error ? (
                        <Graph
                          data={graph.data}
                          selected={selected}
                          statuses={graph.data.root ? statuses : []}
                          focus={focus}
                          onSelect={setSelected}
                        />
                      ) : (
                        <div className="empty canvas-placeholder">
                          <Layers3 size={35} />
                          <p>
                            {error
                              ? "ジョブネットを取得できません"
                              : "ジョブネットを読み込んでいます…"}
                          </p>
                        </div>
                      )}
                      <aside className="detail-panel">
                        <div className="detail-title">
                          <Settings2 size={15} />
                          ユニット詳細
                        </div>
                        {selectedNode ? (
                          <>
                            <span
                              className={`status-badge ${tone(selectedNode.status)}`}
                            >
                              {label(selectedNode.status)}
                            </span>
                            <h3>{selectedNode.name}</h3>
                            <p className="unit-path">{selectedNode.path}</p>
                            <dl>
                              <dt>ユニット種別</dt>
                              <dd>
                                {selectedNode.isNetwork
                                  ? "ジョブネット"
                                  : "ジョブ"}{" "}
                                ({selectedNode.type})
                              </dd>
                              <dt>予定日時</dt>
                              <dd>
                                {formatDate(selectedNode.execution?.due, zone)}
                              </dd>
                              <dt>業務日</dt>
                              <dd>
                                {selectedNode.execution?.businessDate || "—"}
                              </dd>
                              <dt>開始日時</dt>
                              <dd>
                                {formatDate(
                                  selectedNode.execution?.startedAt,
                                  zone,
                                )}
                              </dd>
                              <dt>終了日時</dt>
                              <dd>
                                {formatDate(
                                  selectedNode.execution?.finishedAt,
                                  zone,
                                )}
                              </dd>
                              <dt>終了コード</dt>
                              <dd>{selectedNode.exitCode ?? "—"}</dd>
                              <dt>理由</dt>
                              <dd>
                                {selectedNode.execution?.reason ||
                                  selectedNode.execution?.statusDetail ||
                                  "—"}
                              </dd>
                            </dl>
                            {!selectedNode.isNetwork &&
                              selectedNode.execution && (
                                <button
                                  className="primary"
                                  onClick={() =>
                                    setLogTarget(selectedNode.execution!)
                                  }
                                >
                                  ログを見る
                                </button>
                              )}
                            {selectedNode.isNetwork && (
                              <button
                                className="primary"
                                onClick={() => {
                                  setNetwork(selectedNode.path);
                                  setSelected("");
                                }}
                              >
                                子ネットを開く
                                <ArrowRight size={15} />
                              </button>
                            )}
                          </>
                        ) : (
                          <div className="detail-empty">
                            <CircleHelp size={27} />
                            <p>ユニットを選択</p>
                            <small>
                              図のジョブをクリックすると
                              <br />
                              実行状態や日時を確認できます。
                            </small>
                          </div>
                        )}
                        <div className="definition-hash">
                          {graph.data &&
                            graph.data.definitionHash !==
                              catalog.data?.definitionHash && (
                              <p>
                                過去の定義版を表示しています。実行回を選ぶと、その回の状態を確認できます。
                              </p>
                            )}
                          定義版{" "}
                          <code>
                            {graph.data?.definitionHash.slice(0, 12) || "—"}
                          </code>
                        </div>
                      </aside>
                    </div>
                  </section>
                  {graph.data && !graph.error && (
                    <GraphJobs
                      key={`${path}:${rootId}:${statuses.join(",")}`}
                      nodes={graph.data.nodes}
                      statuses={graph.data.root ? statuses : []}
                      zone={zone}
                      selected={selected}
                      onLog={setLogTarget}
                      onSelect={(value) => {
                        setSelected(value);
                        setFocus({ path: value, revision: Date.now() });
                      }}
                    />
                  )}
                  <section className="schedule-panel">
                    <div className="schedule-heading">
                      <div>
                        <CalendarDays size={18} />
                        <h3>実行予定・履歴</h3>
                        <span className="count">
                          {executions.data?.total ?? 0}
                        </span>
                      </div>
                      <div className="schedule-actions">
                        <button
                          onClick={() => {
                            setRootId("");
                            setSelected("");
                          }}
                        >
                          定義を表示
                        </button>
                        <button
                          disabled={
                            !graph.data?.canRestore || !!graph.error || busy
                          }
                          onClick={() => open("Restore")}
                        >
                          変更解除
                        </button>
                        <button
                          disabled={
                            !graph.data?.canChange || !!graph.error || busy
                          }
                          onClick={() => open("Cancel")}
                        >
                          予定取消
                        </button>
                        <button
                          className="primary"
                          disabled={
                            !graph.data?.canChange || !!graph.error || busy
                          }
                          onClick={() => open("Move")}
                        >
                          <CalendarDays size={14} />
                          日時変更
                        </button>
                      </div>
                    </div>
                    <div className="filters">
                      <span>ルートネットの実行回を選択</span>
                      <label>
                        開始日 (UTC)
                        <input
                          type="date"
                          value={from}
                          onChange={(e) => {
                            setFrom(e.target.value);
                            setPage(0);
                          }}
                        />
                      </label>
                      <label>
                        終了日 (UTC)
                        <input
                          type="date"
                          value={until}
                          onChange={(e) => {
                            setUntil(e.target.value);
                            setPage(0);
                          }}
                        />
                      </label>
                    </div>
                    <div className="table-scroll">
                      <table>
                        <thead>
                          <tr>
                            <th>実行回</th>
                            <th>状態</th>
                            <th>予定日時 ({zone})</th>
                            <th>業務日</th>
                            <th>変更</th>
                          </tr>
                        </thead>
                        <tbody>
                          {executions.data?.items?.map((ex) => (
                            <tr
                              key={ex.id}
                              className={rootId === ex.id ? "chosen" : ""}
                            >
                              <td>
                                <button
                                  className="execution-link"
                                  onClick={() => {
                                    setRootId(ex.id || "");
                                    setSelected("");
                                  }}
                                >
                                  <span className="radio-dot" />
                                  {ex.jp1ExecutionId || ex.id?.slice(0, 12)}
                                </button>
                              </td>
                              <td>
                                <span
                                  className={`status-badge ${tone(ex.state || "")}`}
                                >
                                  {label(ex.state || "")}
                                </span>
                              </td>
                              <td>{formatDate(ex.due, zone)}</td>
                              <td>{ex.businessDate}</td>
                              <td>
                                {ex.scheduleChangeId ? (
                                  <span className="changed">変更あり</span>
                                ) : (
                                  "—"
                                )}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                      {executions.data?.total === 0 && (
                        <div className="empty">
                          この期間の実行予定はありません。
                        </div>
                      )}
                    </div>
                    <div className="table-footer">
                      <span>
                        <Activity size={13} />
                        実行回を選択すると、図に状態を表示します
                      </span>
                      <div>
                        <button
                          disabled={page === 0}
                          onClick={() => setPage((p) => p - 1)}
                        >
                          前へ
                        </button>
                        <span>
                          {page + 1} /{" "}
                          {Math.max(
                            1,
                            Math.ceil((executions.data?.total || 0) / 20),
                          )}
                        </span>
                        <button
                          disabled={
                            (page + 1) * 20 >= (executions.data?.total || 0)
                          }
                          onClick={() => setPage((p) => p + 1)}
                        >
                          次へ
                        </button>
                      </div>
                    </div>
                  </section>
                </>
              )}
            </>
          )}
          {logTarget && (
            <LogPanel
              key={`${logTarget.rootId}:${logTarget.id}`}
              execution={logTarget}
              zone={zone}
              onClose={() => setLogTarget(undefined)}
            />
          )}
          <footer className="page-footer">
            JobRunner <span>ジョブネット運用コンソール</span>
          </footer>
        </div>
      </main>
      {modal && (
        <div
          className="modal-shade"
          onKeyDown={(e) => {
            if (e.key === "Escape" && !sending) setModal("");
            if (e.key === "Tab") {
              const controls = Array.from(
                form.current?.querySelectorAll<HTMLElement>(
                  "input,button:not(:disabled)",
                ) || [],
              );
              const first = controls[0],
                last = controls[controls.length - 1];
              if (e.shiftKey && document.activeElement === first) {
                e.preventDefault();
                last?.focus();
              } else if (!e.shiftKey && document.activeElement === last) {
                e.preventDefault();
                first?.focus();
              }
            }
          }}
        >
          <form
            ref={form}
            role="dialog"
            aria-modal="true"
            aria-label={actionName}
            className="modal"
            onSubmit={(e) => {
              e.preventDefault();
              if (!graph.data?.root?.id) return;
              void send({
                id: crypto.randomUUID(),
                kind: modal,
                target: graph.data.root.id,
                expectedState: graph.data.root.state,
                ...(modal === "Move"
                  ? {
                      due: new Date(due).toISOString(),
                      changeLower: "SHIFT",
                      pushedAhead: "MOVE",
                    }
                  : {}),
              });
            }}
          >
            <div className="modal-heading">
              <CalendarDays size={22} />
              <h2>{actionName}</h2>
            </div>
            <p>選択したルートジョブネットと未開始の子孫が対象です。</p>
            <dl>
              <dt>対象</dt>
              <dd>{rootPath}</dd>
              <dt>実行回</dt>
              <dd>{graph.data?.root?.jp1ExecutionId || rootId}</dd>
              <dt>現在の予定日時</dt>
              <dd>{formatDate(graph.data?.root?.due, zone)}</dd>
            </dl>
            {modal === "Move" && (
              <label className="date-field">
                変更後の日時 (ISO形式・UTCオフセット必須)
                <input
                  required
                  value={due}
                  onChange={(e) => setDue(e.target.value)}
                  placeholder="2026-11-07T09:00:00+09:00"
                  pattern=".*(Z|[+-][0-9]{2}:[0-9]{2})$"
                />
              </label>
            )}
            <p className="modal-note">
              {modal === "Restore"
                ? "一時変更を解除し、変更前の予定に戻します。"
                : modal === "Cancel"
                  ? "この実行回を取り消します。定期スケジュールは変更しません。"
                  : "子ネットの予定も同じ時間差で移動します。"}
            </p>
            <div className="modal-actions">
              <button
                type="button"
                disabled={sending}
                onClick={() => setModal("")}
              >
                閉じる
              </button>
              <button
                className="primary"
                disabled={
                  sending || (modal === "Move" && Number.isNaN(Date.parse(due)))
                }
              >
                {sending ? "送信中…" : "変更を確定"}
              </button>
            </div>
          </form>
        </div>
      )}
    </div>
  );
}
