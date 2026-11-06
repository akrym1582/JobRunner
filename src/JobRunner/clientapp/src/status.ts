export const labels: Record<string, string> = {
  DEFINITION: "定義",
  NORMAL: "正常終了",
  Succeeded: "正常終了",
  NORMALFALSE: "条件不成立",
  WARNING: "警告終了",
  Warning: "警告終了",
  RUNNING: "実行中",
  Running: "実行中",
  ABCONT: "異常あり・実行中",
  WACONT: "警告あり・実行中",
  EXECWAIT: "実行待ち",
  Starting: "起動中",
  TIMEWAIT: "時刻待ち",
  TERMWAIT: "先行待ち",
  Waiting: "実行待ち",
  HOLDING: "保留中",
  Held: "保留中",
  ABNORMAL: "異常終了",
  Failed: "異常終了",
  FAIL: "起動失敗",
  UNKNOWN: "結果不明",
  Unknown: "結果不明",
  BYPASS: "取消・除外",
  Cancelled: "取消",
  UNEXEC: "未実行",
  NotScheduled: "未実行",
  KILL: "強制終了",
  TimedOut: "時間超過",
  INTERRUPT: "中断",
  Interrupted: "中断",
  Skipped: "スキップ",
};
export const label = (state: string) => labels[state] ?? state;
export const tone = (state: string) =>
  /^(NORMAL|Succeeded)/.test(state)
    ? "success"
    : /RUNNING|Running|CONT/.test(state)
      ? "running"
      : /ABNORMAL|Failed|FAIL|KILL|TimedOut/.test(state)
        ? "danger"
        : /UNKNOWN|Unknown|WARNING|Warning|HOLDING|Held/.test(state)
          ? "warning"
          : "neutral";
// サービスのタイムゾーンごとに書式を再利用し、大きな月間一覧でも毎セル生成しません。
const dateFormats = new Map<string, Intl.DateTimeFormat>();
function dateFormat(zone: string) {
  let formatter = dateFormats.get(zone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat("ja-JP", {
      timeZone: zone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: false,
    });
    if (dateFormats.size >= 8)
      dateFormats.delete(dateFormats.keys().next().value!);
    dateFormats.set(zone, formatter);
  }
  return formatter;
}
export const formatDate = (value: string | null | undefined, zone: string) =>
  value ? dateFormat(zone).format(new Date(value)) : "—";
