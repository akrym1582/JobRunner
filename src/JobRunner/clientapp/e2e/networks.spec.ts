import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import type { EditorDocument } from "../src/api/@types";
const api = "http://127.0.0.1:5181/api/jobrunner";
async function open(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
}
async function pick(page: Page, label: string) {
  await page.getByLabel("編集ネット").selectOption({ label });
}
async function preview(page: Page) {
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
}
async function root(page: Page, name: string) {
  const details = page
    .locator("details")
    .filter({ hasText: "ルートネットを作成" });
  if (!(await details.evaluate((e) => (e as HTMLDetailsElement).open)))
    await page.getByText("ルートネットを作成", { exact: true }).click();
  await page.getByLabel("新規ルート名", { exact: true }).fill(name);
  await page
    .getByRole("button", { name: "ルートネットを追加", exact: true })
    .click();
  await expect(page.getByLabel("ネット名", { exact: true })).toHaveValue(name);
}
async function child(page: Page, name: string, kind = "n") {
  await page.getByLabel("追加するジョブ種別").selectOption(kind);
  await page.getByRole("button", { name: "子ネット追加", exact: true }).click();
  await page.getByLabel("子ネット名", { exact: true }).fill(name);
  await page
    .getByRole("button", { name: "子ネットを編集", exact: true })
    .click();
}
async function job(page: Page) {
  await page.getByLabel("追加するジョブ種別").selectOption("j");
  await page.getByRole("button", { name: "ジョブ追加", exact: true }).click();
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf nested-edit");
}
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}

test("新規ルート・子・孫を編集し、改名後の階層を再取り込みできる", async ({
  page,
  request,
}, info) => {
  const original: EditorDocument = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await open(page);
  await root(page, "新規親");
  await child(page, "新規子");
  await child(page, "孫");
  await job(page);
  await pick(page, "/新規親");
  await page.getByLabel("ネット名", { exact: true }).fill("親改名");
  await pick(page, "/親改名/新規子");
  await page.getByLabel("ネット名", { exact: true }).fill("子改名");
  await page.getByLabel("ネットのコメント", { exact: true }).fill("説明を保持");
  await pick(page, "/親改名/子改名/孫");
  await page.getByRole("button", { name: "編集 ジョブ1", exact: true }).click();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf nested-edit");
  await preview(page);
  if (process.env.SCREENSHOTS) {
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({
      path: resolve(
        "../../../docs/spec/scheduler/web-ui/images/network-editor.png",
      ),
      fullPage: true,
    });
  }
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("networks.ajs");
  await (await download).saveAs(path);
  const bytes = await readFile(path);
  const response = await request.post(api + "/definitions/validate", {
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
  expect(response.ok()).toBeTruthy();
  const parsed = await response.json();
  const created = parsed.units.find(
    (u: { name: string }) => u.name === "親改名",
  );
  expect(created.attributes.sd).toEqual(["0,ud"]);
  expect(created.attributes.ha).toEqual(["y"]);
  expect(created.children[0].path).toBe("/親改名/子改名");
  expect(created.children[0].children[0].children[0].attributes.te).toEqual([
    "printf nested-edit",
  ]);
  expect(
    bytes.toString("utf8").startsWith(original.sources[0].text),
  ).toBeTruthy();
  expect(
    (await (await request.get(api + "/ui/definition-editor")).json()).draft
      .definitionHash,
  ).toBe(original.draft.definitionHash);
  await saved(page);
  await page.getByRole("button", { name: "ジョブネット", exact: true }).click();
  await expect(page.locator(".job-node")).toHaveCount(5);
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("ネット名", { exact: true })).toHaveValue("孫");
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await pick(page, "/親改名/子改名");
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("説明を保持");
});

test("既存親子の改名・削除とundo/redoで接続・子の編集を保持する", async ({
  page,
}) => {
  await open(page);
  await pick(page, "/受注処理/受注伝票作成");
  await page
    .getByRole("button", { name: "編集 番号採番", exact: true })
    .click();
  await page
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf keep-child");
  await page.getByLabel("ネット名", { exact: true }).fill("伝票改名");
  await pick(page, "/受注処理");
  await page.getByLabel("ネット名", { exact: true }).fill("親変更");
  await preview(page);
  await expect(page.getByLabel("変更後の原文")).toContainText("t=伝票改名,seq");
  await page
    .getByRole("button", { name: "編集 伝票改名", exact: true })
    .click();
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "子ネットを削除", exact: true })
    .click();
  await expect(
    page
      .getByLabel("編集ネット")
      .locator("option")
      .filter({ hasText: "/親変更/伝票改名" }),
  ).toHaveCount(0);
  await preview(page);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await pick(page, "/親変更/伝票改名");
  await page
    .getByRole("button", { name: "編集 番号採番", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf keep-child");
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await pick(page, "/親変更");
  await preview(page);
});

test("子リカバリーネットを作成し先行へ接続する", async ({ page }) => {
  await open(page);
  await pick(page, "/受注処理");
  await child(page, "復旧ネット", "rn");
  await job(page);
  await pick(page, "/受注処理");
  await page.getByRole("button", { name: "接続", exact: true }).click();
  await page.getByLabel("接続種別").selectOption("normal");
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click();
  await page
    .getByRole("button", { name: "編集 復旧ネット", exact: true })
    .click();
  await preview(page);
  await expect(page.getByLabel("変更後の原文")).toContainText(
    /unit="復旧ネット";\{[^{}]*ty=rn;/,
  );
  await expect(page.getByLabel("変更後の原文")).toContainText(
    "t=復旧ネット,seq",
  );
});

test("全ネット削除後も空ファイルを出力せずルートを作り直せる", async ({
  page,
}) => {
  await open(page);
  while (await page.getByLabel("編集ネット").locator("option").count()) {
    page.once("dialog", (d) => d.accept());
    await page
      .getByRole("button", { name: "このネットを削除", exact: true })
      .click();
  }
  await expect(
    page.getByText("ネットがありません。ルートネットを作成できます。"),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText("定義が空");
  await expect(
    page.getByRole("button", { name: "ダウンロード", exact: true }),
  ).toBeDisabled();
  await root(page, "置換ネット");
  await preview(page);
});

test("版2の判定編集済み下書きに階層を補い既存編集を保つ", async ({
  page,
  request,
}) => {
  const document: EditorDocument = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await page.goto("/");
  await page.evaluate(async (document) => {
    const base = structuredClone(document);
    base.draft.editorVersion = 2;
    delete base.draft.roots;
    delete base.containers;
    const draft = structuredClone(base.draft);
    draft.networks
      .find((n) => n.path === "/判定検証")!
      .nodes.find((n) => n.type === "jdj")!.attributes.ejl = ["3"];
    draft.networks.find(
      (n) => n.path === "/受注処理/受注伝票作成",
    )!.nodes[0].attributes.te = ["printf version-two"];
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open("jobrunner-definition-editor", 2);
      request.onupgradeneeded = () =>
        request.result.createObjectStore("drafts");
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result,
          tx = db.transaction("drafts", "readwrite");
        tx.objectStore("drafts").put(
          { version: 1, revision: "v2", base, draft },
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
  await pick(page, "/判定検証");
  await page
    .getByRole("button", { name: "編集 既存判定", exact: true })
    .click();
  await expect(page.getByLabel("判定値の下限")).toHaveValue("3");
  await pick(page, "/受注処理");
  await page.getByLabel("ネット名", { exact: true }).fill("移行後");
  await pick(page, "/移行後/受注伝票作成");
  await page
    .getByRole("button", { name: "編集 番号採番", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf version-two");
  await preview(page);
  await saved(page);
});

test("ネット作成でも別タブの古い階層を保存せず明示的に読み直せる", async ({
  page,
  context,
}) => {
  await open(page);
  await saved(page);
  const other = await context.newPage();
  await open(other);
  await saved(other);
  await root(page, "先に保存した親");
  await saved(page);
  await root(other, "競合した親");
  await expect(other.getByRole("alert")).toContainText("別のタブ");
  await expect(other.getByLabel("ネット名", { exact: true })).toHaveValue(
    "競合した親",
  );
  await preview(other);
  other.once("dialog", (d) => d.accept());
  await other
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await pick(other, "/先に保存した親");
  await expect(
    other
      .getByLabel("編集ネット")
      .locator("option")
      .filter({ hasText: "/競合した親" }),
  ).toHaveCount(0);
  await expect(other.getByRole("alert")).toHaveCount(0);
});
