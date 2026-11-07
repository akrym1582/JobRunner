import type { EditorDraft } from "../api/@types";

export type Attributes = Record<string, string[]>;
export const patterns = {
  daily: "毎日",
  working: "毎運用日",
  weekly: "毎週の曜日",
  monthly: "毎月の指定日",
  nth: "毎月の第N運用日",
  last: "毎月の最終運用日",
  once: "指定日に一度",
  offset: "毎月の指定日から前後N運用日",
} as const;
export type SimpleSchedule = {
  pattern: keyof typeof patterns;
  month: string;
  date: string;
  day: string;
  weekday: string;
  index: string;
  time: string;
  relative: boolean;
  shift: string;
  shiftDays: string;
  direction: string;
  count: string;
  grace: string;
  calendar: string;
};
export const managedKeys = ["sd", "st", "cy", "sh", "shd", "cftd"];
const numberedKeys = [...managedKeys, "ln", "sy", "ey"];
export const weekdays = ["su", "mo", "tu", "we", "th", "fr", "sa"];
export function splitRule(raw: string): [number, string] {
  const match = /^(\d+),\s*(.*)$/.exec(raw);
  return match ? [Number(match[1]), match[2]] : [1, raw];
}
export function scheduleRules(attributes: Attributes): number[] {
  return Array.from(
    new Set((attributes.sd ?? ["en"]).map((s) => splitRule(s)[0])),
  )
    .filter((id) => id > 0)
    .sort((a, b) => a - b);
}
export function nextRule(
  attributes: Attributes,
  monitoring: Attributes = {},
): number | undefined {
  const used = new Set(
    numberedKeys.flatMap((key) =>
      [
        ...(attributes[key] ?? (key === "sd" ? ["en"] : [])),
        ...(monitoring[key] ?? []),
      ].map((s) => splitRule(s)[0]),
    ),
  );
  return Array.from({ length: 144 }, (_, i) => i + 1).find((i) => !used.has(i));
}
export function defaults(
  pattern: SimpleSchedule["pattern"] = "offset",
): SimpleSchedule {
  return {
    pattern,
    month: "",
    date: "",
    day: "25",
    weekday: "mo",
    index: "1",
    time: "09:00",
    relative: false,
    shift: pattern === "offset" || pattern === "daily" ? "no" : "ca",
    shiftDays: "2",
    direction: "af",
    count: "2",
    grace: "10",
    calendar: "",
  };
}
function ruleValue(
  attributes: Attributes,
  key: string,
  rule: number,
  fallback = "",
) {
  return (
    attributes[key]?.map(splitRule).find(([id]) => id === rule)?.[1] ?? fallback
  );
}
// 確実に往復できる標準属性だけを読み戻します。未対応指定を近いパターンへ変換しません。
export function readSimple(
  attributes: Attributes,
  rule: number,
): SimpleSchedule | undefined {
  if (
    numberedKeys.some(
      (key) =>
        (attributes[key] ?? []).filter((s) => splitRule(s)[0] === rule).length >
        1,
    )
  )
    return;
  const sd = ruleValue(attributes, "sd", rule, rule === 1 ? "en" : "");
  const cy = ruleValue(attributes, "cy", rule);
  const origin = ruleValue(attributes, "cftd", rule, "no");
  const result = defaults();
  const parts = /^(?:(\d{4})\/(\d{1,2})\/)?([^/]+)$/.exec(sd);
  if (!parts) return;
  const selector = parts[3];
  result.month = parts[1] ? `${parts[1]}-${parts[2].padStart(2, "0")}` : "";
  if (selector === "en" && cy === "(1,d)") result.pattern = "daily";
  else if (weekdays.includes(selector) && cy === "(1,w)") {
    result.pattern = "weekly";
    result.weekday = selector;
  } else if (/^\d{1,2}$/.test(selector) && cy === "(1,m)") {
    result.pattern = "monthly";
    result.day = selector;
  } else if (/^\*\d{1,2}$/.test(selector) && cy === "(1,m)") {
    result.pattern = "nth";
    result.index = selector.slice(1);
  } else if (selector === "*b" && cy === "(1,m)") result.pattern = "last";
  else if (parts[1] && /^\d{1,2}$/.test(selector) && !cy) {
    result.pattern = "once";
    result.date = `${result.month}-${selector.padStart(2, "0")}`;
    result.month = "";
  } else return;
  if (result.pattern === "daily" && result.month) return;
  if (
    /^(be|af)(,\d{1,2})?(,\d{1,2})?$/.test(origin) &&
    result.pattern === "monthly"
  ) {
    result.pattern = "offset";
    const [direction, count = "1", grace = "10"] = origin.split(",");
    Object.assign(result, { direction, count, grace });
  } else if (!/^no(,\d{1,2})?(,\d{1,2})?$/.test(origin)) return;
  const time = ruleValue(attributes, "st", rule, "+00:00");
  result.relative = time.startsWith("+");
  result.time = time.replace(/^\+/, "");
  result.shift = ruleValue(attributes, "sh", rule, "ca");
  result.shiftDays = ruleValue(attributes, "shd", rule, "2");
  result.calendar = attributes.jc?.[0] ?? "";
  if (result.pattern === "daily" && result.shift === "ca")
    result.pattern = "working";
  if (simpleErrors(result, 48).length) return;
  return result;
}
function integer(value: string, max: number) {
  return /^\d+$/.test(value) && Number(value) >= 1 && Number(value) <= max;
}
export function simpleErrors(value: SimpleSchedule, mode: number): string[] {
  const errors: string[] = [];
  if (!Object.hasOwn(patterns, value.pattern))
    errors.push("パターンが不正です。");
  if (
    !/^\d{1,2}:[0-5]\d$/.test(value.time) ||
    Number(value.time.split(":")[0]) >= mode
  )
    errors.push(`時刻は00:00〜${mode - 1}:59で指定してください。`);
  if (
    value.month &&
    !/^(199[4-9]|20[0-2]\d|203[0-6])-(0[1-9]|1[0-2])$/.test(value.month)
  )
    errors.push("開始年月は1994〜2036年で指定してください。");
  if (["monthly", "offset"].includes(value.pattern) && !integer(value.day, 31))
    errors.push("暦日は1〜31で指定してください。");
  if (value.pattern === "nth" && !integer(value.index, 35))
    errors.push("運用日番号は1〜35で指定してください。");
  if (value.pattern === "weekly" && !weekdays.includes(value.weekday))
    errors.push("曜日が不正です。");
  if (value.pattern === "once") {
    const d = new Date(value.date + "T00:00:00Z");
    if (
      !/^\d{4}-\d{2}-\d{2}$/.test(value.date) ||
      !Number.isFinite(d.getTime()) ||
      d.toISOString().slice(0, 10) !== value.date ||
      value.date < "1994-01-01" ||
      value.date > "2036-12-31"
    )
      errors.push("実在する1994〜2036年の日付を指定してください。");
  }
  if (!["no", "ca", "be", "af"].includes(value.shift))
    errors.push("振り替え方法が不正です。");
  if (!integer(value.shiftDays, 31))
    errors.push("振替猶予は1〜31暦日で指定してください。");
  if (
    value.pattern === "offset" &&
    (!["be", "af"].includes(value.direction) ||
      !integer(value.count, 31) ||
      !integer(value.grace, 31))
  )
    errors.push("起算日数・猶予は1〜31で指定してください。");
  return errors;
}
export function simpleAttributes(
  value: SimpleSchedule,
): Record<string, string | undefined> {
  const prefix = value.month
    ? `${Number(value.month.slice(0, 4))}/${Number(value.month.slice(5))}/`
    : "";
  const selector = {
    daily: "en",
    working: "en",
    weekly: value.weekday,
    monthly: String(Number(value.day)),
    nth: `*${Number(value.index)}`,
    last: "*b",
    once: value.date.split("-").map(Number).join("/"),
    offset: String(Number(value.day)),
  }[value.pattern];
  return {
    sd:
      (value.pattern === "once" || ["daily", "working"].includes(value.pattern)
        ? ""
        : prefix) + selector,
    st: (value.relative ? "+" : "") + value.time,
    cy:
      value.pattern === "once"
        ? undefined
        : `(1,${["daily", "working"].includes(value.pattern) ? "d" : value.pattern === "weekly" ? "w" : "m"})`,
    sh: value.shift,
    shd: String(Number(value.shiftDays)),
    cftd:
      value.pattern === "offset"
        ? `${value.direction},${Number(value.count)},${Number(value.grace)}`
        : "no",
  };
}
export function applySimple(
  attributes: Attributes,
  rule: number,
  value: SimpleSchedule,
  original?: SimpleSchedule,
): Attributes {
  // sd省略時の暗黙の第1ルールも、新規追加で消さないよう明示化します。
  const next: Attributes = {
    ...attributes,
    ...(!original && !attributes.sd ? { sd: ["1,en"] } : {}),
  };
  const generated = simpleAttributes(value),
    before = original && simpleAttributes(original);
  for (const key of managedKeys) {
    // 値が同じなら、省略・番号省略・無効な付加値も原文のまま残します。
    if (before && before[key] === generated[key]) continue;
    const values = next[key] ?? [];
    const index = values.findIndex((s) => splitRule(s)[0] === rule);
    const replacement =
      generated[key] === undefined ? [] : [`${rule},${generated[key]}`];
    next[key] =
      index < 0
        ? [...values, ...replacement]
        : [
            ...values.slice(0, index),
            ...replacement,
            ...values.slice(index + 1),
          ];
    if (!next[key].length) delete next[key];
  }
  // 予定なしからの明示的な追加だけで0,udを解除します。
  if (next.sd) next.sd = next.sd.filter((s) => splitRule(s)[0] !== 0);
  if (!original || value.calendar !== original.calendar) {
    if (value.calendar) next.jc = [value.calendar];
    else delete next.jc;
  }
  return next;
}
export function withSchedule(
  draft: EditorDraft,
  id: string,
  attributes: Attributes,
): EditorDraft {
  return {
    ...draft,
    schedules: draft.schedules!.map((s) =>
      s.id === id ? { ...s, attributes } : s,
    ),
  };
}
export function describeSimple(value: SimpleSchedule): string {
  const subject =
    value.pattern === "offset"
      ? `毎月${value.day}日の${value.count}運用日${value.direction === "af" ? "後" : "前"}（起点を含めず、猶予${value.grace}暦日）`
      : value.pattern === "monthly"
        ? `毎月${value.day}日`
        : value.pattern === "nth"
          ? `業務月の第${value.index}運用日`
          : value.pattern === "last"
            ? "業務月の最終運用日"
            : value.pattern === "weekly"
              ? `毎週${["日", "月", "火", "水", "木", "金", "土"][weekdays.indexOf(value.weekday)]}曜日`
              : value.pattern === "once"
                ? value.date
                : patterns[value.pattern];
  const shift =
    value.pattern === "offset"
      ? `起点が休業日の場合は${{ no: "振替なし", ca: "起算せず中止", be: "前の運用日へ振替", af: "後の運用日へ振替" }[value.shift]}`
      : `休業日は${{ no: "そのまま実行", ca: "中止", be: "前へ振替", af: "後へ振替" }[value.shift]}`;
  return `${subject}、${value.relative ? "基準時刻から+" : ""}${value.time}。${shift}。`;
}

// 書出し側と同じJP1の引用規則で、参照先の空白・#・引用符も定義として表示します。
export function formatSimpleAttributes(
  attributes: Attributes,
  rule: number,
  calendarPath: (path: string) => string = (path) => path,
): string {
  return Object.entries(attributes)
    .filter(([key]) => [...managedKeys, "jc", "de", "ln"].includes(key))
    .flatMap(([key, values]) =>
      values
        .filter(
          (value) => ["jc", "de"].includes(key) || splitRule(value)[0] === rule,
        )
        .map((value) => {
          const text =
            key === "jc"
              ? '"' +
                calendarPath(value)
                  .replaceAll("#", "##")
                  .replaceAll('"', '#"') +
                '"'
              : value;
          return `${key}=${text};`;
        }),
    )
    .join("\n");
}

export function upperRule(attributes: Attributes, rule: number): string {
  return ruleValue(attributes, "ln", rule, String(rule));
}
export function applyNestedSimple(
  attributes: Attributes,
  rule: number,
  value: SimpleSchedule,
  original: SimpleSchedule | undefined,
  mode: "parent" | "own",
  originalMode: "parent" | "own",
  upper: string,
  originalUpper: string,
): Attributes {
  if (mode === "parent") {
    const next = { ...attributes };
    delete next.sd;
    if (originalMode !== "parent") next.de = ["y"];
    return next;
  }
  // 上位依存のsd省略は、ルートの暗黙第1ルールとは異なります。
  const base =
    originalMode === "parent"
      ? { ...attributes, sd: attributes.sd ?? ["0,ud"] }
      : attributes;
  const next = applySimple(base, rule, value, original);
  if (attributes.de?.[0] !== "n") next.de = ["n"];
  if (!original || originalMode === "parent" || upper !== originalUpper) {
    const values = next.ln ?? [],
      index = values.findIndex((s) => splitRule(s)[0] === rule);
    const generated = `${rule},${Number(upper)}`;
    next.ln =
      index < 0
        ? [...values, generated]
        : values.map((s, i) => (i === index ? generated : s));
  }
  return next;
}
