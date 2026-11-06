import { useState } from "react";
import useAspidaSWR from "@aspida/swr";
import {
  CalendarDays,
  ChevronLeft,
  ChevronRight,
  FolderTree,
  GitBranch,
  RefreshCw,
} from "lucide-react";
import { client } from "./client";
import type { MonthlyEntry } from "./api/@types";
import { formatDate, label, tone } from "./status";
export function monthInZone(zone: string) {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: zone,
    year: "numeric",
    month: "2-digit",
  }).formatToParts(new Date());
  return `${parts.find((p) => p.type === "year")!.value}-${parts.find((p) => p.type === "month")!.value}`;
}
export function monthDays(month: string) {
  const date = new Date(month + "-01T12:00:00Z");
  date.setUTCMonth(date.getUTCMonth() + 1, 0);
  return date.getUTCDate();
}
export function Monthly({
  rootPath,
  zone,
  onOpen,
}: {
  rootPath: string;
  zone: string;
  onOpen: (entry: MonthlyEntry) => void;
}) {
  const [month, setMonth] = useState(() => monthInZone(zone));
  const [all, setAll] = useState(false);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [selection, setSelection] = useState<{ path: string; day: string }>();
  const [detailPage, setDetailPage] = useState(0);
  const [updated, setUpdated] = useState("");
  const schedule = useAspidaSWR(client.ui.monthly, {
    query: { month, rootPath: all ? "/" : rootPath },
    enabled: !!rootPath,
    refreshInterval: 5000,
    onSuccess: () => setUpdated(new Date().toLocaleTimeString("ja-JP")),
  });
  const days = Array.from({ length: monthDays(month) }, (_, i) => i + 1);
  const rows = schedule.data?.units ?? [];
  const entries = schedule.data?.entries ?? [];
  const cells = new Map<string, MonthlyEntry[]>();
  for (const entry of entries) {
    const key = entry.execution.unitPath + "|" + entry.calendarDate;
    const cell = cells.get(key) || [];
    cell.push(entry);
    cells.set(key, cell);
  }
  const visible = rows.filter(
    (row) =>
      ![...collapsed].some((parent) => row.path.startsWith(parent + "/")),
  );
  const details = selection
    ? entries.filter(
        (e) =>
          e.execution.unitPath === selection.path &&
          e.calendarDate === selection.day,
      )
    : entries;
  const todayParts = new Intl.DateTimeFormat("en-CA", {
    timeZone: zone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(new Date());
  const today = ["year", "month", "day"]
    .map((k) => todayParts.find((p) => p.type === k)!.value)
    .join("-");
  function changeMonth(value: string) {
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(value)) return;
    setMonth(value);
    setSelection(undefined);
    setDetailPage(0);
  }
  function shift(delta: number) {
    const date = new Date(month + "-01T12:00:00Z");
    date.setUTCMonth(date.getUTCMonth() + delta);
    changeMonth(
      `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, "0")}`,
    );
  }
  function toggle(path: string) {
    setCollapsed((previous) => {
      const next = new Set(previous);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }
  return (
    <section className="monthly-view" aria-label="マンスリースケジュール">
      <div className="monthly-heading">
        <div>
          <CalendarDays size={21} />
          <h2>マンスリースケジュール</h2>
          <span className="outline-pill">確定予定・実績</span>
        </div>
        <span className="heading-meta">最終取得 {updated || "—"}</span>
      </div>
      <div className="monthly-toolbar">
        <div className="month-navigation">
          <button aria-label="前月" onClick={() => shift(-1)}>
            <ChevronLeft size={16} />
          </button>
          <input
            aria-label="表示月"
            type="month"
            min="0001-01"
            max="9998-12"
            value={month}
            onChange={(e) => changeMonth(e.target.value)}
          />
          <button aria-label="翌月" onClick={() => shift(1)}>
            <ChevronRight size={16} />
          </button>
          <button onClick={() => changeMonth(monthInZone(zone))}>今月</button>
        </div>
        <label>
          <input
            type="checkbox"
            checked={all}
            onChange={(e) => {
              setAll(e.target.checked);
              setSelection(undefined);
              setDetailPage(0);
            }}
          />
          全ジョブネット
        </label>
        <button
          aria-label="月間予定を再取得"
          onClick={() => void schedule.mutate()}
        >
          <RefreshCw size={15} />
        </button>
      </div>
      {schedule.error && (
        <div role="alert" className="alert">
          月間予定を取得できません。
          <button onClick={() => void schedule.mutate()}>再取得</button>
        </div>
      )}
      <div className="monthly-help">
        <span>日付は予定日時の暦日 ({zone})</span>
        <span>□ 未開始の予定 / ○ 開始済み・実績 / 数字は実行回数</span>
      </div>
      <div className="month-grid-scroll">
        <table className="month-grid">
          <thead>
            <tr>
              <th className="unit-column">
                <FolderTree size={14} />
                ユニット
              </th>
              {days.map((day) => {
                const date = `${month}-${String(day).padStart(2, "0")}`;
                const weekday = new Date(date + "T12:00:00Z").getUTCDay();
                return (
                  <th
                    key={day}
                    className={`${weekday === 0 ? "sunday" : weekday === 6 ? "saturday" : ""} ${date === today ? "today" : ""}`}
                  >
                    <strong>{day}</strong>
                    <small>{"日月火水木金土"[weekday]}</small>
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {visible.map((row) => (
              <tr
                key={row.path}
                className={selection?.path === row.path ? "selected-unit" : ""}
              >
                <th
                  className="unit-column"
                  style={{
                    paddingLeft:
                      12 +
                      Math.max(
                        0,
                        row.path.split("/").length -
                          (all ? 2 : rootPath.split("/").length),
                      ) *
                        14,
                  }}
                >
                  {rows.some((child) => child.parent === row.path) ? (
                    <button
                      className="tree-toggle"
                      aria-label={`${row.name}を${collapsed.has(row.path) ? "展開" : "折りたたむ"}`}
                      onClick={() => toggle(row.path)}
                    >
                      {collapsed.has(row.path) ? "▸" : "▾"}
                    </button>
                  ) : (
                    <span className="tree-spacer" />
                  )}
                  <span title={row.path}>{row.name}</span>
                  <small>
                    {row.isNetwork ? "NET" : row.type === "g" ? "GROUP" : "JOB"}
                  </small>
                </th>
                {days.map((day) => {
                  const date = `${month}-${String(day).padStart(2, "0")}`;
                  const values = cells.get(row.path + "|" + date) || [];
                  const status =
                    values.find((e) => tone(e.status) === "danger")?.status ||
                    values.find((e) => tone(e.status) === "warning")?.status ||
                    values.find((e) => tone(e.status) === "running")?.status ||
                    values[0]?.status ||
                    "";
                  const started = values.some(
                    (e) => e.execution.started || e.execution.startedAt,
                  );
                  return (
                    <td
                      key={day}
                      className={`${date === today ? "today" : ""} ${selection?.path === row.path && selection.day === date ? "selected-cell" : ""}`}
                    >
                      <button
                        aria-label={`${row.name} ${date} ${values.length}件`}
                        title={
                          values
                            .map(
                              (e) =>
                                `${formatDate(e.execution.due, zone)} ${label(e.status)}`,
                            )
                            .join("\n") || "保存済みの予定・実績なし"
                        }
                        className="day-cell"
                        onClick={() => {
                          setSelection({ path: row.path, day: date });
                          setDetailPage(0);
                        }}
                      >
                        {values.length > 0 && (
                          <>
                            <i
                              className={`schedule-mark ${tone(status)} ${started ? "actual" : "planned"}`}
                            />
                            {values.length > 1 && <sup>{values.length}</sup>}
                          </>
                        )}
                      </button>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
        {schedule.isLoading && (
          <div className="empty">月間予定を読み込んでいます…</div>
        )}
        {schedule.data && !rows.length && (
          <div className="empty">対象のユニットはありません。</div>
        )}
      </div>
      <p className="monthly-note">
        生成済みの確定予定と実績を表示しています。空欄は保存済みデータがない日です。未生成の将来予定は含みません。
      </p>
      <section className="schedule-panel monthly-details">
        <div className="schedule-heading">
          <div>
            <GitBranch size={17} />
            <h3>
              {selection
                ? `${selection.path.split("/").pop()} · ${selection.day}`
                : "月内の確定予定・実績"}
            </h3>
            <span className="count">{details.length}</span>
          </div>
          <button
            onClick={() => {
              setSelection(undefined);
              setDetailPage(0);
            }}
          >
            選択を解除
          </button>
        </div>
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>ユニット名</th>
                <th>状態</th>
                <th>コメント</th>
                <th>実行ID</th>
                <th>予定日時</th>
                <th>業務日</th>
                <th>開始日時</th>
                <th>終了日時</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {details
                .slice(detailPage * 20, (detailPage + 1) * 20)
                .map((entry) => (
                  <tr key={entry.execution.id}>
                    <td>{entry.execution.unitPath?.split("/").pop()}</td>
                    <td>
                      <span className={`status-badge ${tone(entry.status)}`}>
                        {label(entry.status)}
                      </span>
                    </td>
                    <td>{entry.comment || "—"}</td>
                    <td>{entry.executionId}</td>
                    <td>{formatDate(entry.execution.due, zone)}</td>
                    <td>{entry.execution.businessDate}</td>
                    <td>{formatDate(entry.execution.startedAt, zone)}</td>
                    <td>{formatDate(entry.execution.finishedAt, zone)}</td>
                    <td>
                      <button onClick={() => onOpen(entry)}>図を開く</button>
                    </td>
                  </tr>
                ))}
            </tbody>
          </table>
          {!details.length && !schedule.isLoading && (
            <div className="empty">保存済みの予定・実績はありません。</div>
          )}
        </div>
        <div className="table-footer">
          <span>
            表示 {Math.min(detailPage * 20 + 1, details.length)}–
            {Math.min((detailPage + 1) * 20, details.length)} / {details.length}
            件
          </span>
          <div>
            <button
              disabled={detailPage === 0}
              onClick={() => setDetailPage((p) => p - 1)}
            >
              前へ
            </button>
            <button
              disabled={(detailPage + 1) * 20 >= details.length}
              onClick={() => setDetailPage((p) => p + 1)}
            >
              次へ
            </button>
          </div>
        </div>
      </section>
    </section>
  );
}
