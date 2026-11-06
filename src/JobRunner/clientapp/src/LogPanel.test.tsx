import { afterEach, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { SWRConfig } from "swr";
import { LogPanel } from "./LogPanel";
const api = vi.hoisted(() => ({ attempts: vi.fn(), logs: vi.fn() }));
vi.mock("./client", () => ({
  client: {
    ui: {
      attempts: { $path: () => "/attempts", $get: api.attempts },
      logs: { $path: () => "/logs", $get: api.logs },
    },
  },
  errorMessage: async () => "取得失敗",
}));
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.resetAllMocks();
});
it("親画面が毎秒更新されても試行の終了を取得し、本文の追尾を止める", async () => {
  vi.useFakeTimers();
  let state = "Running";
  api.attempts.mockImplementation(async () => ({
    unitPath: "/net/job",
    currentAttemptId: "a",
    archived: false,
    items: [
      {
        id: "a",
        state,
        logsComplete: false,
        startedAt: "2030-01-01T00:00:00Z",
      },
    ],
  }));
  api.logs.mockImplementation(async () => ({
    chunks: [],
    previousCursor: null,
    nextCursor: "after:0",
    hasMore: false,
    logsComplete: false,
    state,
  }));
  const config = { provider: () => new Map(), dedupingInterval: 0 };
  const content = () => (
    <SWRConfig value={config}>
      <LogPanel
        execution={{ id: "job", rootId: "root", unitPath: "/net/job" }}
        zone="UTC"
        onClose={() => {}}
      />
    </SWRConfig>
  );
  const view = render(content());
  await act(async () => {
    await vi.advanceTimersByTimeAsync(0);
  });
  expect(api.attempts).toHaveBeenCalledTimes(1);
  state = "Succeeded";
  for (let i = 0; i < 10; i++) {
    view.rerender(content());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });
  }
  expect(api.attempts.mock.calls.length).toBeGreaterThanOrEqual(3);
  expect(
    screen.getByText("試行終了・自動更新停止", { exact: false }),
  ).toBeTruthy();
  const reads = api.logs.mock.calls.length;
  await act(async () => {
    await vi.advanceTimersByTimeAsync(10000);
  });
  expect(api.logs).toHaveBeenCalledTimes(reads);
});
