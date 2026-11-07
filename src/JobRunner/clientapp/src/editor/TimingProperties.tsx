import { groupPath, originalGroupPath } from "./group-paths";
import { useState } from "react";
import type { EditorDocument, EditorDraft } from "../api/@types";
import { CalendarMonth } from "./CalendarMonth";
import { DraftSchedulePreview } from "./DraftSchedulePreview";

type Field = {
  key: string;
  label: string;
  example: string;
  multiple?: boolean;
};
const schedules: Field[] = [
  {
    key: "sd",
    label: "開始日",
    example: "0,ud（予定なし） / 1,2026/11/6 / 2,*b",
    multiple: true,
  },
  {
    key: "st",
    label: "開始時刻",
    example: "1,09:00 / 2,+01:00",
    multiple: true,
  },
  {
    key: "cy",
    label: "処理周期",
    example: "1,(1,m) / 2,(7,d)",
    multiple: true,
  },
  {
    key: "sh",
    label: "休業日の振替",
    example: "1,ca（中止） / 2,af（後） / be（前） / no（実行）",
    multiple: true,
  },
  {
    key: "shd",
    label: "振替の猶予日数",
    example: "1,2（1〜31日）",
    multiple: true,
  },
  {
    key: "cftd",
    label: "起算",
    example: "1,af,1,10 / 2,da,2 / no",
    multiple: true,
  },
  {
    key: "ln",
    label: "上位ルール参照",
    example: "1,2（自ルール1→上位2、ルートでは無効）",
    multiple: true,
  },
  { key: "ed", label: "有効期日", example: "2026/12/31（この日を含む）" },
  {
    key: "jc",
    label: "参照カレンダー",
    example: "/業務/カレンダー（既存グループ完全名）",
  },
  {
    key: "de",
    label: "上位予定への依存",
    example: "y（依存） / n（独立）、ルートには指定不可",
  },
];
const calendars: Field[] = [
  {
    key: "op",
    label: "運用日",
    example: "mo / 2026/11/6（1行に1値）",
    multiple: true,
  },
  {
    key: "cl",
    label: "休業日",
    example: "sa / su / 2026/12/31（1行に1値）",
    multiple: true,
  },
  { key: "stt", label: "基準時刻", example: "06:00（00:00〜23:59）" },
  {
    key: "sdd",
    label: "基準日",
    example: "20 / mo:2（暦日1〜28、第1〜4曜日）",
  },
  { key: "md", label: "月区分", example: "th（当月） / ne（翌月）" },
];
function TimingFields({
  fields,
  attributes,
  onChange,
}: {
  fields: Field[];
  attributes: Record<string, string[]>;
  onChange: (attributes: Record<string, string[]>) => void;
}) {
  return (
    <div className="editor-timing-fields">
      {fields.map((field) => {
        const defined = Object.hasOwn(attributes, field.key);
        const label = `${field.label} (${field.key})`;
        return (
          <div className="editor-timing-field" key={field.key}>
            <label className="editor-timing-toggle">
              <input
                type="checkbox"
                checked={defined}
                onChange={(event) => {
                  const next = { ...attributes };
                  if (event.target.checked) next[field.key] = [""];
                  else delete next[field.key];
                  onChange(next);
                }}
              />
              {label}を指定
            </label>
            {defined ? (
              <label>
                {label}
                {field.multiple ? (
                  <textarea
                    aria-label={label}
                    rows={Math.min(
                      5,
                      Math.max(2, attributes[field.key].length),
                    )}
                    value={attributes[field.key].join("\n")}
                    onChange={(event) =>
                      onChange({
                        ...attributes,
                        [field.key]: event.target.value.split("\n"),
                      })
                    }
                  />
                ) : (
                  <input
                    aria-label={label}
                    value={attributes[field.key][0] ?? ""}
                    onChange={(event) =>
                      onChange({
                        ...attributes,
                        [field.key]: [event.target.value],
                      })
                    }
                  />
                )}
              </label>
            ) : (
              <span className="editor-muted">省略（継承・既定値）</span>
            )}
            <small>{field.example}</small>
          </div>
        );
      })}
    </div>
  );
}
export function TimingProperties({
  base,
  draft,
  current,
  onChange,
}: {
  base: EditorDocument;
  draft: EditorDraft;
  current: string;
  onChange: (draft: EditorDraft) => void;
}) {
  const [calendarId, setCalendarId] = useState("");
  if (![4, 5, 6, 7, 8, 9].includes(draft.editorVersion ?? 1)) return null;
  const schedule = draft.schedules?.find((s) => s.id === current);
  const visibleCalendars =
    draft.calendars?.filter((c) => groupPath(draft, c.id) !== null) ?? [];
  const calendar =
    visibleCalendars.find((c) => c.id === calendarId) ?? visibleCalendars[0];
  return (
    <details className="editor-timing">
      <summary>スケジュール・カレンダー</summary>
      <p>
        指定を解除すると属性を省略します。省略値を自動で書き込みません。複数値は1行に1値、ルール番号はそのまま保持します。
      </p>
      <p>
        ルートの開始日を省略すると登録日基準です。予定なしは <code>0,ud</code>{" "}
        を指定してください。ネストの独立開始日には <code>de=n</code>{" "}
        が必要です。
      </p>
      <p className="editor-muted">
        {base.timeZone}・{base.scheduleTimeMode}
        時間制。29〜31日と第5曜日の基準日、24時間制の24時以降は未対応です。夏時間の曖昧・欠落時刻は試算時に拒否します。
      </p>
      {schedule && (
        <section aria-label="ネットのスケジュール">
          <h3>現在のネットのスケジュール</h3>
          <TimingFields
            fields={schedules}
            attributes={{
              ...schedule.attributes,
              ...(schedule.attributes.jc
                ? {
                    jc: schedule.attributes.jc.map(
                      (p) => groupPath(draft, p) ?? p,
                    ),
                  }
                : {}),
            }}
            onChange={(attributes) =>
              onChange({
                ...draft,
                schedules: draft.schedules!.map((s) =>
                  s.id === current
                    ? {
                        ...s,
                        attributes: {
                          ...attributes,
                          ...(attributes.jc
                            ? {
                                jc: attributes.jc.map((p) =>
                                  originalGroupPath(draft, p),
                                ),
                              }
                            : {}),
                        },
                      }
                    : s,
                ),
              })
            }
          />
          {schedule.attributes.jc?.[0] && (
            <button onClick={() => setCalendarId(schedule.attributes.jc![0])}>
              参照カレンダーを開く: {schedule.attributes.jc[0]}
            </button>
          )}
          <DraftSchedulePreview draft={draft} current={current} />
        </section>
      )}
      <section aria-label="カレンダーの編集">
        <h3>既存グループのカレンダー</h3>
        {calendar ? (
          <>
            <label>
              編集するカレンダー
              <select
                aria-label="編集するカレンダー"
                value={calendar.id}
                onChange={(event) => setCalendarId(event.target.value)}
              >
                {visibleCalendars.map((c) => (
                  <option key={c.id} value={c.id}>
                    {groupPath(draft, c.id)}
                  </option>
                ))}
              </select>
            </label>
            <p className="editor-muted">
              このグループを参照・継承するネットの下書き試算にも反映します。未指定の日は上位へ探索します。
            </p>
            <CalendarMonth
              key={calendar.id}
              draft={draft}
              calendarId={calendar.id}
              onNavigate={(path) =>
                setCalendarId(originalGroupPath(draft, path))
              }
              attributes={calendar.attributes}
              onChange={(attributes) =>
                onChange({
                  ...draft,
                  calendars: draft.calendars!.map((c) =>
                    c.id === calendar.id ? { ...c, attributes } : c,
                  ),
                })
              }
            />
            <TimingFields
              fields={calendars}
              attributes={calendar.attributes}
              onChange={(attributes) =>
                onChange({
                  ...draft,
                  calendars: draft.calendars!.map((c) =>
                    c.id === calendar.id ? { ...c, attributes } : c,
                  ),
                })
              }
            />
          </>
        ) : (
          <p>編集可能なグループはありません。グループ作成後に編集できます。</p>
        )}
      </section>
    </details>
  );
}
