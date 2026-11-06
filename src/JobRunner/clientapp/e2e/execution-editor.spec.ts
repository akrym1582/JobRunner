import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import type { EditorDocument } from "../src/api/@types";

const api = "http://127.0.0.1:5181/api/jobrunner";
const text =
  '/* 保持 */\r\nunit=local;{ty=n;sd=0,ud;ha=y;sy=1,C30;el=job,j,+80 +48;unit=job;{ty=j;te="true";ha=y;jd=cod;wth=1;tho=5;etm=10;ex="local";}}\r\n';
async function open(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
}
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}
async function load(page: Page) {
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "execution.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(text),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/local");
  await saved(page);
}
async function field(
  page: Page,
  label: string,
  value?: string,
  choice = false,
) {
  const toggle = page.getByRole("checkbox", {
    name: label + "を指定",
    exact: true,
  });
  if (value === undefined) await toggle.uncheck();
  else {
    await toggle.check();
    const input = page.getByLabel(label, { exact: true });
    if (choice) await input.selectOption(value);
    else await input.fill(value);
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

test("実行属性の省略・明示・境界・保存・出力を確認する", async ({
  page,
  request,
}, info) => {
  const active = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await open(page);
  await load(page);
  await page.getByText("実行属性", { exact: true }).click();
  await field(page, "保留 (ha)", "n", true);
  await field(page, "多重起動 (mp)", "y", true);
  await field(page, "開始遅延監視 (sy)", "1,C30\n144,47:59");
  await field(page, "終了遅延監視 (ey)", "1,C2879");
  await field(page, "終了遅延監視の所要時間（分） (fd)", "2879");
  await page.getByLabel("実行属性の対象").selectOption("/local/job");
  await field(page, "保留 (ha)");
  await field(page, "終了判定 (jd)", "nm", true);
  await field(page, "警告しきい値 (wth)", "2147483647");
  await field(page, "異常しきい値 (tho)", "2147483647");
  await field(page, "実行打ち切り時間（分） (etm)", "1441");
  await preview(page, false);
  await field(page, "実行打ち切り時間（分） (etm)", "1440");
  await field(page, "ローカル実行先の対応付け名 (ex)", "remote");
  await preview(page, false);
  await field(page, "ローカル実行先の対応付け名 (ex)", "local");
  await preview(page);
  await saved(page);
  if (process.env.SCREENSHOTS)
    await page.screenshot({
      path: "../../../docs/spec/scheduler/web-ui/images/execution-editor.png",
      fullPage: true,
    });
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("実行属性", { exact: true }).click();
  await page.getByLabel("実行属性の対象").selectOption("/local/job");
  await expect(
    page.getByLabel("実行打ち切り時間（分） (etm)", { exact: true }),
  ).toHaveValue("1440");
  await expect(
    page.getByRole("checkbox", { name: "保留 (ha)を指定", exact: true }),
  ).not.toBeChecked();
  await preview(page);
  const pending = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("execution.ajs");
  await (await pending).saveAs(path);
  const bytes = await readFile(path);
  const response = await request.post(api + "/ui/definition-editor/local", {
    data: {
      sources: [
        {
          mapping: {
            kind: "Unit",
            blobPath: "execution.ajs",
            targetPath: "/",
            encoding: "utf-8",
          },
          base64: bytes.toString("base64"),
        },
      ],
    },
  });
  expect(response.ok()).toBeTruthy();
  const restored: EditorDocument = await response.json();
  const job = restored.draft.execution!.find(
    (e) => e.id === "/local/job",
  )!.attributes;
  expect(job).toEqual({
    jd: ["nm"],
    wth: ["2147483647"],
    tho: ["2147483647"],
    etm: ["1440"],
    ex: ["local"],
  });
  expect(
    restored.draft.execution!.find((e) => e.id === "/local")!.attributes.sy,
  ).toEqual(["1,C30", "144,47:59"]);
  expect(bytes.toString()).toContain("/* 保持 */\r\n");
  expect(
    (await (await request.get(api + "/ui/definition-editor")).json()).draft
      .definitionHash,
  ).toBe(active.draft.definitionHash);
});

test("新規ルートは保留ありで作成し、解除・undo・redo・削除を扱える", async ({
  page,
}) => {
  await open(page);
  await page.getByText("ルートネットを作成", { exact: true }).click();
  await page.getByLabel("新規ルート名", { exact: true }).fill("保留解除確認");
  await page
    .getByRole("button", { name: "ルートネットを追加", exact: true })
    .click();
  await page.getByText("実行属性", { exact: true }).click();
  await expect(page.getByLabel("保留 (ha)", { exact: true })).toHaveValue("y");
  await field(page, "保留 (ha)", "n", true);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(page.getByLabel("保留 (ha)", { exact: true })).toHaveValue("y");
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(page.getByLabel("保留 (ha)", { exact: true })).toHaveValue("n");
  await preview(page);
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "このネットを削除", exact: true })
    .click();
  await preview(page);
});

test("版4のローカル下書きを原文から移行し、別タブの保存と競合しても上書きしない", async ({
  page,
  context,
  request,
}) => {
  const response = await request.post(api + "/ui/definition-editor/local", {
    data: {
      sources: [
        {
          mapping: {
            kind: "Unit",
            blobPath: "old.ajs",
            targetPath: "/",
            encoding: "utf-8",
          },
          base64: Buffer.from(text).toString("base64"),
        },
      ],
    },
  });
  const document: EditorDocument = await response.json();
  await page.goto("/");
  await page.evaluate(async (document) => {
    const base = structuredClone(document);
    base.draft.editorVersion = 4;
    delete base.draft.execution;
    const draft = structuredClone(base.draft);
    draft.networks[0].nodes[0].attributes.te = ["echo legacy"];
    draft.schedules![0].attributes.sd = ["1,2026/11/6"];
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open("jobrunner-definition-editor", 2);
      request.onupgradeneeded = () =>
        request.result.createObjectStore("drafts");
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result,
          tx = db.transaction("drafts", "readwrite");
        tx.objectStore("drafts").put(
          { version: 1, revision: "old-v4", base, draft },
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
  await page.route("**/definition-editor?definitionHash=*", (route) =>
    route.abort(),
  );
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("実行属性", { exact: true }).click();
  await expect(page.getByLabel("保留 (ha)", { exact: true })).toHaveValue("y");
  await saved(page);
  const other = await context.newPage();
  await open(other);
  await other.getByText("実行属性", { exact: true }).click();
  await saved(other);
  await field(page, "保留 (ha)", "n", true);
  await saved(page);
  await field(other, "多重起動 (mp)", "y", true);
  await expect(other.getByRole("alert")).toContainText("別のタブ");
  await expect(other.getByLabel("多重起動 (mp)", { exact: true })).toHaveValue(
    "y",
  );
  other.once("dialog", (d) => d.accept());
  await other
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await expect(other.getByLabel("保留 (ha)", { exact: true })).toHaveValue("n");
  await expect(
    other.getByRole("checkbox", { name: "多重起動 (mp)を指定", exact: true }),
  ).not.toBeChecked();
  await preview(other);
  await expect(other.getByText(/echo legacy/).first()).toBeVisible();
  await other.close();
});
