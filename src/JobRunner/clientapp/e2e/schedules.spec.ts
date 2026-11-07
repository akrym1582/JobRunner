import { test, expect, type Page, type Locator } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import type { EditorDocument } from "../src/api/@types";

test("試算中に条件AからBへ変え、応答後にAへ戻しても再試算できる", async ({
  page,
}) => {
  await open(page);
  await dates(page);
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
  const button = page.getByRole("button", {
    name: "下書きを試算",
    exact: true,
  });
  await button.click();
  await received;
  await page
    .getByLabel("試算終了業務日（含まない）", { exact: true })
    .fill("2024-03-02");
  const response = page.waitForResponse("**/definition-editor/schedules");
  release();
  await (await response).finished();
  await expect(page.getByText(/下書き候補/)).toHaveCount(0);
  await page
    .getByLabel("試算終了業務日（含まない）", { exact: true })
    .fill("2024-03-01");
  await expect(button).toBeEnabled();
  await button.click();
  await expect(page.getByText(/下書き候補 .*件/)).toBeVisible();
});

const api = "http://127.0.0.1:5181/api/jobrunner";
async function open(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
}
async function field(scope: Page | Locator, label: string, value?: string) {
  const toggle = scope.getByRole("checkbox", {
    name: label + "を指定",
    exact: true,
  });
  if (value === undefined) await toggle.uncheck();
  else {
    await toggle.check();
    await scope.getByRole("textbox", { name: label, exact: true }).fill(value);
  }
}
async function preview(page: Page, valid = true) {
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: valid ? /検証OK/ : /検証エラー/ }),
  ).toBeVisible();
}
async function dates(page: Page) {
  await page.getByLabel("仮の登録基準日", { exact: true }).fill("2024-02-01");
  await page.getByLabel("試算開始業務日", { exact: true }).fill("2024-02-29");
  await page
    .getByLabel("試算終了業務日（含まない）", { exact: true })
    .fill("2024-03-01");
}
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}

test("複数ルールと参照先の継承を編集し、下書きだけを試算・出力・再解析する", async ({
  page,
  request,
}, info) => {
  const original: EditorDocument = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await open(page);
  await field(page, "開始日 (sd)", "7,2024/2/29\n2,2024/2/29\n3,2024/3/1");
  await field(page, "開始時刻 (st)", "7,06:00\n2,+00:00\n3,06:00");
  await field(page, "処理周期 (cy)");
  await field(page, "休業日の振替 (sh)", "7,ca\n2,ca\n3,ca");
  await field(page, "参照カレンダー (jc)", "/運用暦/継承暦");
  await page
    .getByLabel("編集するカレンダー", { exact: true })
    .selectOption("/運用暦");
  await field(page, "運用日 (op)", "2024/2/29");
  await field(page, "休業日 (cl)", "sa\nsu\n2024/2/29");
  await dates(page);
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  const trial = page.getByRole("region", {
    name: "下書き予定試算",
    exact: true,
  });
  await expect(trial).toContainText("下書き候補 1件");
  await expect(trial.locator("tbody tr")).toHaveCount(1);
  await expect(trial.locator("tbody tr")).toContainText("2024-02-29");
  await expect(trial.locator("tbody td").first()).toHaveText("2");
  await expect(trial.locator("tbody tr")).toContainText("06:00");
  await field(page, "運用日 (op)");
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  await expect(trial).toContainText("下書き候補 0件");
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  await expect(trial).toContainText("下書き候補 1件");
  if (process.env.SCREENSHOTS) {
    await page.locator(".editor-timing").screenshot({
      path: resolve(
        "../../../docs/spec/scheduler/web-ui/images/schedule-editor.png",
      ),
    });
  }
  await preview(page);
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("schedule.ajs");
  await (await download).saveAs(path);
  const bytes = await readFile(path);
  const result = await request.post(api + "/definitions/validate", {
    data: [
      {
        mapping: {
          kind: "Unit",
          blobPath: original.sources[0].fileName,
          targetPath: original.sources[0].targetPath,
          encoding: original.sources[0].encoding,
        },
        base64: bytes.toString("base64"),
      },
    ],
  });
  expect(result.ok()).toBeTruthy();
  const parsed = await result.json();
  expect(parsed.units[0].attributes.sd).toEqual([
    "7,2024/2/29",
    "2,2024/2/29",
    "3,2024/3/1",
  ]);
  expect(parsed.units[0].attributes.cy).toBeUndefined();
  expect(bytes.toString("utf8")).toContain(
    'unit=在庫チェック;{ty=j;te="printf checked";}',
  );
  expect(
    (await (await request.get(api + "/ui/definition-editor")).json()).draft
      .definitionHash,
  ).toBe(original.draft.definitionHash);
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("7,2024/2/29\n2,2024/2/29\n3,2024/3/1");
});

test("ネストの独立予定と上位ルールを保持し、不正値・継承の競合を診断する", async ({
  page,
}) => {
  await open(page);
  await page
    .getByLabel("編集ネット")
    .selectOption({ label: "/受注処理/受注伝票作成" });
  await field(page, "上位予定への依存 (de)", "n");
  await field(page, "開始日 (sd)", "9,en");
  await field(page, "開始時刻 (st)", "9,+01:00");
  await field(page, "上位ルール参照 (ln)", "9,2");
  await preview(page);
  await expect(
    page.getByRole("button", { name: "下書きを試算", exact: true }),
  ).toBeEnabled();
  await field(page, "上位予定への依存 (de)");
  await preview(page, false);
  await expect(
    page.getByRole("region", { name: "検証結果", exact: true }),
  ).toContainText("de=n");
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await preview(page);
  await field(page, "上位ルール参照 (ln)", "9,145");
  await preview(page, false);
  await field(page, "上位ルール参照 (ln)", "9,2");
  await field(page, "基準日 (sdd)", "29");
  await preview(page, false);
  await expect(
    page.getByRole("region", { name: "検証結果", exact: true }),
  ).toContainText("現在未対応");
  await field(page, "基準日 (sdd)");
  await page.getByLabel("編集ネット").selectOption({ label: "/受注処理" });
  await page.getByLabel("ネット名", { exact: true }).fill("改名後");
  await page
    .getByLabel("編集ネット")
    .selectOption({ label: "/改名後/受注伝票作成" });
  await expect(
    page.getByRole("textbox", { name: "上位ルール参照 (ln)", exact: true }),
  ).toHaveValue("9,2");
  await preview(page);
});

test("旧版3の新規ネットと改名を維持して予定投影を補完する", async ({
  page,
  request,
}) => {
  const document: EditorDocument = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await page.goto("/");
  await page.evaluate(async (document) => {
    const base = structuredClone(document);
    base.draft.editorVersion = 3;
    delete base.draft.schedules;
    delete base.draft.calendars;
    const draft = structuredClone(base.draft);
    draft.roots![0].node.name = "旧版改名";
    draft.roots!.push({
      node: {
        id: "new:legacy",
        name: "旧版新規",
        type: "n",
        attributes: {},
        x: null,
        y: null,
      },
      sourceIndex: 0,
      parentPath: "/",
    });
    draft.networks.push({ path: "new:legacy", nodes: [], edges: [] });
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open("jobrunner-definition-editor", 2);
      request.onupgradeneeded = () =>
        request.result.createObjectStore("drafts");
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result,
          tx = db.transaction("drafts", "readwrite");
        tx.objectStore("drafts").put(
          { version: 1, revision: "v3", base, draft },
          "current",
        );
        tx.oncomplete = () => {
          db.close();
          resolve();
        };
        tx.onabort = () => {
          db.close();
          reject(tx.error);
        };
      };
    });
  }, document);
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByLabel("編集ネット").selectOption({ label: "/旧版新規" });
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("0,ud");
  await page.getByLabel("編集ネット").selectOption({ label: "/旧版改名" });
  await expect(
    page.getByRole("textbox", { name: "開始時刻 (st)", exact: true }),
  ).toHaveValue("1,23:59");
  await preview(page);
  await saved(page);
});

test("予定とカレンダーの別タブ競合を無言で上書きしない", async ({
  page,
  context,
}) => {
  await open(page);
  await saved(page);
  const second = await context.newPage();
  await open(second);
  await field(page, "開始時刻 (st)", "1,09:00");
  await field(page, "月区分 (md)", "ne");
  await saved(page);
  await field(second, "開始時刻 (st)", "1,10:00");
  await expect(
    second.getByText(/別のタブで下書きが更新されました/).first(),
  ).toBeVisible();
  await expect(
    second.getByRole("textbox", { name: "開始時刻 (st)", exact: true }),
  ).toHaveValue("1,10:00");
  second.once("dialog", (dialog) => dialog.accept());
  await second
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await expect(
    second.getByRole("textbox", { name: "開始時刻 (st)", exact: true }),
  ).toHaveValue("1,09:00");
  await expect(
    second.getByRole("textbox", { name: "月区分 (md)", exact: true }),
  ).toHaveValue("ne");
});

test("遅い試算応答を編集後に表示せず、条件変更でも古い結果を消す", async ({
  page,
}) => {
  await open(page);
  await field(page, "開始日 (sd)", "2024/2/29");
  await field(page, "開始時刻 (st)", "09:00");
  await field(page, "処理周期 (cy)");
  await dates(page);
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
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  await received;
  await field(page, "開始時刻 (st)", "10:00");
  release();
  await expect(page.getByText(/下書き候補/)).toHaveCount(0);
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  const trial = page.getByRole("region", {
    name: "下書き予定試算",
    exact: true,
  });
  await expect(trial).toContainText("下書き候補 1件");
  await expect(trial.locator("tbody tr")).toContainText("10:00");
  await page
    .getByLabel("試算終了業務日（含まない）", { exact: true })
    .fill("2024-03-02");
  await expect(page.getByText(/下書き候補/)).toHaveCount(0);
  await field(page, "開始時刻 (st)", "");
  await preview(page, false);
  await field(page, "開始時刻 (st)");
  await preview(page);
});
