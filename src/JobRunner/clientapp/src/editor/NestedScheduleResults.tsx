import { useState } from "react";
import type {
  EditorNestedSchedulePreview,
  EditorNestedScheduleStep,
} from "../api/@types";
import { formatDate } from "../status";
const reasons: Record<string, string> = {
  ROOT: "ルート予定",
  ROOT_NOT_SCHEDULED: "上位予定なし（ルート）",
  ANCESTOR_EXCLUDED: "祖先が対象外",
  RULE_NOT_LINKED: "上位ルールと対応しません",
  NO_SAME_BUSINESS_DATE: "同じ業務日に候補がありません",
  BUSINESS_TIME_MISMATCH: "基準時刻の条件が一致しません",
  INHERITED: "上位のスケジュールを使用",
  SELECTED: "独自ルールを採用",
};
export function NestedScheduleResults({
  data,
}: {
  data: EditorNestedSchedulePreview;
}) {
  return (
    <>
      <p role="status">
        スケジュール上の対象 {data.rows.filter((row) => row.eligible).length}件
        / 判定 {data.rows.length}行・{data.timeZone}（未登録）
      </p>
      <p>
        ルート: {data.rootPath}
        。ルートごとの仮の世代に対する判定です。先行完了・異常発生・保留・停止を含む実開始時刻は予測しません。
      </p>
      {data.rows.some((row) =>
        row.chain.some((step) => step.type === "rn"),
      ) && (
        <p>
          リカバリーネットを含みます。スケジュール上の対象でも、実際の起動には異常時の条件が必要です。
        </p>
      )}
      <table>
        <thead>
          <tr>
            <th>業務日</th>
            <th>ルートのルール・実日時</th>
            <th>子のルール・実日時</th>
            <th>スケジュール上の対象</th>
            <th>理由・階層</th>
          </tr>
        </thead>
        <tbody>
          {data.rows.map((row) => (
            <tr
              key={`${row.businessDate}:${row.root?.rule ?? "none"}:${row.root?.due ?? "none"}`}
            >
              <td>{row.businessDate}</td>
              <td>
                {row.root
                  ? `#${row.root.rule} ${formatDate(row.root.due, data.timeZone)}`
                  : "予定なし"}
              </td>
              <td>
                {row.selected
                  ? `#${row.selected.rule} ${formatDate(row.selected.due, data.timeZone)}`
                  : "—"}
              </td>
              <td>{row.eligible ? "対象" : "対象外"}</td>
              <td>
                {reasons[row.reason] ?? row.reason}
                <ScheduleChain chain={row.chain} timeZone={data.timeZone} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

function ScheduleChain({
  chain,
  timeZone,
}: {
  chain: EditorNestedScheduleStep[];
  timeZone: string;
}) {
  const [open, setOpen] = useState(false);
  return (
    <details onToggle={(event) => setOpen(event.currentTarget.open)}>
      <summary>階層ごとの判定</summary>
      {open && (
        <ol>
          {chain.map((step) => (
            <li key={step.path}>
              {step.path}{" "}
              {step.point
                ? `#${step.point.rule} ${formatDate(step.point.due, timeZone)}`
                : "予定なし"}{" "}
              — {reasons[step.reason] ?? step.reason}
              {step.type === "rn" && "（異常時のみ）"}
            </li>
          ))}
        </ol>
      )}
    </details>
  );
}
