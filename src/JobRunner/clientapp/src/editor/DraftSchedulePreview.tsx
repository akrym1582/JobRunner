import { useMemo, useRef, useState } from "react";
import type {
  EditorDraft,
  EditorSchedulePreview,
  EditorNestedSchedulePreview,
} from "../api/@types";
import { client, errorMessage } from "../client";
import { formatDate } from "../status";
import { networkNames } from "./hierarchy";
import { scheduleParentId } from "./schedule-hierarchy";
import { NestedScheduleResults } from "./NestedScheduleResults";

export function DraftSchedulePreview({
  draft,
  current,
  candidate = false,
  blocked = false,
}: {
  draft: EditorDraft;
  current: string;
  candidate?: boolean;
  blocked?: boolean;
}) {
  const today = new Date().toISOString().slice(0, 10);
  const [anchor, setAnchor] = useState(today);
  const [from, setFrom] = useState(today);
  const [until, setUntil] = useState(() =>
    new Date(Date.now() + 31 * 86400000).toISOString().slice(0, 10),
  );
  const [response, setResponse] = useState<{
    key: string;
    data?: EditorSchedulePreview | EditorNestedSchedulePreview;
    error?: string;
    busy?: boolean;
  }>();
  const serial = useRef(0);
  const key = useMemo(
    () =>
      JSON.stringify({
        draft,
        current,
        anchor,
        from,
        until,
        candidate,
        blocked,
      }),
    [draft, current, anchor, from, until, candidate, blocked],
  );
  const latest = useRef(key);
  latest.current = key;
  const names = useMemo(() => networkNames(draft), [draft]);
  const nested = !!scheduleParentId(draft, current);
  const eligible = names.has(current);
  const visible = response?.key === key ? response : undefined;
  return (
    <section className="editor-schedule-preview" aria-label="下書き予定試算">
      <h3>
        {candidate
          ? "未適用の簡単設定の試算（保存・登録しません）"
          : "下書きの予定試算（登録しません）"}
      </h3>
      <p>
        実行予定・登録済み世代ではありません。選択したネットのスケジュール候補を計算します。ネストはルートから祖先の予定に連動して試算します。先行完了・異常発生・保留・停止・起動条件・間隔登録を含む実開始は予測しません。計算が5秒を超える場合は期間またはルール数を減らしてください。
      </p>
      {!eligible && <p>編集対象のジョブネットを選択してください。</p>}
      <div className="editor-network-fields">
        <label>
          仮の登録基準日
          <input
            type="date"
            value={anchor}
            onChange={(e) => setAnchor(e.target.value)}
          />
        </label>
        <label>
          試算開始業務日
          <input
            type="date"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
          />
        </label>
        <label>
          試算終了業務日（含まない）
          <input
            type="date"
            value={until}
            onChange={(e) => setUntil(e.target.value)}
          />
        </label>
        <button
          disabled={
            blocked || !eligible || !anchor || !from || !until || visible?.busy
          }
          onClick={async () => {
            const request = ++serial.current;
            setResponse({ key, busy: true });
            try {
              const body = { draft, networkId: current, anchor, from, until };
              const data = nested
                ? await client.ui.definition_editor.nested_schedules.$post({
                    body,
                  })
                : await client.ui.definition_editor.schedules.$post({ body });
              if (request === serial.current && latest.current === key)
                setResponse({ key, data });
            } catch (error) {
              const message = await errorMessage(error);
              if (request === serial.current && latest.current === key)
                setResponse({ key, error: message });
            } finally {
              // 条件を戻しても、終了した通信の「処理中」を復活させません。
              setResponse((previous) =>
                request === serial.current &&
                previous?.key === key &&
                previous.busy
                  ? undefined
                  : previous,
              );
            }
          }}
        >
          下書きを試算
        </button>
      </div>
      <p className="editor-muted">
        1994〜2036年・最大366日・2000候補。編集や条件変更で結果を無効にし、再試算します。
      </p>
      {visible?.busy && <p role="status">下書きから計算しています…</p>}
      {visible?.error && <p role="alert">{visible.error}</p>}
      {visible?.data &&
        ("rows" in visible.data ? (
          <NestedScheduleResults data={visible.data} />
        ) : (
          <>
            <p role="status">
              下書き候補 {visible.data.points.length}件・{visible.data.timeZone}
              （未登録）
            </p>
            <table>
              <thead>
                <tr>
                  <th>ルール</th>
                  <th>業務日</th>
                  <th>実日時</th>
                </tr>
              </thead>
              <tbody>
                {visible.data.points.map((point) => (
                  <tr key={point.due}>
                    <td>{point.rule}</td>
                    <td>{point.businessDate}</td>
                    <td>{formatDate(point.due, visible.data!.timeZone)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </>
        ))}
    </section>
  );
}
