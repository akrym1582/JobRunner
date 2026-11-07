import { test, expect } from "@playwright/test";
import { readFile } from "node:fs/promises";

const text =
  'unit=business;{ty=g;unit=net;{ty=n;sd=0,ud;unit=job;{ty=j;te="echo original";}}}unit=other;{ty=g;}';

test("グループZIPの追加とundoを保存して再読込できる", async ({
  page,
  request,
}) => {
  const before = await (
    await request.get("http://127.0.0.1:5181/api/jobrunner/ui/runtime")
  ).json();
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "group.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(text),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/business/net");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page
    .getByText("グループのエクスポート・インポート", { exact: true })
    .click();
  await page.getByLabel("入出力グループ").selectOption("/business");
  const downloaded = page.waitForEvent("download");
  await page.getByRole("button", { name: "グループZIPをダウンロード" }).click();
  const zip = await readFile((await (await downloaded).path())!);
  await page.getByLabel("グループZIP", { exact: true }).setInputFiles({
    name: "group.zip",
    mimeType: "application/zip",
    buffer: zip,
  });
  await page.getByLabel("追加グループ名", { exact: true }).fill("copied");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループを下書きへ取り込む" })
    .click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/copied/net"]'),
  ).toHaveCount(1);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/copied/net"]'),
  ).toHaveCount(1);
  // 再読込後は履歴がリセットされるため、同じZIPをもう一度追加してundoの永続化を確認する。
  await page
    .getByText("グループのエクスポート・インポート", { exact: true })
    .click();
  await page.getByLabel("グループZIP", { exact: true }).setInputFiles({
    name: "group.zip",
    mimeType: "application/zip",
    buffer: zip,
  });
  await page.getByLabel("追加グループ名", { exact: true }).fill("undo_group");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループを下書きへ取り込む" })
    .click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/undo_group/net"]'),
  ).toHaveCount(1);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/undo_group/net"]'),
  ).toHaveCount(0);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/undo_group/net"]'),
  ).toHaveCount(0);
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/business/net"]'),
  ).toHaveCount(1);
  const after = await (
    await request.get("http://127.0.0.1:5181/api/jobrunner/ui/runtime")
  ).json();
  expect(after.active).toEqual(before.active);
});

test("親からの停止を表示し、子の再開は版と固定IDを送る", async ({ page }) => {
  await page.route("**/api/jobrunner/ui/groups", (route) =>
    route.fulfill({
      json: [
        {
          id: "parent-id",
          path: "/parent",
          definitionRevision: 5,
          control: { revision: 2, paused: true },
          pausedBy: ["/parent"],
          retired: false,
        },
        {
          id: "child-id",
          path: "/parent/child",
          definitionRevision: 5,
          control: { revision: 3, paused: true },
          pausedBy: ["/parent", "/parent/child"],
          retired: false,
        },
      ],
    }),
  );
  await page.route("**/api/jobrunner/operations", async (route) => {
    const value = route.request().postDataJSON();
    expect(value.kind).toBe("ResumeGroup");
    expect(value.target).toBe("child-id");
    expect(value.expectedControlRevision).toBe(3);
    expect(value.expectedDefinitionRevision).toBe(5);
    await route.fulfill({ status: 202, json: { ...value, status: "Pending" } });
  });
  await page.route("**/api/jobrunner/operations/*", (route) =>
    route.fulfill({ json: { status: "Completed", result: "Running" } }),
  );
  await page.goto("/");
  await page
    .getByLabel("運用グループ", { exact: true })
    .selectOption("child-id");
  const control = page.getByRole("region", { name: "グループの実行制御" });
  await expect(control).toContainText("/parent、/parent/child");
  page.once("dialog", async (d) => {
    expect(d.message()).toContain("親グループが停止中");
    await d.accept();
  });
  const request = page.waitForRequest(
    (r) =>
      r.url().endsWith("/api/jobrunner/operations") && r.method() === "POST",
  );
  await page
    .getByRole("button", { name: "グループの予定実行を再開", exact: true })
    .click();
  await request;
  await expect(control).toContainText("停止中");
});

for (const accepted of [false, true]) {
  test(`通信失敗と再読込後に同じグループ操作を再送できる（受付済み=${accepted}）`, async ({
    page,
  }) => {
    await page.route("**/api/jobrunner/ui/groups", (route) =>
      route.fulfill({
        json: [
          {
            id: "recover-group",
            path: "/recover",
            definitionRevision: 5,
            control: { revision: 2, paused: true },
            pausedBy: ["/recover"],
            retired: false,
          },
        ],
      }),
    );
    const sent: unknown[] = [];
    let recovered = false;
    await page.route("**/api/jobrunner/operations", async (route) => {
      sent.push(route.request().postDataJSON());
      if (sent.length === 1) return route.abort("failed");
      recovered = true;
      await route.fulfill({
        status: 202,
        json: { ...route.request().postDataJSON(), status: "Completed" },
      });
    });
    await page.route("**/api/jobrunner/operations/*", (route) =>
      route.fulfill(
        recovered
          ? { json: { status: "Completed", result: "Running" } }
          : accepted
            ? { json: { status: "Pending" } }
            : { status: 404, json: { message: "Not found" } },
      ),
    );
    await page.goto("/");
    await page
      .getByLabel("運用グループ", { exact: true })
      .selectOption("recover-group");
    page.once("dialog", (d) => d.accept());
    await page
      .getByRole("button", { name: "グループの予定実行を再開", exact: true })
      .click();
    const control = page.getByRole("region", { name: "グループの実行制御" });
    await expect(
      control.getByRole("button", { name: "同じ操作IDで再照会・再送" }),
    ).toBeVisible();
    await page.reload();
    await expect(
      control.getByRole("button", { name: "同じ操作IDで再照会・再送" }),
    ).toBeVisible();
    await control
      .getByRole("button", { name: "同じ操作IDで再照会・再送" })
      .click();
    await expect(
      control.getByText("グループ操作の結果を確認中…", { exact: true }),
    ).toHaveCount(0);
    expect(sent).toHaveLength(2);
    expect(sent[1]).toEqual(sent[0]);
    await page
      .getByLabel("運用グループ", { exact: true })
      .selectOption("recover-group");
    await expect(
      page.getByRole("button", {
        name: "グループの予定実行を再開",
        exact: true,
      }),
    ).toBeEnabled();
  });
}
