import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
const api = "http://127.0.0.1:5181/api/jobrunner";
const text =
  '/* 原文 */\r\nunit=local;{ty=n;sd=0,ud;el=job,j,+80 +48;unit=job;{ty=j;te="true";}}\r\n';
const payload = (name = "local.ajs", buffer = Buffer.from(text)) => ({
  name,
  mimeType: "application/octet-stream",
  buffer,
});
async function open(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
}
async function load(page: Page, accept = true) {
  page.once("dialog", (d) => (accept ? d.accept() : d.dismiss()));
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
}
async function preview(page: Page) {
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
}
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}

test("ローカル原文のBOMと文字コードを保ち、編集ZIPを再度開ける", async ({
  page,
  request,
}, info) => {
  const active = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await open(page);
  const bytes = Buffer.from(
    "\ufeff" + text.replace("sd=0,ud;", 'sd=0,ud;jc="/business/cal";'),
    "utf16le",
  );
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles([
      payload("local.ajs", bytes),
      payload("cal.ajs", Buffer.from("unit=cal;{ty=g;cl=su;}")),
    ]);
  await page.getByLabel("文字コード 1", { exact: true }).selectOption("utf-16");
  await page.getByLabel("論理配置先 2", { exact: true }).fill("/business");
  await load(page, false);
  await expect(page.getByLabel("編集ネット")).not.toHaveValue("/local");
  await load(page);
  await expect(page.getByLabel("編集ネット")).toHaveValue("/local");
  await expect(
    page.getByText("ローカル入力を基にした下書き・2ファイル", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "編集 job", exact: true }).click();
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("echo changed");
  await preview(page);
  if (process.env.SCREENSHOTS)
    await page.screenshot({
      path: resolve(
        "../../../docs/spec/scheduler/web-ui/images/local-files-editor.png",
      ),
      fullPage: true,
    });
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("local.zip");
  await (await download).saveAs(path);
  const zip = await readFile(path);
  const restored = await request.post(api + "/ui/definition-editor/local", {
    data: { zipBase64: zip.toString("base64") },
  });
  expect(restored.ok()).toBeTruthy();
  const document = await restored.json();
  const source = document.draft.localSources.find(
    (s: { mapping: { blobPath: string } }) =>
      s.mapping.blobPath === "local.ajs",
  );
  expect(Buffer.from(source.base64, "base64")).toEqual(
    Buffer.from(
      bytes.toString("utf16le").replace("true", "echo changed"),
      "utf16le",
    ),
  );
  expect(source.mapping.encoding).toBe("utf-16");
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(payload("local.zip", zip));
  await load(page);
  await expect(page.getByLabel("編集ネット")).toHaveValue("/local");
  await preview(page);
  await expect(page.getByLabel("変更後の原文")).toContainText("echo changed");
  expect(
    (await (await request.get(api + "/ui/definition-editor")).json()).draft
      .definitionHash,
  ).toBe(active.draft.definitionHash);
});

test("単一原文の無変更出力とブラウザー復元は採用定義の取得に依存しない", async ({
  page,
}, info) => {
  await open(page);
  const original = Buffer.from("\ufeff" + text);
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(payload("local.ajs", original));
  await load(page);
  await expect(page.getByLabel("編集ネット")).toHaveValue("/local");
  await saved(page);
  await page.route("**/ui/definition-editor", (route) => route.abort());
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/local");
  await expect(page.getByText(/採用定義が更新されています/)).toHaveCount(0);
  await preview(page);
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("exact.ajs");
  await (await download).saveAs(path);
  expect(await readFile(path)).toEqual(original);
});

test("危険な対応・重複・参照不足・サイズ超過を拒否して現在の下書きを保つ", async ({
  page,
}) => {
  await open(page);
  const original = await page.getByLabel("編集ネット").inputValue();
  await page
    .getByLabel("ネットのコメント", { exact: true })
    .fill("保持する編集");
  await saved(page);
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(payload());
  await page
    .getByLabel("元ファイルの対応 1", { exact: true })
    .fill("../bad.ajs");
  await load(page);
  await expect(
    page.getByRole("status").filter({ hasText: "安全な相対パス" }),
  ).toBeVisible();
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles([payload(), payload()]);
  await load(page);
  await expect(
    page.getByRole("status").filter({ hasText: "対応が重複" }),
  ).toBeVisible();
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(
      payload("refs.ajs", Buffer.from('unit=local;{ty=n;jc="/missing";}')),
    );
  await load(page);
  await expect(
    page.getByRole("status").filter({ hasText: /カレンダー|参照/ }),
  ).toBeVisible();
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(payload("large.ajs", Buffer.alloc(2 * 1024 * 1024 + 1)));
  await load(page);
  await expect(
    page.getByRole("status").filter({ hasText: "各2MiB" }),
  ).toBeVisible();
  await expect(page.getByLabel("編集ネット")).toHaveValue(original);
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("保持する編集");
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("保持する編集");
});

test("遅いローカル読込がその間の編集を消さない", async ({ page }) => {
  await open(page);
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(payload());
  let started!: () => void;
  let release!: () => void;
  const received = new Promise<void>((resolve) => {
    started = resolve;
  });
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(
    "**/definition-editor/local",
    async (route) => {
      const response = await route.fetch();
      started();
      await gate;
      await route.fulfill({ response });
    },
    { times: 1 },
  );
  await load(page);
  await received;
  await page
    .getByLabel("ネットのコメント", { exact: true })
    .fill("読込中の編集");
  release();
  await expect(
    page.getByText(
      "読込中に編集されたため、ローカル定義への置き換えを中止しました。",
      { exact: true },
    ),
  ).toBeVisible();
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("読込中の編集");
  await saved(page);
});

test("ローカル入力の置換も別タブの保存版を比較する", async ({
  page,
  context,
}) => {
  await open(page);
  await saved(page);
  const second = await context.newPage();
  await open(second);
  await saved(second);
  await page.getByLabel("ネットのコメント", { exact: true }).fill("勝者の編集");
  await saved(page);
  await second
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(payload());
  await load(second);
  await expect(second.getByRole("alert")).toContainText("別のタブ");
  await expect(second.getByLabel("編集ネット")).not.toHaveValue("/local");
  second.once("dialog", (d) => d.accept());
  await second
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await expect(
    second.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("勝者の編集");
  await second.close();
});

test("保存容量エラーでもローカル入力が未保存の編集を置き換えない", async ({
  page,
}) => {
  await open(page);
  await saved(page);
  const original = await page.getByLabel("編集ネット").inputValue();
  await page.evaluate(() => {
    IDBObjectStore.prototype.put = function () {
      throw new DOMException("test quota exceeded", "QuotaExceededError");
    };
  });
  await page
    .getByLabel("ネットのコメント", { exact: true })
    .fill("未保存でも保持");
  await expect(page.getByRole("alert")).toContainText("QuotaExceededError");
  await page
    .getByLabel("定義ファイル", { exact: true })
    .setInputFiles(payload());
  await load(page);
  await expect(
    page.getByRole("status").filter({ hasText: "test quota exceeded" }),
  ).toBeVisible();
  await expect(page.getByLabel("編集ネット")).toHaveValue(original);
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("未保存でも保持");
  await preview(page);
  await expect(page.getByLabel("変更後の原文")).toContainText("未保存でも保持");
});
