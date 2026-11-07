import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
const api = "http://127.0.0.1:5181/api/jobrunner";
async function open(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
}
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}
async function begin(page: Page) {
  await page
    .getByRole("button", { name: "このルールを簡単設定で編集", exact: true })
    .click();
  await page.getByLabel("実行パターン", { exact: true }).selectOption("offset");
}
async function trial(page: Page) {
  await page.getByLabel("仮の登録基準日", { exact: true }).fill("2026-11-01");
  await page.getByLabel("試算開始業務日", { exact: true }).fill("2026-11-01");
  await page
    .getByLabel("試算終了業務日（含まない）", { exact: true })
    .fill("2026-12-01");
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  return page.getByRole("region", { name: "下書き予定試算", exact: true });
}
test("月25日の前後運用日を未適用で試算し、一操作のundo・保存復元・出力再取込ができる", async ({
  page,
  request,
}, info) => {
  const active = await (await request.get(api + "/ui/runtime")).json();
  await open(page);
  await page
    .getByLabel("編集するカレンダー", { exact: true })
    .selectOption("/運用暦");
  await page
    .getByRole("checkbox", { name: "休業日 (cl)を指定", exact: true })
    .check();
  await page
    .getByRole("textbox", { name: "休業日 (cl)", exact: true })
    .fill("sa\nsu\n2026/11/23\n2026/11/26");
  await saved(page);
  await begin(page);
  await page
    .getByLabel("簡単設定のカレンダー", { exact: true })
    .selectOption("/運用暦/継承暦");
  await page.getByLabel("簡単設定の開始時刻", { exact: true }).fill("09:00");
  await expect(page.getByLabel("生成するJP1属性")).toContainText(
    "cftd=1,af,2,10;",
  );
  const preview = await trial(page);
  await expect(preview).toContainText("未適用の簡単設定の試算");
  await expect(preview.locator("tbody tr")).toHaveCount(1);
  await expect(preview.locator("tbody")).toContainText("2026-11-30");
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("1,en");
  await page.getByLabel("起算方向", { exact: true }).selectOption("be");
  await page.getByLabel("起算する運用日数", { exact: true }).fill("5");
  await trial(page);
  await expect(preview.locator("tbody")).toContainText("2026-11-17");
  if (process.env.SIMPLE_SCREENSHOTS) {
    await page
      .getByRole("region", { name: "スケジュール簡単設定", exact: true })
      .screenshot({
        path: resolve(
          "../../../docs/spec/scheduler/web-ui/images/simple-schedule-editor.png",
        ),
      });
    await preview.screenshot({
      path: resolve(
        "../../../docs/spec/scheduler/web-ui/images/simple-schedule-preview.png",
      ),
    });
  }
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("1,25");
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("1,en");
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(
    page.getByRole("textbox", { name: "起算 (cftd)", exact: true }),
  ).toHaveValue("1,be,5,10");
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
  await page
    .getByRole("button", { name: "このルールを簡単設定で編集", exact: true })
    .click();
  await expect(
    page.getByLabel("起算する運用日数", { exact: true }),
  ).toHaveValue("5");
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "簡単設定の入力を破棄", exact: true })
    .click();
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
  const downloading = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("simple.ajs");
  await (await downloading).saveAs(path);
  const bytes = await readFile(path);
  expect(bytes.toString()).toContain(
    'unit=在庫チェック;{ty=j;te="printf checked";}',
  );
  const imported = await request.post(api + "/ui/definition-editor/local", {
    data: {
      sources: [
        {
          mapping: {
            kind: "Unit",
            blobPath: "simple.ajs",
            targetPath: "/",
            encoding: "utf-8",
          },
          base64: bytes.toString("base64"),
        },
      ],
    },
  });
  expect(imported.ok()).toBeTruthy();
  const document = await imported.json();
  expect(
    document.draft.schedules.find((s: { id: string }) => s.id === "/受注処理")
      .attributes.cftd,
  ).toEqual(["1,be,5,10"]);
  expect(
    (await (await request.get(api + "/ui/runtime")).json()).active,
  ).toEqual(active.active);
});

test("未適用入力の破棄を確認し、別編集やネット切替後の古い候補を適用しない", async ({
  page,
}) => {
  await open(page);
  await begin(page);
  page.once("dialog", (dialog) => dialog.dismiss());
  await page.getByRole("button", { name: "ジョブネット", exact: true }).click();
  await expect(page.getByLabel("実行パターン", { exact: true })).toBeVisible();
  await page
    .getByRole("textbox", { name: "開始時刻 (st)", exact: true })
    .fill("1,10:00");
  await expect(
    page.getByRole("button", { name: "簡単設定を下書きへ適用", exact: true }),
  ).toBeDisabled();
  await expect(page.getByText(/古い候補は適用できません/)).toBeVisible();
  await page
    .getByLabel("編集ネット")
    .selectOption({ label: "/受注処理/受注伝票作成" });
  await expect(
    page.getByRole("button", { name: "簡単設定を下書きへ適用", exact: true }),
  ).toBeDisabled();
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "簡単設定の入力を破棄", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "簡単設定でルールを追加", exact: true }),
  ).toHaveCount(0);
});

test("簡単設定でも別タブの保存を無言で上書きしない", async ({
  page,
  context,
}) => {
  await open(page);
  await saved(page);
  const other = await context.newPage();
  await open(other);
  await begin(other);
  await page
    .getByRole("textbox", { name: "開始時刻 (st)", exact: true })
    .fill("1,10:00");
  await saved(page);
  await other
    .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
    .click();
  await expect(
    other.getByText(/別のタブで下書きが更新されました/).first(),
  ).toBeVisible();
  await expect(
    other.getByRole("textbox", { name: "起算 (cftd)", exact: true }),
  ).toHaveValue("1,af,2,10");
  other.once("dialog", (dialog) => dialog.accept());
  await other
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await expect(
    other.getByRole("textbox", { name: "開始時刻 (st)", exact: true }),
  ).toHaveValue("1,10:00");
});

test("未適用候補の遅い応答と不正入力で古い試算を表示しない", async ({
  page,
}) => {
  await open(page);
  await begin(page);
  let release!: () => void;
  let started!: () => void;
  const received = new Promise<void>((resolve) => {
    started = resolve;
  });
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(
    "**/definition-editor/schedules",
    async (route) => {
      const response = await route.fetch();
      started();
      await gate;
      await route.fulfill({ response });
    },
    { times: 1 },
  );
  await trial(page);
  await received;
  await page.getByLabel("起算する運用日数", { exact: true }).fill("0");
  const response = page.waitForResponse("**/definition-editor/schedules");
  release();
  await (await response).finished();
  await expect(page.getByText(/下書き候補 .*件/)).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "下書きを試算", exact: true }),
  ).toBeDisabled();
  await page.getByLabel("起算する運用日数", { exact: true }).fill("2");
  await expect(
    page.getByRole("button", { name: "下書きを試算", exact: true }),
  ).toBeEnabled();
  await trial(page);
  await expect(page.getByText(/下書き候補 .*件/)).toBeVisible();
});

test("予定なしから未使用番号のルールを追加し、暦日と運用日数を自由に指定できる", async ({
  page,
}, info) => {
  await open(page);
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "new.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(
      'unit=n;{ty=n;ha=y;sd=0,ud;sy=1,12:00;ey=2,13:00;unit=j;{ty=j;te="true";}}',
    ),
  });
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/n");
  await page
    .getByRole("button", { name: "簡単設定でルールを追加", exact: true })
    .click();
  await page.getByLabel("毎月の暦日", { exact: true }).fill("20");
  await page.getByLabel("起算方向", { exact: true }).selectOption("be");
  await page.getByLabel("起算する運用日数", { exact: true }).fill("3");
  await expect(page.getByLabel("生成するJP1属性")).toContainText("sd=3,20;");
  await expect(page.getByLabel("生成するJP1属性")).toContainText(
    "cftd=3,be,3,10;",
  );
  page.once("dialog", (dialog) => dialog.dismiss());
  await page
    .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("0,ud");
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("3,20");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("added.ajs");
  await (await download).saveAs(path);
  const text = await readFile(path, "utf8");
  expect(text).toContain("ha=y;");
  expect(text).toContain("sy=1,12:00;");
  expect(text).toContain("ey=2,13:00;");
});
