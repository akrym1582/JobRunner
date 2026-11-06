import { useState } from "react";
import type { Node, Occurrence } from "./api/@types";
import { matchesStatus } from "./filter-model";
import { formatDate, label, tone } from "./status";
export function GraphJobs({
  nodes,
  statuses,
  zone,
  selected,
  onSelect,
  onLog,
}: {
  nodes: Node[];
  statuses: string[];
  zone: string;
  selected: string;
  onSelect: (path: string) => void;
  onLog: (execution: Occurrence) => void;
}) {
  const [page, setPage] = useState(0);
  const matches = nodes.filter((n) => matchesStatus(n.status, statuses));
  const current = Math.min(
    page,
    Math.max(0, Math.ceil(matches.length / 20) - 1),
  );
  return (
    <section className="schedule-panel graph-jobs" aria-label="ジョブ一覧">
      <div className="schedule-heading">
        <h3>ジョブ一覧</h3>
        <span>
          {matches.length} / {nodes.length}件
        </span>
      </div>
      <div className="table-scroll">
        <table>
          <thead>
            <tr>
              <th>ユニット</th>
              <th>状態</th>
              <th>開始日時</th>
              <th>終了日時</th>
              <th>終了コード</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {matches.slice(current * 20, (current + 1) * 20).map((node) => (
              <tr
                key={node.path}
                className={selected === node.path ? "selected-row" : ""}
              >
                <td>
                  <button onClick={() => onSelect(node.path)}>
                    {node.name}
                  </button>
                </td>
                <td>
                  <span className={`status-badge ${tone(node.status)}`}>
                    {label(node.status)}
                  </span>
                </td>
                <td>{formatDate(node.execution?.startedAt, zone)}</td>
                <td>{formatDate(node.execution?.finishedAt, zone)}</td>
                <td>{node.exitCode ?? "—"}</td>
                <td>
                  {!node.isNetwork && node.execution && (
                    <button onClick={() => onLog(node.execution!)}>
                      ログを見る
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {!matches.length && (
          <div className="empty">該当するユニットはありません。</div>
        )}
      </div>
      <div className="table-footer">
        <span>
          表示 {Math.min(current * 20 + 1, matches.length)}–
          {Math.min((current + 1) * 20, matches.length)} / {matches.length}件
        </span>
        <div>
          <button disabled={current === 0} onClick={() => setPage(current - 1)}>
            前へ
          </button>
          <button
            disabled={(current + 1) * 20 >= matches.length}
            onClick={() => setPage(current + 1)}
          >
            次へ
          </button>
        </div>
      </div>
    </section>
  );
}
