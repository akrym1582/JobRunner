import { useRef, useState } from "react";
import type { EditorCalendarDay, EditorDraft } from "../api/@types";
import { client, errorMessage } from "../client";

function sameDate(value: string, date: string) {
  const parts = value.split("/");
  return (
    parts.length === 3 &&
    parts.map((v, i) => (i ? v.padStart(2, "0") : v)).join("-") === date
  );
}
export function changeCalendarDate(
  attributes: Record<string, string[]>,
  date: string,
  choice: string,
) {
  const next = { ...attributes };
  for (const key of ["op", "cl"]) {
    if (!next[key]) continue;
    const remaining = next[key].filter((v) => !sameDate(v, date));
    if (remaining.length) next[key] = remaining;
    else delete next[key];
  }
  if (choice !== "inherit") {
    const value = date.split("-").map(Number).join("/");
    next[choice] = [...(next[choice] ?? []), value];
  }
  return next;
}
export function CalendarMonth({
  draft,
  calendarId,
  attributes,
  onChange,
  onNavigate,
}: {
  draft: EditorDraft;
  calendarId: string;
  attributes: Record<string, string[]>;
  onChange: (value: Record<string, string[]>) => void;
  onNavigate: (path: string) => void;
}) {
  const [month, setMonth] = useState(new Date().toISOString().slice(0, 7));
  const [date, setDate] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [result, setResult] = useState<{
    draft: EditorDraft;
    month: string;
    days: EditorCalendarDay[];
  }>();
  const latest = useRef({ draft, month });
  latest.current = { draft, month };
  const visible =
    result?.draft === draft && result.month === month ? result.days : undefined;
  const weekdays = ["su", "mo", "tu", "we", "th", "fr", "sa"];
  const labels = ["日", "月", "火", "水", "木", "金", "土"];
  return (
    <section aria-label="カレンダー月編集" className="editor-calendar-month">
      <h4>月表示で編集</h4>
      <label>
        表示月{" "}
        <input
          type="month"
          value={month}
          min="1994-01"
          max="2036-12"
          onChange={(e) => {
            setMonth(e.target.value);
            setDate("");
          }}
        />
      </label>
      <button
        disabled={busy || !month}
        onClick={async () => {
          setBusy(true);
          setMessage("");
          try {
            const days = await client.ui.definition_editor.calendar.$post({
              body: { draft, unitId: calendarId, month: `${month}-01` },
            });
            if (
              latest.current.draft === draft &&
              latest.current.month === month
            )
              setResult({ draft, month, days });
          } catch (error) {
            if (latest.current.draft === draft)
              setMessage(await errorMessage(error));
          } finally {
            setBusy(false);
          }
        }}
      >
        {busy ? "評価中…" : "実効カレンダーを表示"}
      </button>
      <p>
        編集後は再表示してください。個別指定の解除はこの日付のop/clだけを除き、曜日・上位グループの指定へ戻します。
      </p>
      {message && <p role="alert">{message}</p>}
      {visible && (
        <div className="calendar-grid" role="group" aria-label="実効稼働日">
          {labels.map((label) => (
            <strong key={label}>{label}</strong>
          ))}
          {visible.map((day, index) => (
            <button
              style={
                index === 0
                  ? {
                      gridColumnStart:
                        new Date(`${day.date}T00:00:00Z`).getUTCDay() + 1,
                    }
                  : undefined
              }
              key={day.date}
              className={
                day.evaluation.working ? "calendar-open" : "calendar-closed"
              }
              aria-pressed={date === day.date}
              onClick={() => setDate(day.date)}
              title={`${day.evaluation.sourcePath ?? "既定値"}・${day.evaluation.rule}`}
            >
              <strong>{Number(day.date.slice(-2))}</strong>{" "}
              {day.evaluation.working ? "稼働" : "休業"}
              <small>
                {day.evaluation.sourcePath ?? "既定値"} ({day.evaluation.rule})
              </small>
            </button>
          ))}
        </div>
      )}
      {visible &&
        [
          ...new Set(
            visible
              .map((day) => day.evaluation.sourcePath)
              .filter((path): path is string => !!path),
          ),
        ].map((path) => (
          <button key={path} onClick={() => onNavigate(path)}>
            継承元を開く: {path}
          </button>
        ))}
      {date && (
        <div className="editor-network-fields">
          <span>{date}</span>
          <button
            onClick={() => onChange(changeCalendarDate(attributes, date, "op"))}
          >
            この日を稼働日
          </button>
          <button
            onClick={() => onChange(changeCalendarDate(attributes, date, "cl"))}
          >
            この日を休業日
          </button>
          <button
            onClick={() =>
              onChange(changeCalendarDate(attributes, date, "inherit"))
            }
          >
            この日の指定を解除
          </button>
        </div>
      )}
      <fieldset>
        <legend>曜日の指定</legend>
        {weekdays.map((day, i) => (
          <label key={day}>
            {labels[i]}
            <select
              aria-label={`${labels[i]}曜日の稼働指定`}
              value={
                attributes.op?.includes(day)
                  ? "op"
                  : attributes.cl?.includes(day)
                    ? "cl"
                    : "inherit"
              }
              onChange={(e) => {
                const next = { ...attributes };
                for (const key of ["op", "cl"]) {
                  if (!next[key]) continue;
                  const remaining = next[key].filter((v) => v !== day);
                  if (remaining.length) next[key] = remaining;
                  else delete next[key];
                }
                if (e.target.value !== "inherit")
                  next[e.target.value] = [...(next[e.target.value] ?? []), day];
                onChange(next);
              }}
            >
              <option value="inherit">継承・既定値</option>
              <option value="op">稼働</option>
              <option value="cl">休業</option>
            </select>
          </label>
        ))}
      </fieldset>
    </section>
  );
}
