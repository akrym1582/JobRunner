import { useState } from "react";
import useAspidaSWR from "@aspida/swr";
import type { Operation } from "./api/@types";
import { client, errorMessage } from "./client";

export function RuntimeControls() {
  const runtime = useAspidaSWR(client.ui.runtime, { refreshInterval: 1500 });
  const [request, setRequest] = useState<Operation | undefined>(() => {
    try {
      return (
        JSON.parse(
          sessionStorage.getItem("jobrunner-runtime-operation") || "null",
        ) ?? undefined
      );
    } catch {
      return undefined;
    }
  });
  const [message, setMessage] = useState("");
  const result = useAspidaSWR(client.operations._id(request?.id || "_"), {
    enabled: !!request,
    refreshInterval: (data) => (data?.status === "Pending" || !data ? 1000 : 0),
  });
  const pending =
    !!request && !["Completed", "Rejected"].includes(result.data?.status || "");
  const paused = runtime.data?.control.paused;
  async function send(value: Operation) {
    sessionStorage.setItem(
      "jobrunner-runtime-operation",
      JSON.stringify(value),
    );
    setRequest(value);
    setMessage("");
    try {
      await client.operations.$post({ body: value });
      await result.mutate();
      await runtime.mutate();
    } catch (error) {
      setMessage(await errorMessage(error));
    }
  }
  return (
    <section className="runtime-control" aria-label="全体の実行制御">
      <strong>
        {runtime.error
          ? "運用状態を取得できません"
          : !runtime.data
            ? "状態確認中…"
            : runtime.data.pending.length > 0
              ? "運用変更を適用中"
              : !runtime.data.active
                ? "定義未登録・停止中"
                : paused
                  ? "全体停止中"
                  : "実行可能"}
      </strong>
      <button
        disabled={
          !runtime.data ||
          pending ||
          !!runtime.data.pending.length ||
          (paused && !runtime.data.active)
        }
        onClick={() => {
          if (!runtime.data) return;
          if (
            !window.confirm(
              paused
                ? "ジョブ実行を再開しますか？ 再開時刻より前の未開始ルートは、保留分も見送ります。開始済みネットの後続は再開します。"
                : "全ジョブの新規開始を停止しますか？ 起動済みプロセスは終了まで動作し、結果を回収します。",
            )
          )
            return;
          void send({
            id: crypto.randomUUID(),
            kind: paused ? "ResumeAll" : "PauseAll",
            target: "",
            expectedControlRevision: runtime.data.control.revision,
          } as Operation);
        }}
      >
        {paused ? "全体を再開" : "全体を一時停止"}
      </button>
      {pending && (
        <span role="status">受付・適用結果を確認中… {request?.id}</span>
      )}
      {result.data?.status === "Completed" && (
        <span role="status">
          {result.data.result === "Paused"
            ? "全体停止を適用済み"
            : "再開を適用済み"}
        </span>
      )}
      {result.data?.status === "Rejected" && (
        <span role="alert">{result.data.result}</span>
      )}
      {message && (
        <span role="alert">
          {message}{" "}
          <button onClick={() => request && void send(request)}>
            同じ操作IDで再照会・再送
          </button>
        </span>
      )}
    </section>
  );
}
