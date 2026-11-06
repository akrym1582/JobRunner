import {
  test,
  expect,
  type Page,
  type APIRequestContext,
} from "@playwright/test";
import { resolve } from "node:path";
import type {
  Graph,
  AttemptPage,
  LogPage,
  MonthlySchedule,
} from "../src/api/@types";
const api = "http://127.0.0.1:5181/api/jobrunner";
async function operation(
  request: APIRequestContext,
  body: Record<string, unknown>,
) {
  const id = crypto.randomUUID();
  expect(
    (await request.post(`${api}/operations`, { data: { id, ...body } })).ok(),
  ).toBe(true);
  await expect
    .poll(
      async () =>
        (await (await request.get(`${api}/operations/${id}`)).json()).status,
    )
    .toBe("Completed");
}
async function failedJob(request: APIRequestContext) {
  await operation(request, {
    kind: "Register",
    target: "/ログ検証",
    registrationKind: "Immediate",
    due: new Date().toISOString(),
  });
  let graph: Graph;
  await expect
    .poll(async () => {
      const list = await (
        await request.get(`${api}/ui/executions`, {
          params: { rootPath: "/ログ検証" },
        })
      ).json();
      const root = list.items[0];
      if (!root) return false;
      graph = await (
        await request.get(`${api}/ui/graph`, {
          params: { rootId: root.id, unitPath: "/ログ検証" },
        })
      ).json();
      return graph.root?.state === "Failed";
    })
    .toBe(true);
  return graph!;
}
async function openLog(page: Page, graph: Graph) {
  await page.goto("/");
  await page
    .getByRole("navigation", { name: "ジョブネット一覧" })
    .getByRole("button", { name: "ログ検証", exact: true })
    .click();
  await page
    .locator(".execution-link")
    .filter({ hasText: graph.root!.jp1ExecutionId! })
    .click();
  await page.locator(".job-node").filter({ hasText: "出力ジョブ" }).click();
  await page
    .locator(".detail-panel")
    .getByRole("button", { name: "ログを見る" })
    .click();
  return page.getByRole("region", { name: "実行ログ", exact: true });
}
async function screenshot(page: Page, name: string) {
  if (!process.env.SCREENSHOTS) return;
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({
    path: resolve(
      "../../..",
      "docs/spec/scheduler/web-ui/images",
      `${name}.png`,
    ),
    fullPage: (await page.locator(".log-panel.expanded").count()) === 0,
    animations: "disabled",
  });
}
async function filter(page: Page, status: string) {
  const field = page.locator(".status-filter");
  await field.locator("summary").click();
  await field.getByRole("checkbox", { name: status, exact: true }).check();
  await field.locator("summary").click();
}

test("実APIの標準出力・標準エラー・再実行履歴と月間からのログ表示", async ({
  page,
  request,
  context,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const graph = await failedJob(request);
  const job = graph.nodes.find(
    (node) => node.name === "出力ジョブ",
  )!.execution!;
  const query = { rootId: graph.root!.id!, occurrenceId: job.id! };
  const before: AttemptPage = await (
    await request.get(`${api}/ui/attempts`, { params: query })
  ).json();
  const oldId = before.items[0].id!;
  const panel = await openLog(page, graph);
  await expect(panel.getByLabel("ログ本文")).toContainText(
    "日本語 <script>alert(1)</script> 出力完了",
  );
  await expect(panel.getByLabel("ログの試行")).toHaveValue(oldId);
  const oldPid = (await panel.getByLabel("ログ本文").textContent())!.match(
    /PID=\d+/,
  )![0];
  await expect(panel).toContainText("終了コード 1");
  await expect(panel.locator("script")).toHaveCount(0);
  await panel.getByLabel("表示範囲内を検索").fill("日本語");
  await expect(panel.locator("mark")).toHaveText("日本語");
  await expect(panel.getByLabel("自動追尾（5秒）")).not.toBeChecked();
  await panel.getByLabel("表示範囲内を検索").fill("");
  await panel.getByLabel("折り返す").uncheck();
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await panel
    .getByRole("button", { name: "表示範囲をコピー", exact: true })
    .click();
  await expect(panel.getByRole("status")).toHaveText(
    "表示範囲をコピーしました",
  );
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain(
    "出力完了",
  );
  await panel.getByRole("button", { name: "前のログ", exact: true }).click();
  await expect(panel.locator(".log-range")).toContainText("チャンク 1–");
  await panel.getByRole("tab", { name: "標準エラー" }).click();
  await expect(panel.getByLabel("ログ本文")).toHaveText("標準エラーの診断");
  await panel.getByRole("button", { name: "ログを拡大", exact: true }).click();
  await expect(panel).toHaveClass(/expanded/);
  await screenshot(page, "execution-logs");
  await panel.getByRole("button", { name: "ログを縮小", exact: true }).click();
  await operation(request, {
    kind: "Rerun",
    target: job.id,
    rerunMethod: "ONLY",
  });
  let newId = "";
  await expect
    .poll(async () => {
      const attempts: AttemptPage = await (
        await request.get(`${api}/ui/attempts`, { params: query })
      ).json();
      newId = attempts.items[0].id!;
      return attempts.items.length;
    })
    .toBe(2);
  await expect(panel).toContainText("新しい試行があります");
  await expect(panel.getByLabel("ログの試行")).toHaveValue(oldId);
  await panel.getByRole("button", { name: "最新の試行を見る" }).click();
  await expect(panel.getByLabel("ログの試行")).toHaveValue(newId);
  await expect(panel.getByLabel("ログ本文")).toContainText("出力完了");
  await expect(panel.getByLabel("ログ本文")).not.toContainText(oldPid);
  await panel.getByLabel("ログの試行").selectOption(oldId);
  await expect(panel.getByLabel("ログ本文")).toContainText("出力完了");
  await expect(panel.getByLabel("ログ本文")).toContainText(oldPid);
  await page.getByRole("button", { name: "マンスリー", exact: true }).click();
  await expect(panel.getByLabel("ログの試行")).toHaveValue(oldId);
  await panel
    .getByRole("button", { name: "ログを閉じる", exact: true })
    .click();
  const row = page
    .locator(".monthly-details tbody tr")
    .filter({ hasText: "出力ジョブ" })
    .first();
  await row.getByRole("button", { name: "ログを見る" }).click();
  await expect(panel.getByLabel("ログの試行")).toHaveValue(newId);
  await panel.getByRole("tab", { name: "標準エラー" }).click();
  await expect(panel.getByLabel("ログ本文")).toHaveText("標準エラーの診断");
  await panel
    .getByRole("button", { name: "ログを閉じる", exact: true })
    .click();
  await page
    .locator(".monthly-details tbody tr")
    .filter({ hasText: "後続ジョブ" })
    .first()
    .getByRole("button", { name: "ログを見る" })
    .click();
  await expect(panel).toContainText("試行履歴はありません");
  expect(errors).toEqual([]);
});

test("追尾停止・64片上限・復旧・ストリーム切替で遅い応答を混ぜない", async ({
  page,
  request,
}) => {
  const graph = await failedJob(request);
  const job = graph.nodes.find(
    (node) => node.name === "出力ジョブ",
  )!.execution!;
  const history: AttemptPage = await (
    await request.get(`${api}/ui/attempts`, {
      params: { rootId: graph.root!.id!, occurrenceId: job.id! },
    })
  ).json();
  let live = true,
    available = 0,
    fail = false,
    delay = false;
  let logReads = 0;
  await page.route("**/api/jobrunner/ui/attempts?**", (route) =>
    route.fulfill({
      json: {
        ...history,
        items: history.items.map((item) => ({
          ...item,
          state: live ? "Running" : "Succeeded",
          logsComplete: false,
        })),
      },
    }),
  );
  await page.route("**/api/jobrunner/ui/logs?**", async (route) => {
    logReads++;
    if (fail) return route.abort();
    const url = new URL(route.request().url());
    const stderr = url.searchParams.get("stream") === "stderr";
    const cursor = url.searchParams.get("cursor");
    const boundary = cursor ? Number(cursor.split(":")[1]) : available;
    const start = stderr
      ? 0
      : cursor?.startsWith("after:")
        ? boundary + 1
        : Math.max(0, boundary - 16);
    const end = stderr ? 1 : Math.min(available, start + 16);
    const result: LogPage = {
      chunks: Array.from({ length: Math.max(0, end - start) }, (_, i) => ({
        sequence: start + i,
        text: stderr ? "標準エラーだけ" : `保存済みログ ${start + i}\n`,
      })),
      previousCursor: start > 0 ? `before:${start}` : null,
      nextCursor: `after:${Math.max(start, end - 1)}`,
      hasMore: end < available,
      logsComplete: false,
      state: live ? "Running" : "Succeeded",
    };
    if (delay && !stderr)
      await new Promise((resolve) => setTimeout(resolve, 1200));
    await route.fulfill({ json: result });
  });
  const panel = await openLog(page, graph);
  await expect(panel).toContainText("保存済みログがありません");
  await expect(panel).toContainText("ログ保存未完了");
  available = 16;
  await expect(panel.getByLabel("ログ本文")).toContainText("保存済みログ 15");
  available = 96;
  await expect(panel.getByLabel("ログ本文")).toContainText("保存済みログ 95", {
    timeout: 20000,
  });
  await expect(panel.locator(".log-range")).toContainText(
    "チャンク 33–96 · 64/64片",
  );
  await panel.getByLabel("自動追尾（5秒）").uncheck();
  const paused = await panel.getByLabel("ログ本文").textContent();
  available = 100;
  await page.waitForTimeout(5500);
  await expect(panel.getByLabel("ログ本文")).toHaveText(paused!);
  await panel.getByRole("button", { name: "前のログ", exact: true }).click();
  await expect(panel.locator(".log-range")).toContainText(
    "チャンク 17–80 · 64/64片",
  );
  fail = true;
  await panel.getByRole("button", { name: "最新へ", exact: true }).click();
  await expect(panel.getByRole("alert")).toBeVisible();
  fail = false;
  await panel.getByRole("button", { name: "ログを再取得" }).click();
  await expect(panel.getByLabel("ログ本文")).toContainText("保存済みログ 99");
  delay = true;
  const pending = page.waitForRequest(
    (r) => r.url().includes("/ui/logs?") && r.url().includes("stdout"),
  );
  await panel.getByRole("button", { name: "最新へ", exact: true }).click();
  await pending;
  await panel.getByRole("tab", { name: "標準エラー" }).click();
  await expect(panel.getByLabel("ログ本文")).toHaveText("標準エラーだけ");
  await page.waitForTimeout(1500);
  await expect(panel.getByLabel("ログ本文")).toHaveText("標準エラーだけ");
  live = false;
  await expect(panel).toContainText("試行終了・自動更新停止");
  await expect(panel).toContainText("一部欠けている可能性");
  await page.waitForTimeout(1000);
  const stopped = logReads;
  await page.waitForTimeout(5500);
  expect(logReads).toBe(stopped);
  await panel
    .getByRole("button", { name: "ログを閉じる", exact: true })
    .click();
  await expect(panel).toHaveCount(0);
  const closed = logReads;
  await page.waitForTimeout(5500);
  expect(logReads).toBe(closed);
});

test("状態絞り込みは図の接続とログ対象を保持し、月間の件数に一致する", async ({
  page,
  request,
}) => {
  const graph = await failedJob(request);
  const panel = await openLog(page, graph);
  const attempt = await panel.getByLabel("ログの試行").inputValue();
  await filter(page, "異常終了");
  await expect(page.locator(".graph-jobs tbody tr")).toHaveCount(1);
  await expect(page.locator(".job-node")).toHaveCount(2);
  await expect(page.locator(".job-node.dimmed")).toHaveCount(1);
  await expect(page.locator(".graph path[marker-end]")).toHaveCount(1);
  await page
    .locator(".graph-jobs")
    .getByRole("button", { name: "出力ジョブ", exact: true })
    .click();
  await expect(page.locator(".job-node.selected")).toHaveAttribute(
    "aria-label",
    "出力ジョブ 異常終了",
  );
  await screenshot(page, "status-filter");
  await expect(panel.getByLabel("ログの試行")).toHaveValue(attempt);
  await page.getByRole("button", { name: "マンスリー", exact: true }).click();
  const month = await page.getByLabel("表示月").inputValue();
  const monthly: MonthlySchedule = await (
    await request.get(`${api}/ui/monthly`, {
      params: { rootPath: "/ログ検証", month },
    })
  ).json();
  const matching = monthly.entries.filter(
    (entry) => entry.status === "ABNORMAL",
  );
  await expect(page.locator(".monthly-details .count")).toHaveText(
    String(matching.length),
  );
  await expect(page.locator(".month-grid tbody tr")).toHaveCount(2);
  await expect(page.locator(".monthly-details tbody tr")).toHaveCount(
    matching.length,
  );
  await filter(page, "結果不明");
  await expect(page.locator(".monthly-details .count")).toHaveText(
    String(matching.length),
  );
  await page
    .locator(".status-filter")
    .getByRole("button", { name: "すべて", exact: true })
    .click();
  await expect(page.locator(".month-grid tbody tr")).toHaveCount(3);
  await expect(panel.getByLabel("ログの試行")).toHaveValue(attempt);
});
