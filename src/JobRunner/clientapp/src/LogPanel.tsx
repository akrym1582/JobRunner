import { useEffect, useMemo, useRef, useState } from "react";
import useAspidaSWR from "@aspida/swr";
import { Maximize, Minimize, Terminal, X } from "lucide-react";
import type {
  AttemptPage,
  JobAttempt,
  LogChunk,
  Occurrence,
} from "./api/@types";
import { client, errorMessage } from "./client";
import { formatDate, label } from "./status";
import { isLiveAttempt, MAX_LOG_CHUNKS, mergeChunks } from "./log-model";

// 親画面の再描画でSWRの5秒タイマーがリセットされないよう、関数の参照を固定します。
const attemptRefreshInterval = (data: AttemptPage | undefined) =>
  data?.archived ? 0 : 5000;

type Target = { rootId: string; occurrenceId: string };
export function LogPanel({
  execution,
  zone,
  onClose,
}: {
  execution: Occurrence;
  zone: string;
  onClose: () => void;
}) {
  const target: Target = {
    rootId: execution.rootId!,
    occurrenceId: execution.id!,
  };
  const [selected, setSelected] = useState<string>();
  const [stream, setStream] = useState<"stdout" | "stderr">("stdout");
  const [expanded, setExpanded] = useState(false);
  const [wrap, setWrap] = useState(true);
  const [generation, setGeneration] = useState(0);
  const attempts = useAspidaSWR(client.ui.attempts, {
    query: target,
    refreshInterval: attemptRefreshInterval,
  });
  const items = attempts.data?.items ?? [];
  const attempt = items.find((item) => item.id === selected);
  const newest = items[0]?.id;
  useEffect(() => {
    if (!selected && newest) setSelected(newest);
  }, [selected, newest]);
  const panel = useRef<HTMLElement>(null);
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    return () => {
      if (opener?.isConnected) opener.focus();
    };
  }, []);
  return (
    <section
      ref={panel}
      tabIndex={-1}
      className={`log-panel ${expanded ? "expanded" : ""}`}
      aria-label="実行ログ"
      onKeyDown={(event) => {
        if (event.key === "Escape") {
          if (expanded) setExpanded(false);
          else onClose();
        }
        if (expanded && event.key === "Tab") {
          const controls = panel.current?.querySelectorAll<HTMLElement>(
            'button:not(:disabled),input,select,[tabindex="0"]',
          );
          if (!controls?.length) return;
          const first = controls[0],
            last = controls[controls.length - 1];
          if (
            event.shiftKey &&
            (document.activeElement === first ||
              document.activeElement === panel.current)
          ) {
            event.preventDefault();
            last.focus();
          } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
          }
        }
      }}
    >
      <div className="log-heading">
        <div>
          <Terminal size={19} />
          <h2>実行ログ</h2>
          <span>{execution.unitPath?.split("/").pop()}</span>
        </div>
        <div>
          <button
            aria-label={expanded ? "ログを縮小" : "ログを拡大"}
            aria-expanded={expanded}
            onClick={() => setExpanded(!expanded)}
          >
            {expanded ? <Minimize size={16} /> : <Maximize size={16} />}
          </button>
          <button aria-label="ログを閉じる" onClick={onClose}>
            <X size={17} />
          </button>
        </div>
      </div>
      <p className="log-target">
        {execution.unitPath} <span>ルート実行回: {execution.rootId}</span>
      </p>
      {attempts.error && (
        <LogError error={attempts.error} retry={() => void attempts.mutate()} />
      )}
      {attempts.isLoading && (
        <p className="empty">試行履歴を読み込んでいます…</p>
      )}
      {attempts.data && !items.length && (
        <p className="empty">
          試行履歴はありません。まだ起動されていないジョブにはログがありません。
        </p>
      )}
      {!!items.length && (
        <>
          <div className="log-attempt">
            <label>
              試行
              <select
                aria-label="ログの試行"
                value={selected ?? ""}
                onChange={(event) => {
                  setSelected(event.target.value);
                  setStream("stdout");
                }}
              >
                {items.map((item, index) => (
                  <option value={item.id} key={item.id}>
                    {index === 0 ? "最新 · " : ""}
                    {formatDate(item.startedAt, zone)} ·{" "}
                    {label(item.state ?? "")} · {item.id}
                  </option>
                ))}
              </select>
            </label>
            <span>開始 {formatDate(attempt?.startedAt, zone)}</span>
            <span>終了 {formatDate(attempt?.finishedAt, zone)}</span>
            <strong>終了コード {attempt?.exitCode ?? "—"}</strong>
          </div>
          {newest && selected && newest !== selected && (
            <div className="notice">
              選択中より新しい試行があります。
              <button
                onClick={() => {
                  setSelected(newest);
                  setStream("stdout");
                }}
              >
                最新の試行を見る
              </button>
            </div>
          )}
          <div
            className="log-tabs"
            role="tablist"
            aria-label="ログのストリーム"
          >
            {(["stdout", "stderr"] as const).map((value) => (
              <button
                key={value}
                id={`tab-${value}`}
                role="tab"
                aria-controls="log-content"
                aria-selected={stream === value}
                onKeyDown={(event) => {
                  if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
                    event.preventDefault();
                    const next = value === "stdout" ? "stderr" : "stdout";
                    setStream(next);
                    document.getElementById(`tab-${next}`)?.focus();
                  }
                }}
                onClick={() => setStream(value)}
              >
                {value === "stdout" ? "標準出力" : "標準エラー"}{" "}
                <small>{value}</small>
              </button>
            ))}
          </div>
          {attempt?.id && (
            <LogBody
              key={`${attempt.id}:${stream}:${generation}`}
              target={target}
              attempt={attempt}
              stream={stream}
              wrap={wrap}
              onWrap={setWrap}
              onLatest={() => setGeneration((g) => g + 1)}
            />
          )}
        </>
      )}
    </section>
  );
}
function LogError({ error, retry }: { error: unknown; retry: () => void }) {
  const [message, setMessage] = useState("");
  useEffect(() => {
    let alive = true;
    void errorMessage(error).then((text) => {
      if (alive) setMessage(text);
    });
    return () => {
      alive = false;
    };
  }, [error]);
  return (
    <div className="alert" role="alert">
      ログを取得できません。{message}
      <button onClick={retry}>ログを再取得</button>
    </div>
  );
}
function LogBody({
  target,
  attempt,
  stream,
  onLatest,
  wrap,
  onWrap,
}: {
  target: Target;
  attempt: JobAttempt;
  stream: string;
  onLatest: () => void;
  wrap: boolean;
  onWrap: (value: boolean) => void;
}) {
  const [follow, setFollow] = useState(true);
  const [search, setSearch] = useState("");
  const [searchFrom, setSearchFrom] = useState(0);
  const [copyMessage, setCopyMessage] = useState("");
  const [chunks, setChunks] = useState<LogChunk[]>([]);
  const [previous, setPrevious] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [complete, setComplete] = useState(attempt.logsComplete ?? false);
  const [request, setRequest] = useState<{
    cursor?: string;
    mode: "replace" | "append" | "prepend";
  }>({ mode: "replace" });
  const viewport = useRef<HTMLPreElement>(null);
  const mark = useRef<HTMLElement>(null);
  const scrollBefore = useRef<{ height: number; top: number } | null>(null);
  const following = useRef(follow);
  following.current = follow;
  const requestKey = `${request.mode}:${request.cursor ?? "tail"}`;
  const currentRequest = useRef(requestKey);
  currentRequest.current = requestKey;
  const live = isLiveAttempt(attempt.state);
  const body = useAspidaSWR(client.ui.logs, {
    query: {
      ...target,
      attemptId: attempt.id!,
      stream,
      cursor: request.cursor,
    },
    refreshInterval: follow && live ? 5000 : 0,
    dedupingInterval: 0,
    revalidateOnMount: true,
    revalidateOnFocus: false,
    revalidateOnReconnect: follow,
    onSuccess: (data) => {
      if (
        currentRequest.current !== requestKey ||
        (request.mode !== "prepend" && loaded && !following.current)
      )
        return;
      setLoaded(true);
      setComplete(data.logsComplete);
      setChunks((current) => {
        const next = mergeChunks(current, data.chunks, request.mode);
        const trimmed =
          request.mode === "append" &&
          next[0]?.sequence !== current[0]?.sequence;
        if (request.mode !== "append" || trimmed)
          setPrevious(
            trimmed ? `before:${next[0].sequence}` : data.previousCursor,
          );
        return next;
      });
      if (data.chunks.length && following.current && (live || data.hasMore))
        setRequest({ mode: "append", cursor: data.nextCursor });
    },
  });
  const refreshBody = body.mutate;
  const wasLive = useRef(live);
  useEffect(() => {
    // 終了状態を観測した時に最後の保存分を取り、以後本文を定期取得しません。
    if (wasLive.current && !live && following.current) void refreshBody();
    wasLive.current = live;
  }, [live, refreshBody]);
  useEffect(() => {
    const element = viewport.current;
    if (!element) return;
    if (scrollBefore.current) {
      element.scrollTop =
        scrollBefore.current.top +
        element.scrollHeight -
        scrollBefore.current.height;
      scrollBefore.current = null;
    } else if (following.current) element.scrollTop = element.scrollHeight;
  }, [chunks]);
  const text = useMemo(
    () => chunks.map((chunk) => chunk.text).join(""),
    [chunks],
  );
  const match = search ? text.indexOf(search, searchFrom) : -1;
  const count = useMemo(() => {
    if (!search) return 0;
    let found = 0,
      from = 0;
    while ((from = text.indexOf(search, from)) >= 0) {
      found++;
      from += search.length;
    }
    return found;
  }, [text, search]);
  useEffect(() => {
    if (search) mark.current?.scrollIntoView({ block: "nearest" });
  }, [search, match]);
  return (
    <div id="log-content" role="tabpanel" aria-labelledby={`tab-${stream}`}>
      <div className="log-controls">
        <label>
          <input
            type="checkbox"
            checked={follow}
            onChange={(event) => {
              if (event.target.checked) onLatest();
              else setFollow(false);
            }}
          />
          自動追尾（5秒）
        </label>
        <button onClick={onLatest}>最新へ</button>
        <button
          disabled={!previous || body.isValidating}
          onClick={() => {
            setFollow(false);
            if (viewport.current)
              scrollBefore.current = {
                height: viewport.current.scrollHeight,
                top: viewport.current.scrollTop,
              };
            setRequest({ mode: "prepend", cursor: previous! });
          }}
        >
          前のログ
        </button>
        <label>
          <input
            type="checkbox"
            checked={wrap}
            onChange={(event) => onWrap(event.target.checked)}
          />
          折り返す
        </label>
        <label className="log-search">
          表示範囲内を検索
          <input
            type="search"
            value={search}
            onChange={(event) => {
              setSearch(event.target.value);
              setSearchFrom(0);
              if (event.target.value) setFollow(false);
            }}
          />
        </label>
        {!!search && (
          <>
            <span>{count}件</span>
            <button
              disabled={!count}
              onClick={() =>
                setSearchFrom(
                  text.indexOf(search, match + search.length) < 0
                    ? 0
                    : match + search.length,
                )
              }
            >
              次の一致
            </button>
          </>
        )}
        <button
          disabled={!text}
          onClick={async () => {
            try {
              await navigator.clipboard.writeText(text);
              setCopyMessage("表示範囲をコピーしました");
            } catch {
              setCopyMessage(
                "コピーできません。ブラウザーの権限を確認してください。",
              );
            }
          }}
        >
          表示範囲をコピー
        </button>
      </div>
      {body.error && (
        <LogError error={body.error} retry={() => void body.mutate()} />
      )}
      <div className="log-range">
        <span>
          {chunks.length
            ? `チャンク ${chunks[0].sequence + 1}–${chunks[chunks.length - 1].sequence + 1} · ${chunks.length}/${MAX_LOG_CHUNKS}片`
            : "表示範囲なし"}{" "}
          · 全文ではなく最大64片を保持
        </span>
        <span>
          {live ? (follow ? "追尾中" : "追尾停止") : "試行終了・自動更新停止"} ·{" "}
          {complete ? "ログ保存完了" : "ログ保存未完了"}
        </span>
      </div>
      {!loaded && !body.error && (
        <p className="empty">ログを読み込んでいます…</p>
      )}
      {loaded && !chunks.length && (
        <p className="empty">
          保存済みログがありません。未出力・保存待ち・保持期限による削除の可能性があります。
        </p>
      )}
      {!complete && loaded && (
        <p className="log-warning">
          {live
            ? "出力は保存された分から表示します。"
            : "ログの保存は完了していません。表示内容が一部欠けている可能性があります。"}
        </p>
      )}
      <pre
        ref={viewport}
        tabIndex={0}
        aria-label="ログ本文"
        className={`log-console ${wrap ? "wrap" : ""}`}
      >
        {match < 0 ? (
          text
        ) : (
          <>
            {text.slice(0, match)}
            <mark ref={mark}>{text.slice(match, match + search.length)}</mark>
            {text.slice(match + search.length)}
          </>
        )}
      </pre>
      {copyMessage && (
        <p role="status" className="log-copy">
          {copyMessage}
        </p>
      )}
    </div>
  );
}
