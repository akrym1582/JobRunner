import { useRef, useState } from "react";
import type { EditorDraft, EditorSchedulePreview } from "../api/@types";
import { client, errorMessage } from "../client";
import { formatDate } from "../status";
import { networkNames } from "./hierarchy";

export function DraftSchedulePreview({
  draft,
  current,
}: {
  draft: EditorDraft;
  current: string;
}) {
  const today = new Date().toISOString().slice(0, 10);
  const [anchor, setAnchor] = useState(today);
  const [from, setFrom] = useState(today);
  const [until, setUntil] = useState(() =>
    new Date(Date.now() + 31 * 86400000).toISOString().slice(0, 10),
  );
  const [response, setResponse] = useState<{
    key: string;
    data?: EditorSchedulePreview;
    error?: string;
    busy?: boolean;
  }>();
  const serial = useRef(0);
  const key = JSON.stringify({ draft, current, anchor, from, until });
  const latest = useRef(key);
  latest.current = key;
  const names = networkNames(draft);
  const root = draft.roots?.find((r) => r.node.id === current);
  const eligible =
    !!root && !Array.from(names.values()).includes(root.parentPath);
  const visible = response?.key === key ? response : undefined;
  return (
    <section className="editor-schedule-preview" aria-label="下書き予定試算">
      <h3>下書きの予定試算（登録しません）</h3>
      <p>
        実行予定・登録済み世代ではありません。ルートの定義候補だけを計算し、保留・起動条件・間隔登録・ネストの実行予測は含みません。
      </p>
      {!eligible && (
        <p>ネストは上位世代の条件が必要なため、単独では試算しません。</p>
      )}
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
          disabled={!eligible || !anchor || !from || !until || visible?.busy}
          onClick={async () => {
            const request = ++serial.current;
            setResponse({ key, busy: true });
            try {
              const data = await client.ui.definition_editor.schedules.$post({
                body: { draft, networkId: current, anchor, from, until },
              });
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
      {visible?.data && (
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
      )}
    </section>
  );
}
