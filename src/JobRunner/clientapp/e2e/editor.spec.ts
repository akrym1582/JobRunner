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

test("別タブの保存を古い下書きで上書きしない", async ({ page, context }) => {
  await openEditor(page);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  const other = await context.newPage();
  await openEditor(other);
  for (const tab of [page, other])
    await tab
      .getByRole("button", { name: "編集 在庫チェック", exact: true })
      .click();
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf first-tab");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await other
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf stale-tab");
  await expect(other.getByRole("alert")).toContainText("別のタブ");
  await expect(
    other.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf stale-tab");
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf first-tab");
  // 競合後の破棄も、他方の保存を上書きしてはいけません。
  other.once("dialog", (dialog) => dialog.accept());
  await other
    .getByRole("button", { name: "下書きを破棄", exact: true })
    .click();
  await expect(
    other.getByRole("button", { name: "下書きを破棄", exact: true }),
  ).toBeEnabled();
  await expect(
    other.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf stale-tab");
  await expect(
    other.getByRole("button", { name: "保存を再試行", exact: true }),
  ).toHaveCount(0);
  other.once("dialog", (dialog) => dialog.accept());
  await other
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await other
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await expect(
    other.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf first-tab");
  await other
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf resolved");
  await expect(
    other.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await other.reload();
  await other.getByRole("button", { name: "定義編集", exact: true }).click();
  await other
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await expect(
    other.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf resolved");
});

test("破棄の遅い再取得が新しい編集を消さない", async ({ page }) => {
  await openEditor(page);
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf before-discard");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let arrived!: () => void;
  const received = new Promise<void>((resolve) => {
    arrived = resolve;
  });
  await page.route("**/ui/definition-editor", async (route) => {
    const response = await route.fetch();
    arrived();
    await gate;
    await route.fulfill({ response });
  });
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByRole("button", { name: "下書きを破棄", exact: true }).click();
  await received;
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf after-discard-start");
  release();
  await expect(
    page.getByRole("button", { name: "下書きを破棄", exact: true }),
  ).toBeEnabled();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf after-discard-start");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.unroute("**/ui/definition-editor");
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf after-discard-start");
});

test("旧形式の下書きを読み、連続編集と座標上限を保存・出力できる", async ({
  page,
  request,
}) => {
  await page.goto("/");
  const base = await (await request.get(api + "/ui/definition-editor")).json();
  await page.evaluate(async (base) => {
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open("jobrunner-definition-editor", 1);
      request.onupgradeneeded = () =>
        request.result.createObjectStore("drafts");
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result;
        // 旧版と同じ接続終了処理を持つタブが残っている状態から更新します。
        db.onversionchange = () => db.close();
        const tx = db.transaction("drafts", "readwrite");
        const draft = structuredClone(base.draft);
        const node = draft.networks
          .flatMap(
            (n: {
              nodes: { name: string; attributes: Record<string, string[]> }[];
            }) => n.nodes,
          )
          .find((n: { name: string }) => n.name === "在庫チェック");
        node.attributes.te = ["printf legacy"];
        tx.objectStore("drafts").put({ version: 1, base, draft }, "current");
        tx.oncomplete = () => resolve();
        tx.onabort = () => {
          db.close();
          reject(tx.error);
        };
      };
    });
  }, base);
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  // 旧版タブが再接続して無条件保存することも防ぎます。
  expect(
    await page.evaluate(
      () =>
        new Promise<string>((resolve) => {
          const request = indexedDB.open("jobrunner-definition-editor", 1);
          request.onerror = () => resolve(request.error?.name ?? "unknown");
          request.onsuccess = () => {
            request.result.close();
            resolve("opened");
          };
        }),
    ),
  ).toBe("VersionError");
  const command = page.getByRole("textbox", { name: "コマンド", exact: true });
  await expect(command).toHaveValue("printf legacy");
  await command.fill("");
  await command.pressSequentially("printf sequential");
  await page.getByLabel("水平 H").fill("16000");
  await page.getByLabel("垂直 V").fill("10000");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  // 端の座標に移動したノードは図の外にあるので一覧から選びます。
  await page
    .locator(".editor-unit-list")
    .getByRole("button", { name: /在庫チェック/ })
    .click();
  await expect(command).toHaveValue("printf sequential");
  await expect(page.getByLabel("水平 H")).toHaveValue("16000");
  await expect(page.getByLabel("垂直 V")).toHaveValue("10000");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
  await expect(page.getByLabel("変更後の原文")).toContainText("+16000 +10000");
  await expect(page.getByLabel("変更後の原文")).toContainText("sz=100*100;");
});

test("破棄も保存版を比較し、読み直した後は通常どおり破棄できる", async ({
  page,
  context,
}) => {
  await openEditor(page);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  const original = await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .inputValue();
  const other = await context.newPage();
  await openEditor(other);
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf newer-tab");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  // ローカル編集をしていない古いタブから破棄を試みます。
  other.once("dialog", (dialog) => dialog.accept());
  await other
    .getByRole("button", { name: "下書きを破棄", exact: true })
    .click();
  await expect(other.getByRole("alert")).toContainText("別のタブ");
  other.once("dialog", (dialog) => dialog.accept());
  await other
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await other
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await expect(
    other.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf newer-tab");
  other.once("dialog", (dialog) => dialog.accept());
  await other
    .getByRole("button", { name: "下書きを破棄", exact: true })
    .click();
  await expect(
    other.getByRole("button", { name: "下書きを破棄", exact: true }),
  ).toBeEnabled();
  await other.reload();
  await other.getByRole("button", { name: "定義編集", exact: true }).click();
  await other
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await expect(
    other.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue(original);
});
