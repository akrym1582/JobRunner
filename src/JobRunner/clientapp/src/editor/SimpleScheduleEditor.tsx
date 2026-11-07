import { useEffect, useMemo, useState } from "react";
import type { EditorDocument, EditorDraft } from "../api/@types";
import { DraftSchedulePreview } from "./DraftSchedulePreview";
import { groupPath } from "./group-paths";
import { networkNames } from "./hierarchy";
import { scheduleParentId, effectiveScheduleRules } from "./schedule-hierarchy";
import {
  applySimple,
  applyNestedSimple,
  upperRule,
  defaults,
  describeSimple,
  nextRule,
  patterns,
  readSimple,
  scheduleRules,
  simpleErrors,
  weekdays,
  withSchedule,
  formatSimpleAttributes,
  type SimpleSchedule,
} from "./simple-schedule";

type Session = {
  draft: EditorDraft;
  current: string;
  rule: number;
  original?: SimpleSchedule;
  value: SimpleSchedule;
  mode: "parent" | "own";
  originalMode: "parent" | "own";
  upper: string;
  originalUpper: string;
  adding: boolean;
};
export function SimpleScheduleEditor({
  base,
  draft,
  current,
  disabled,
  onChange,
  onPendingChange,
}: {
  base: EditorDocument;
  draft: EditorDraft;
  current: string;
  disabled: boolean;
  onChange: (draft: EditorDraft) => void;
  onPendingChange: (pending: boolean) => void;
}) {
  const [session, setSession] = useState<Session>();
  const [rule, setRule] = useState(1);
  const names = useMemo(() => networkNames(draft), [draft]);
  const parentId = useMemo(
    () => scheduleParentId(draft, current),
    [draft, current],
  );
  const nested = !!parentId;
  const eligible = names.has(current);
  const parentRules = parentId
    ? effectiveScheduleRules(draft, parentId)
    : undefined;
  const attributes = useMemo(
    () => draft.schedules?.find((s) => s.id === current)?.attributes ?? {},
    [draft, current],
  );
  const mode = nested && attributes.de?.[0] !== "n" ? "parent" : "own";
  const availableRule = nextRule(
    mode === "parent"
      ? { ...attributes, sd: attributes.sd ?? ["0,ud"] }
      : attributes,
    draft.execution?.find((s) => s.id === current)?.attributes,
  );
  const rules = scheduleRules(attributes);
  const selected = rules.includes(rule) ? rule : rules[0];
  const original = selected && readSimple(attributes, selected);
  const pending = !!session;
  useEffect(() => {
    onPendingChange(pending);
    return () => onPendingChange(false);
  }, [pending, onPendingChange]);
  useEffect(() => {
    if (!pending) return;
    const guard = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", guard);
    return () => window.removeEventListener("beforeunload", guard);
  }, [pending]);
  const stale =
    !!session && (session.draft !== draft || session.current !== current);
  const errors =
    session && session.mode === "own"
      ? simpleErrors(session.value, base.scheduleTimeMode ?? 48)
      : [];
  if (
    session &&
    nested &&
    session.mode === "own" &&
    !/^(?:[1-9]|[1-9]\d|1[0-3]\d|14[0-4])$/.test(session.upper)
  )
    errors.push("上位対応ルールは1〜144で指定してください。");
  if (session && session.mode === "own" && !session.adding && !session.original)
    errors.push(
      "既存の独自ルールは詳細編集の対象です。元の設定へ戻すには未適用入力を破棄してください。",
    );
  if (session && session.mode === "own" && session.adding && !availableRule)
    errors.push(
      "新しい独自ルールの空き番号がありません。詳細編集で既存ルールを確認してください。",
    );
  const candidate = useMemo(() => {
    if (!session || stale || errors.length) return undefined;
    return withSchedule(
      draft,
      current,
      nested
        ? applyNestedSimple(
            attributes,
            session.rule,
            session.value,
            session.original,
            session.mode,
            session.originalMode,
            session.upper,
            session.originalUpper,
          )
        : applySimple(
            attributes,
            session.rule,
            session.value,
            session.original,
          ),
    );
  }, [session, stale, errors.length, draft, current, attributes, nested]);
  function discard() {
    if (!session || window.confirm("簡単設定の未適用入力を破棄しますか？")) {
      setSession(undefined);
      return true;
    }
    return false;
  }
  function begin(adding: boolean, desiredMode: "parent" | "own" = "own") {
    if (!discard()) return;
    // 上位依存への切替はルールを生成しないため、予定なし・空番なしでも開けます。
    // 仮の番号を独自設定へ流用しないよう、新規ルールの空きは別途検証します。
    const id =
      (adding || mode === "parent" ? availableRule : selected) ??
      (desiredMode === "parent" ? 1 : undefined);
    if (!id) return;
    const value = adding
      ? { ...defaults(), calendar: attributes.jc?.[0] ?? "" }
      : (original ??
        (desiredMode === "parent"
          ? { ...defaults(), calendar: attributes.jc?.[0] ?? "" }
          : undefined));
    if (!value) return;
    setSession({
      draft,
      current,
      rule: id,
      original: adding ? undefined : original || undefined,
      value,
      mode: desiredMode,
      originalMode: mode,
      upper: upperRule(attributes, id),
      originalUpper: upperRule(attributes, id),
      adding: adding || mode === "parent",
    });
  }
  function set<K extends keyof SimpleSchedule>(
    key: K,
    value: SimpleSchedule[K],
  ) {
    setSession((s) => s && { ...s, value: { ...s.value, [key]: value } });
  }
  const value = session?.value;
  return (
    <>
      <section
        className="editor-simple-schedule"
        aria-label="スケジュール簡単設定"
      >
        <h3>スケジュール簡単設定</h3>
        <p>
          JP1/AJS3
          V12の標準定義を生成します。適用するまで下書きには保存されません。
        </p>
        {!eligible && <p>編集対象のジョブネットを選択してください。</p>}
        {nested && (
          <p>
            上位ネット: {names.get(parentId!)}
            。独自設定でも、上位の予定と対応ルールが成立する日だけが対象です。
          </p>
        )}
        {nested && (
          <button
            disabled={disabled}
            onClick={() => begin(mode === "parent", "parent")}
          >
            {mode === "parent" ? "依存方法を設定" : "上位依存への切替を設定"}
          </button>
        )}
        {eligible && mode === "own" && (
          <div className="editor-network-fields">
            <label>
              簡単設定のルール
              <select
                aria-label="簡単設定のルール"
                value={selected ?? ""}
                onChange={(e) => {
                  if (discard()) setRule(Number(e.target.value));
                }}
              >
                {!rules.length && <option value="">予定なし</option>}
                {rules.map((id) => (
                  <option key={id} value={id}>
                    ルール {id}
                  </option>
                ))}
              </select>
            </label>
            <button
              disabled={disabled || !original}
              onClick={() => begin(false)}
            >
              このルールを簡単設定で編集
            </button>
            <button
              disabled={disabled || !availableRule}
              onClick={() => begin(true)}
            >
              簡単設定でルールを追加
            </button>
          </div>
        )}
        {eligible && mode === "own" && !original && rules.length > 0 && (
          <p>
            この指定は簡単設定の対象外です。原文を保持して詳細編集を使ってください。
          </p>
        )}
        {session && value && (
          <>
            <p role="status">
              {session.mode === "parent"
                ? "上位依存"
                : `ルール ${session.rule}`}{" "}
              の未適用入力（ブラウザー保存の対象外）
            </p>
            {stale && (
              <p role="alert">
                下書きまたは対象ネットが変わりました。古い候補は適用できません。入力を破棄して開き直してください。
              </p>
            )}
            {nested && (
              <label>
                スケジュールの依存方法
                <select
                  aria-label="スケジュールの依存方法"
                  disabled={stale || disabled}
                  value={session.mode}
                  onChange={(e) =>
                    setSession({
                      ...session,
                      mode: e.target.value as "parent" | "own",
                    })
                  }
                >
                  <option value="parent">上位のスケジュールを使用</option>
                  <option value="own">独自スケジュールを設定</option>
                </select>
              </label>
            )}
            {nested && session.mode === "own" && (
              <label>
                リンクする上位ルール番号
                <input
                  list="editor-upper-schedule-rules"
                  aria-label="リンクする上位ルール番号"
                  type="number"
                  min="1"
                  max="144"
                  disabled={stale || disabled}
                  value={session.upper}
                  onChange={(e) =>
                    setSession({ ...session, upper: e.target.value })
                  }
                />
              </label>
            )}
            {nested && (
              <datalist id="editor-upper-schedule-rules">
                {parentRules!.rules.map((id) => (
                  <option key={id} value={id}>
                    上位ルール {id}
                  </option>
                ))}
              </datalist>
            )}
            {nested && session.mode === "own" && (
              <p>
                上位の有効ルール（{names.get(parentRules!.source)}）:{" "}
                {parentRules!.rules.join(", ") || "予定なし"}
                {!parentRules!.rules.includes(Number(session.upper)) &&
                  "。指定した対応先がないため、現在の上位予定では対象になりません。"}
              </p>
            )}
            {nested && session.mode === "parent" && (
              <p>
                独自の日付・時刻・参照カレンダーは使用しません。切替はこのネットの全ルールに影響します。
              </p>
            )}
            <fieldset disabled={stale || disabled || session.mode === "parent"}>
              <legend>簡単設定の入力</legend>
              <div className="editor-network-fields">
                <label>
                  実行パターン
                  <select
                    aria-label="実行パターン"
                    value={value.pattern}
                    onChange={(e) => {
                      const pattern = e.target
                        .value as SimpleSchedule["pattern"];
                      setSession({
                        ...session,
                        value: {
                          ...value,
                          pattern,
                          month: ["daily", "working", "once"].includes(pattern)
                            ? ""
                            : value.month,
                          shift: defaults(pattern).shift,
                        },
                      });
                    }}
                  >
                    {Object.entries(patterns).map(([id, label]) => (
                      <option key={id} value={id}>
                        {label}
                      </option>
                    ))}
                  </select>
                </label>
                {!["daily", "working", "once"].includes(value.pattern) && (
                  <label>
                    開始年月（省略は登録月）
                    <input
                      type="month"
                      value={value.month}
                      onChange={(e) => set("month", e.target.value)}
                    />
                  </label>
                )}
                {["monthly", "offset"].includes(value.pattern) && (
                  <label>
                    毎月の暦日
                    <input
                      type="number"
                      min="1"
                      max="31"
                      value={value.day}
                      onChange={(e) => set("day", e.target.value)}
                    />
                  </label>
                )}
                {value.pattern === "nth" && (
                  <label>
                    業務月の運用日番号
                    <input
                      type="number"
                      min="1"
                      max="35"
                      value={value.index}
                      onChange={(e) => set("index", e.target.value)}
                    />
                  </label>
                )}
                {value.pattern === "weekly" && (
                  <label>
                    毎週の曜日
                    <select
                      aria-label="毎週の曜日"
                      value={value.weekday}
                      onChange={(e) => set("weekday", e.target.value)}
                    >
                      {weekdays.map((day, i) => (
                        <option key={day} value={day}>
                          {["日", "月", "火", "水", "木", "金", "土"][i]}曜日
                        </option>
                      ))}
                    </select>
                  </label>
                )}
                {value.pattern === "once" && (
                  <label>
                    一回実行の日付
                    <input
                      type="date"
                      min="1994-01-01"
                      max="2036-12-31"
                      value={value.date}
                      onChange={(e) => set("date", e.target.value)}
                    />
                  </label>
                )}
                <label>
                  簡単設定の開始時刻
                  <input
                    placeholder="09:00"
                    value={value.time}
                    onChange={(e) => set("time", e.target.value)}
                  />
                </label>
                <label>
                  <input
                    type="checkbox"
                    checked={value.relative}
                    onChange={(e) => set("relative", e.target.checked)}
                  />
                  基準時刻からの相対時刻
                </label>
                <label>
                  簡単設定のカレンダー
                  <select
                    aria-label="簡単設定のカレンダー"
                    value={value.calendar}
                    onChange={(e) => set("calendar", e.target.value)}
                  >
                    <option value="">省略（親グループから継承）</option>
                    {draft.calendars
                      ?.filter((c) => groupPath(draft, c.id) !== null)
                      .map((c) => (
                        <option key={c.id} value={c.id}>
                          {groupPath(draft, c.id)}
                        </option>
                      ))}
                  </select>
                </label>
                <label>
                  休業日の扱い
                  <select
                    disabled={value.pattern === "working"}
                    aria-label="休業日の扱い"
                    value={value.shift}
                    onChange={(e) => set("shift", e.target.value)}
                  >
                    <option value="ca">中止</option>
                    <option value="no">そのまま実行（振替なし）</option>
                    <option value="be">前の運用日へ振替</option>
                    <option value="af">後の運用日へ振替</option>
                  </select>
                </label>
                {
                  <label>
                    振替猶予（暦日）
                    <input
                      type="number"
                      min="1"
                      max="31"
                      value={value.shiftDays}
                      onChange={(e) => set("shiftDays", e.target.value)}
                    />
                  </label>
                }
                {value.pattern === "offset" && (
                  <>
                    <label>
                      起算方向
                      <select
                        aria-label="起算方向"
                        value={value.direction}
                        onChange={(e) => set("direction", e.target.value)}
                      >
                        <option value="af">運用日後</option>
                        <option value="be">運用日前</option>
                      </select>
                    </label>
                    <label>
                      起算する運用日数
                      <input
                        type="number"
                        min="1"
                        max="31"
                        value={value.count}
                        onChange={(e) => set("count", e.target.value)}
                      />
                    </label>
                    <label>
                      起算猶予（暦日）
                      <input
                        type="number"
                        min="1"
                        max="31"
                        value={value.grace}
                        onChange={(e) => set("grace", e.target.value)}
                      />
                    </label>
                  </>
                )}
              </div>
            </fieldset>
            {session.mode === "own" && <p>{describeSimple(value)}</p>}
            <p>
              運用日番号・最終運用日はカレンダーの基準日・月区分に従います。起算は休業日振替の後に行い、起点の前日/翌日から数えます。猶予内に必要な運用日がない場合は候補なしです。
            </p>
            <p>参照カレンダーの変更は、このネットの全ルールへ影響します。</p>
            <pre aria-label="生成するJP1属性">
              {candidate
                ? formatSimpleAttributes(
                    candidate.schedules!.find((s) => s.id === current)!
                      .attributes,
                    session.rule,
                    (path) => groupPath(draft, path) ?? path,
                  )
                : "入力を修正すると属性を表示します。"}
            </pre>
            {errors.map((error) => (
              <p role="alert" key={error}>
                {error}
              </p>
            ))}
            <button
              disabled={disabled || !candidate}
              onClick={() => {
                if (!candidate || !eligible || stale) return;
                const notices: string[] = [];
                if (nested && session.mode !== session.originalMode)
                  notices.push(
                    session.mode === "parent"
                      ? "上位依存へ切り替えます。全ての独自の開始日(sd)を削除し、他の属性は保持します。全ルールに影響します。"
                      : "独自スケジュールへ切り替えます。上位の予定が成立する日だけが対象です。全ルールの依存方法に影響します。",
                  );
                if (
                  session.mode === "own" &&
                  attributes.sd?.some((s) => /^0,\s*ud$/.test(s))
                )
                  notices.push(
                    "予定なしを解除し、実行予定を定義します。保留・運用停止は解除しません。",
                  );
                if (
                  session.mode === "own" &&
                  value.calendar !== (attributes.jc?.[0] ?? "")
                )
                  notices.push(
                    "参照カレンダーを変更します。このネットの全ルールに影響します。",
                  );
                if (
                  notices.length &&
                  !window.confirm(
                    notices.join("\n") + "\n下書きへ適用しますか？",
                  )
                )
                  return;
                onChange(candidate);
                setRule(session.rule);
                setSession(undefined);
              }}
            >
              簡単設定を下書きへ適用
            </button>
            <button onClick={discard}>簡単設定の入力を破棄</button>
          </>
        )}
      </section>
      <DraftSchedulePreview
        draft={candidate ?? draft}
        current={current}
        candidate={!!candidate}
        blocked={!!session && !candidate}
      />
    </>
  );
}
