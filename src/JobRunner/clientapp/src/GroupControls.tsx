import { useState } from "react";
import useAspidaSWR from "@aspida/swr";
import type { Operation } from "./api/@types";
import { client, errorMessage } from "./client";

export function GroupControls({
  selected,
  onSelect,
}: {
  selected: string;
  onSelect: (id: string) => void;
}) {
  const groups = useAspidaSWR(client.ui.groups, { refreshInterval: 1500 });
  const runtime = useAspidaSWR(client.ui.runtime, { refreshInterval: 1500 });
  const [request, setRequest] = useState<Operation | undefined>(() => {
    try {
      return (
        JSON.parse(
          sessionStorage.getItem("jobrunner-group-operation") || "null",
        ) ?? undefined
      );
    } catch {
      return undefined;
    }
  });
  const [message, setMessage] = useState("");
  const [sending, setSending] = useState(false);
  const result = useAspidaSWR(client.operations._id(request?.id || "_"), {
    enabled: !!request,
    refreshInterval: (data) => (data?.status === "Pending" || !data ? 1000 : 0),
  });
  const pending =
    !!request && !["Completed", "Rejected"].includes(result.data?.status || "");
  const group = groups.data?.find((g) => g.id === selected);
  async function send(value: Operation) {
    sessionStorage.setItem("jobrunner-group-operation", JSON.stringify(value));
    setRequest(value);
    setMessage("");
    setSending(true);
    try {
      await client.operations.$post({ body: value });
      await result.mutate();
      await groups.mutate();
    } catch (error) {
      setMessage(await errorMessage(error));
    } finally {
      setSending(false);
    }
  }
  return (
    <section className="runtime-control" aria-label="グループの実行制御">
      <label>
        ジョブグループ{" "}
        <select
          aria-label="運用グループ"
          value={group?.id ?? ""}
          onChange={(e) => onSelect(e.target.value)}
        >
          <option value="">ジョブルート（全体）</option>
          {groups.data?.map((g) => (
            <option key={g.id} value={g.id}>
              {g.path}
              {g.retired ? "（旧世代の所属）" : ""}
            </option>
          ))}
        </select>
      </label>
      {group && (
        <>
          <strong>
            {group.pausedBy.length
              ? `停止中（${group.pausedBy.map((p) => (p === "/" ? "全体" : p)).join("、")}）`
              : "予定実行可能"}
          </strong>
          <button
            disabled={
              pending ||
              !runtime.data ||
              !!runtime.data.pending.length ||
              !!groups.error
            }
            onClick={() => {
              if (
                !window.confirm(
                  group.control.paused
                    ? `${group.path} の予定実行を再開しますか？過去の未開始ルート予定は見送ります。全体・親グループが停止中なら開始しません。`
                    : `${group.path} と配下の新規開始を停止しますか？実行中のプロセスは継続します。`,
                )
              )
                return;
              void send({
                id: crypto.randomUUID(),
                kind: group.control.paused ? "ResumeGroup" : "PauseGroup",
                target: group.id,
                expectedControlRevision: group.control.revision,
                expectedDefinitionRevision: group.definitionRevision,
              } as Operation);
            }}
          >
            {group.control.paused
              ? "グループの予定実行を再開"
              : "グループを一時停止"}
          </button>
        </>
      )}
      {(groups.error || runtime.error) && (
        <span role="alert">運用状態を取得できません</span>
      )}
      {pending && <span role="status">グループ操作の結果を確認中…</span>}
      {result.data?.status === "Rejected" && (
        <span role="alert">{result.data.result}</span>
      )}
      {message && <span role="alert">{message}</span>}
      {pending && (
        <span>
          {result.error && (
            <span role="alert">
              受付結果を取得できません。同じ操作IDで再確認できます。
            </span>
          )}
          <button
            disabled={sending}
            onClick={() => request && void send(request)}
          >
            同じ操作IDで再照会・再送
          </button>
        </span>
      )}
    </section>
  );
}
