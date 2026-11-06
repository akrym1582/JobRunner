import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
const api = "http://127.0.0.1:5181/api/jobrunner";
async function openEditor(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
}
async function shot(page: Page, name: string) {
  if (!process.env.SCREENSHOTS) return;
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({
    path: resolve(
      "../../..",
      "docs/spec/scheduler/web-ui/images",
      `${name}.png`,
    ),
    fullPage: true,
    animations: "disabled",
  });
}

test("SVG編集・接続・undo/redo・下書き復元・実ファイル再解析", async ({
  page,
  request,
}, testInfo) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  const active = await (await request.get(api + "/ui/jobnets")).json();
  await openEditor(page);
  await page
    .getByRole("button", { name: "編集 受注データ集計", exact: true })
    .click();
  await page.getByLabel("ジョブ名", { exact: true }).fill("受注集計");
  await page
    .getByLabel("コメント", { exact: true })
    .fill("受注データを集計して後続へ渡す");
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill('printf "日本語#テスト"');
  const target = page.getByRole("button", {
    name: "編集 受注集計",
    exact: true,
  });
  const box = (await target.boundingBox())!;
  const step = await page
    .locator("svg.editor-graph")
    .evaluate((svg) => (svg as SVGSVGElement).getScreenCTM()!.d * 144);
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2 + step, {
    steps: 8,
  });
  await page.mouse.up();
  await expect(page.getByLabel("垂直 V")).toHaveValue("144");
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(page.getByLabel("垂直 V")).toHaveValue("48");
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(page.getByLabel("垂直 V")).toHaveValue("144");
  await page.getByRole("button", { name: "ジョブ追加", exact: true }).click();
  await page.getByLabel("ジョブ名", { exact: true }).fill("終了確認");
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf complete");
  await page.getByLabel("水平 H").fill("560");
  await page.getByLabel("垂直 V").fill("144");
  await page.getByRole("button", { name: "接続", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 結果通知", exact: true })
    .click();
  await page
    .getByRole("button", { name: "編集 終了確認", exact: true })
    .click();
  await expect(page.locator(".editor-connection-list > div")).toHaveCount(5);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "編集 終了確認", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "編集 受注集計", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue('printf "日本語#テスト"');
  await expect(page.getByLabel("垂直 V")).toHaveValue("144");
  await shot(page, "definition-editor");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "検証OK — ダウンロードできます" }),
  ).toBeVisible();
  await expect(page.getByLabel("変更後の原文")).toContainText(
    'te="printf #"日本語##テスト#"";',
  );
  await shot(page, "definition-preview");
  const downloaded = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const file = await downloaded;
  expect(file.suggestedFilename()).toBe("jobs");
  const path = testInfo.outputPath("edited.ajs");
  await file.saveAs(path);
  const bytes = await readFile(path);
  const parsed = await request.post(api + "/definitions/validate", {
    data: [
      {
        mapping: {
          kind: "Unit",
          targetPath: "/",
          blobPath: "ui-definitions/jobs",
          encoding: "utf-8",
        },
        base64: bytes.toString("base64"),
      },
    ],
  });
  expect(parsed.ok()).toBeTruthy();
  const bundle = await parsed.json();
  const net = bundle.units.find(
    (u: { path: string }) => u.path === "/受注処理",
  );
  expect(
    net.children.find((u: { name: string }) => u.name === "受注集計").attributes
      .te,
  ).toEqual(['printf "日本語#テスト"']);
  expect(net.attributes.ar).toContain("(f=結果通知,t=終了確認,seq)");
  expect(
    (await (await request.get(api + "/ui/jobnets")).json()).definitionHash,
  ).toBe(active.definitionHash);
  await page.getByRole("button", { name: "ジョブネット", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "受注データ集計 定義", exact: true }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});

test("座標修正・子ネット編集・関連接続削除・循環の拒否", async ({ page }) => {
  await openEditor(page);
  await page.getByLabel("編集ネット").selectOption("/座標なし");
  await expect(page.getByRole("alert")).toContainText("表示座標");
  await page.locator(".editor-unit-list button").click();
  await page.getByLabel("水平 H").fill("80");
  await expect(
    page.getByRole("button", { name: "編集 ジョブ", exact: true }),
  ).toBeVisible();
  await page.getByLabel("編集ネット").selectOption("/受注処理/受注伝票作成");
  await expect(page.locator(".editor-node")).toHaveCount(2);
  await page.getByRole("button", { name: "接続", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 帳票出力", exact: true })
    .click();
  await page
    .getByRole("button", { name: "編集 番号採番", exact: true })
    .click();
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText("循環");
  await expect(
    page.getByRole("button", { name: "ダウンロード", exact: true }),
  ).toBeDisabled();
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 帳票出力", exact: true })
    .click();
  page.once("dialog", async (dialog) => {
    expect(dialog.message()).toContain("関連する接続 1 件");
    await dialog.accept();
  });
  await page.getByRole("button", { name: "ジョブを削除", exact: true }).click();
  await expect(page.locator(".editor-node")).toHaveCount(1);
  await expect(page.locator(".editor-connection-list > div")).toHaveCount(0);
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "ダウンロード", exact: true }),
  ).toBeEnabled();
});

test("古い検証応答を無視し、採用版が変わっても下書きを保持", async ({
  page,
}) => {
  await openEditor(page);
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let arrived!: () => void;
  const received = new Promise<void>((resolve) => {
    arrived = resolve;
  });
  await page.route("**/ui/definition-editor/preview", async (route) => {
    const response = await route.fetch();
    arrived();
    await gate;
    await route.fulfill({ response });
  });
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await received;
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf newer");
  const response = page.waitForResponse("**/ui/definition-editor/preview");
  release();
  await response;
  await expect(
    page.getByRole("button", { name: "検証・プレビュー", exact: true }),
  ).toBeEnabled();
  await expect(
    page.getByRole("button", { name: "ダウンロード", exact: true }),
  ).toBeDisabled();
  await expect(page.getByRole("region", { name: "検証結果" })).toHaveCount(0);
  await page.route("**/ui/jobnets", async (route) => {
    const response = await route.fetch();
    await route.fulfill({
      json: { ...(await response.json()), definitionHash: "a".repeat(64) },
    });
  });
  await page.getByRole("button", { name: "再取得", exact: true }).click();
  await expect(page.getByText(/採用定義が更新されています/)).toBeVisible();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf newer");
});

test("ブラウザー保存失敗を隠さず表示する", async ({ page }) => {
  await page.addInitScript(() => {
    IDBObjectStore.prototype.put = () => {
      throw new DOMException("quota exceeded", "QuotaExceededError");
    };
  });
  await openEditor(page);
  await expect(page.getByRole("alert")).toContainText("自動保存できません");
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf edited");
  await expect(page.getByRole("alert")).toContainText("quota exceeded");
});
