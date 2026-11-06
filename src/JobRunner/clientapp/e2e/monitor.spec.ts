import {
  test,
  expect,
  type Page,
  type APIRequestContext,
} from "@playwright/test";
import { resolve } from "node:path";
const api = "http://127.0.0.1:5181/api/jobrunner";
async function shot(page: Page, name: string) {
  if (!process.env.SCREENSHOTS) return;
  await page.evaluate(() => document.fonts.ready);
  if (name !== "monthly-schedule")
    await expect(page.locator("svg.graph")).toHaveAttribute(
      "data-ready",
      "true",
    );
  await page.screenshot({
    path: resolve(
      "../../..",
      "docs/spec/scheduler/web-ui/images",
      name + ".png",
    ),
    fullPage: true,
    animations: "disabled",
  });
}
async function operation(
  request: APIRequestContext,
  body: Record<string, unknown>,
) {
  const id = crypto.randomUUID();
  expect(
    (await request.post(api + "/operations", { data: { id, ...body } })).ok(),
  ).toBeTruthy();
  await expect
    .poll(
      async () =>
        (await (await request.get(api + "/operations/" + id)).json()).status,
    )
    .toBe("Completed");
  return id;
}
test("実APIで図・階層・予定操作・再送・通信復旧を検証", async ({
  page,
  request,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await operation(request, {
    kind: "Register",
    target: "/受注処理",
    fixedDue: "2030-06-18T00:00:00Z",
    registrationKind: "Confirmed",
  });
  await page.goto("/");
  await expect(
    page.getByRole("heading", { name: "ジョブネット監視" }),
  ).toBeVisible();
  await expect(page.locator(".job-node")).toHaveCount(5);
  await shot(page, "jobnet-overview");
  await page
    .getByRole("button", { name: "受注伝票作成 定義", exact: true })
    .click();
  await page.getByRole("button", { name: "子ネットを開く" }).click();
  await expect(page.locator(".job-node")).toHaveCount(2);
  await shot(page, "nested-jobnet");
  await page
    .locator(".breadcrumb")
    .getByRole("button", { name: "受注処理", exact: true })
    .click();
  await page.locator(".execution-link").first().click();
  await expect(
    page.getByRole("button", { name: "日時変更", exact: true }),
  ).toBeEnabled();
  await page
    .getByRole("button", { name: "在庫チェック 先行待ち", exact: true })
    .click();
  await shot(page, "execution-status");
  const camera = page.getByTestId("graph-camera");
  const before = await camera.getAttribute("transform");
  await page.getByRole("button", { name: "拡大", exact: true }).click();
  await expect(camera).not.toHaveAttribute("transform", before!);
  const zoomed = await camera.getAttribute("transform");
  await page.getByRole("button", { name: "再取得", exact: true }).click();
  await expect(camera).toHaveAttribute("transform", zoomed!);
  await page.getByRole("button", { name: "全体表示", exact: true }).click();
  await page.getByRole("button", { name: "日時変更", exact: true }).click();
  await page
    .getByRole("textbox", { name: "変更後の日時" })
    .fill("2030-06-18T12:30:00+09:00");
  await shot(page, "schedule-change");
  await page.getByRole("button", { name: "変更を確定" }).click();
  await expect(page.getByRole("status")).toContainText("操作が完了");
  await expect(page.locator("tr.chosen")).toContainText("12:30:00");
  await shot(page, "schedule-change-result");
  await page.getByRole("button", { name: "予定取消", exact: true }).click();
  await page.getByRole("button", { name: "変更を確定" }).click();
  await expect(page.locator("tr.chosen")).toContainText("取消");
  await page.getByRole("button", { name: "変更解除", exact: true }).click();
  await page.getByRole("button", { name: "変更を確定" }).click();
  await expect(page.locator("tr.chosen")).toContainText("09:00:00");
  const roots = (
    await (
      await request.get(api + "/ui/executions", {
        params: { rootPath: "/受注処理" },
      })
    ).json()
  ).items;
  const target = roots.find((x: { due: string }) =>
    x.due.startsWith("2030-06-18"),
  ).id;
  const id = crypto.randomUUID();
  const payload = { id, kind: "Move", target, due: "2030-06-19T00:00:00Z" };
  expect(
    (await request.post(api + "/operations", { data: payload })).ok(),
  ).toBeTruthy();
  await expect
    .poll(
      async () =>
        (await (await request.get(api + "/operations/" + id)).json()).status,
    )
    .toBe("Completed");
  expect(
    (await request.post(api + "/operations", { data: payload })).status(),
  ).toBe(200);
  expect(
    (
      await request.post(api + "/operations", {
        data: { ...payload, due: "2030-06-20T00:00:00Z" },
      })
    ).status(),
  ).toBe(409);
  await page.route("**/api/jobrunner/ui/graph?**", (route) => route.abort());
  await page.getByRole("button", { name: "再取得", exact: true }).click();
  await expect(page.getByRole("alert")).toBeVisible();
  await page.unroute("**/api/jobrunner/ui/graph?**");
  await page.getByRole("alert").getByRole("button", { name: "再取得" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
  expect(errors).toEqual([]);
});
test("開始済みの変更拒否と状態取得", async ({ request, page }) => {
  await operation(request, {
    kind: "Register",
    target: "/受注処理",
    registrationKind: "Immediate",
    due: new Date().toISOString(),
  });
  let target = "";
  await expect
    .poll(async () => {
      const list = await (
        await request.get(api + "/ui/executions", {
          params: { rootPath: "/受注処理" },
        })
      ).json();
      const done = list.items.find(
        (o: { state: string }) => o.state === "Succeeded",
      );
      target = done?.id || "";
      return !!target;
    })
    .toBe(true);
  const id = crypto.randomUUID();
  await request.post(api + "/operations", {
    data: { id, kind: "Cancel", target },
  });
  await expect
    .poll(
      async () =>
        (await (await request.get(api + "/operations/" + id)).json()).status,
    )
    .toBe("Rejected");
  const graph = await (
    await request.get(api + "/ui/graph", {
      params: { unitPath: "/受注処理", rootId: target },
    })
  ).json();
  expect(graph.canChange).toBe(false);
  expect(
    graph.nodes.some((n: { status: string }) => n.status === "NORMAL"),
  ).toBe(true);
  expect(
    (
      await request.get(api + "/ui/executions", {
        params: { rootPath: "/受注処理", page: -1 },
      })
    ).status(),
  ).toBe(400);
  await page.goto("/");
  await page.getByRole("button", { name: "マンスリー", exact: true }).click();
  await expect(page.locator(".monthly-details")).toContainText("正常終了");
  await expect(
    page.locator(".month-grid .schedule-mark.actual.success").first(),
  ).toBeVisible();
});

test("多ノード・条件分岐・キーボード選択と自動更新", async ({ page }) => {
  const nodes = Array.from({ length: 200 }, (_, i) => ({
    path: `/受注処理/job${i}`,
    name: `長い日本語の検証ジョブ${i}`,
    type: "j",
    isNetwork: false,
    isRecovery: false,
    status: i === 0 ? "UNKNOWN" : "DEFINITION",
    execution: null,
    exitCode: null,
    x: 80 + (i % 20) * 160,
    y: 48 + Math.floor(i / 20) * 96,
  }));
  const edges = nodes.slice(1).map((n, i) => ({
    id: String(i),
    from: nodes[i].path,
    to: n.path,
    kind: i === 0 ? "condition" : "normal",
  }));
  await page.route("**/api/jobrunner/ui/graph?**", (route) =>
    route.fulfill({
      json: {
        unitPath: "/受注処理",
        definitionHash: "test-many-nodes",
        nodes,
        edges,
        root: null,
        canChange: false,
        canRestore: false,
      },
    }),
  );
  await page.goto("/");
  await expect(page.locator(".job-node")).toHaveCount(200);
  const first = page.locator(".job-node").first();
  await first.focus();
  await page.keyboard.press("Enter");
  await expect(page.locator(".detail-panel")).toContainText(
    "長い日本語の検証ジョブ0",
  );
  await expect(page.locator(".detail-panel")).toContainText("結果不明");
  const camera = await page
    .getByTestId("graph-camera")
    .getAttribute("transform");
  // 5秒の自動取得が実際に発生しても図の選択を維持します。
  await page.waitForResponse((r) => r.url().includes("/ui/graph?"));
  await expect(first).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByTestId("graph-camera")).toHaveAttribute(
    "transform",
    camera!,
  );
});

test("受付応答が失われても操作IDで完了を照会する", async ({ page }) => {
  let acceptedId = "";
  await page.route("**/api/jobrunner/operations", async (route) => {
    acceptedId = route.request().postDataJSON().id;
    const response = await route.fetch();
    expect(response.ok()).toBeTruthy();
    await route.abort("connectionreset");
  });
  await page.goto("/");
  await page.locator(".execution-link").first().click();
  await page.getByRole("button", { name: "日時変更", exact: true }).click();
  await page
    .getByRole("textbox", { name: "変更後の日時" })
    .fill("2030-06-21T09:00:00+09:00");
  await page.getByRole("button", { name: "変更を確定" }).click();
  await expect(page.getByRole("status")).toContainText("操作が完了");
  expect(acceptedId).not.toBe("");
  await expect(page.locator("tr.chosen")).toContainText("2030/06/21");
  expect(
    await page.evaluate(() => sessionStorage.getItem("jobrunner-operation")),
  ).toBeNull();
});

test("定義座標がない図はエラーにし、月間一覧は閲覧できる", async ({ page }) => {
  await page.goto("/");
  await page
    .getByRole("navigation", { name: "ジョブネット一覧" })
    .getByRole("button", { name: "座標なし", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText("表示座標がありません");
  await expect(page.locator(".job-node")).toHaveCount(0);
  await page.getByRole("button", { name: "マンスリー", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "マンスリースケジュール" }),
  ).toBeVisible();
  await expect(page.locator(".month-grid")).toContainText("ジョブ");
  await expect(page.getByRole("alert")).toHaveCount(0);
});

test("月間の階層・複数実行・月切替・図への移動", async ({ page, request }) => {
  for (const day of [2, 4, 6, 10, 12, 16, 18, 20, 24, 26, 28, 30])
    await operation(request, {
      kind: "Register",
      target: "/受注処理",
      registrationKind: "Confirmed",
      fixedDue: `2030-06-${String(day).padStart(2, "0")}T00:00:00Z`,
    });
  await operation(request, {
    kind: "Register",
    target: "/受注処理",
    registrationKind: "Confirmed",
    fixedDue: "2030-06-18T04:00:00Z",
  });
  await page.goto("/");
  await page.getByRole("button", { name: "マンスリー", exact: true }).click();
  await page.getByLabel("表示月").fill("2030-06");
  const cell = page.getByRole("button", {
    name: "受注処理 2030-06-18 2件",
    exact: true,
  });
  await expect(cell).toBeVisible();
  await cell.click();
  await expect(page.locator(".monthly-details tbody tr")).toHaveCount(2);
  await expect(page.locator(".monthly-details")).toContainText("09:00:00");
  await expect(page.locator(".monthly-details")).toContainText("13:00:00");
  await shot(page, "monthly-schedule");
  await page
    .getByRole("button", { name: "受注伝票作成を折りたたむ", exact: true })
    .click();
  await expect(page.locator(".month-grid tbody")).not.toContainText("帳票出力");
  await page
    .getByRole("button", { name: "受注伝票作成を展開", exact: true })
    .click();
  await expect(page.locator(".month-grid tbody")).toContainText("帳票出力");
  await page
    .locator(".monthly-details")
    .getByRole("button", { name: "図を開く" })
    .first()
    .click();
  await expect(page.locator("svg.graph")).toHaveAttribute("data-ready", "true");
  await expect(
    page.getByRole("button", { name: "日時変更", exact: true }),
  ).toBeEnabled();
  const graph = await (
    await request.get(api + "/ui/graph", { params: { unitPath: "/受注処理" } })
  ).json();
  const stock = graph.nodes.find(
      (n: { name: string }) => n.name === "在庫チェック",
    ),
    recovery = graph.nodes.find(
      (n: { name: string }) => n.name === "発注伝票作成",
    );
  expect(stock.x).toBe(recovery.x);
  expect(stock.y).toBeLessThan(recovery.y);
  await page.getByRole("button", { name: "マンスリー", exact: true }).click();
  await page.getByLabel("表示月").fill("2030-06");
  const roots = (
    await (
      await request.get(api + "/ui/executions", {
        params: { rootPath: "/受注処理" },
      })
    ).json()
  ).items;
  await operation(request, {
    kind: "Cancel",
    target: roots.find((o: { due: string }) => o.due.startsWith("2030-06-20"))
      .id,
  });
  await page.getByRole("button", { name: "月間予定を再取得" }).click();
  const cancelled = page.getByRole("button", {
    name: "受注処理 2030-06-20 1件",
    exact: true,
  });
  await cancelled.click();
  await expect(page.locator(".monthly-details")).toContainText("取消");
  await expect(cancelled.locator(".schedule-mark")).not.toHaveClass(/actual/);
  await operation(request, {
    kind: "Move",
    target: roots.find((o: { due: string }) => o.due.startsWith("2030-06-30"))
      .id,
    due: "2030-06-30T15:00:00Z",
  });
  await page.getByRole("button", { name: "翌月", exact: true }).click();
  await page
    .getByRole("button", { name: "受注処理 2030-07-01 1件", exact: true })
    .click();
  await expect(page.locator(".monthly-details")).toContainText(
    "2030/07/01 00:00:00",
  );
  await expect(page.locator(".monthly-details")).toContainText("2030-06-30");
  await page.getByLabel("表示月").fill("2028-02");
  await expect(page.locator(".month-grid thead th")).toHaveCount(30);
  await page.getByRole("button", { name: "翌月", exact: true }).click();
  await expect(page.getByLabel("表示月")).toHaveValue("2028-03");
  await expect(page.locator(".month-grid thead th")).toHaveCount(32);
  await page.getByLabel("表示月").fill("2029-12");
  await page.getByRole("button", { name: "翌月", exact: true }).click();
  await expect(page.getByLabel("表示月")).toHaveValue("2030-01");
  await expect(page.locator(".monthly-details")).toContainText(
    "保存済みの予定・実績はありません",
  );
  await page.route("**/api/jobrunner/ui/monthly?**", (route) => route.abort());
  await page.getByRole("button", { name: "月間予定を再取得" }).click();
  await expect(page.getByRole("alert")).toBeVisible();
  await page.unroute("**/api/jobrunner/ui/monthly?**");
  await page.getByRole("alert").getByRole("button", { name: "再取得" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
});
