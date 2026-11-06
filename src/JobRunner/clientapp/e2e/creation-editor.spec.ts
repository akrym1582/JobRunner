import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
const api = "http://127.0.0.1:5181/api/jobrunner/ui/definition-editor";
async function open(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
  await page.getByText("グループ・Unitファイルを作成", { exact: true }).click();
}
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}
async function preview(page: Page) {
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
}
test("空のファイルから親子グループ・ネットを編集しZIPを再取込する", async ({
  page,
  request,
}, info) => {
  const original = await (await request.get(api)).json();
  await open(page);
  await expect(
    page.getByRole("button", { name: "下書きにファイルを追加", exact: true }),
  ).toBeDisabled();
  await page.getByLabel("初期グループ名", { exact: true }).fill("initial");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "新規ローカルファイルで開始", exact: true })
    .click();
  await saved(page);
  await expect(
    page.getByText(
      "ローカル定義を下書きとして読み込みました。採用・登録はしていません。",
      { exact: true },
    ),
  ).toBeVisible();
  await page
    .getByLabel("グループの追加先", { exact: true })
    .selectOption({ label: "new.ajs · /initial" });
  await page.getByLabel("新規グループ名", { exact: true }).fill("child");
  await page
    .getByRole("button", { name: "グループを追加", exact: true })
    .click();
  await expect(
    page
      .getByLabel("グループの追加先")
      .locator("option")
      .filter({ hasText: "/initial/child" }),
  ).toHaveCount(1);
  await saved(page);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page
      .getByLabel("グループの追加先")
      .locator("option")
      .filter({ hasText: "/initial/child" }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await page.getByLabel("新規ファイル名", { exact: true }).fill("added.ajs");
  await page
    .getByLabel("新規ファイルの論理配置先", { exact: true })
    .fill("/initial/child");
  await page
    .getByLabel("新規ファイルの文字コード", { exact: true })
    .selectOption("utf-16BE");
  await page.getByLabel("新規ファイルにBOMを付ける", { exact: true }).check();
  await page.getByLabel("初期グループ名", { exact: true }).fill("extra");
  await page
    .getByRole("button", { name: "下書きにファイルを追加", exact: true })
    .click();
  await expect(
    page
      .getByLabel("グループの追加先")
      .locator("option")
      .filter({ hasText: "added.ajs · /initial/child/extra" }),
  ).toHaveCount(1);
  await page.getByText("ルートネットを作成", { exact: true }).click();
  await page
    .getByLabel("ルートの追加先")
    .selectOption({ label: "added.ajs · /initial/child/extra · utf-16BE" });
  await page.getByLabel("新規ルート名", { exact: true }).fill("newnet");
  await page
    .getByRole("button", { name: "ルートネットを追加", exact: true })
    .click();
  await preview(page);
  await saved(page);
  if (process.env.SCREENSHOTS || process.env.CREATION_SCREENSHOT)
    await page.screenshot({
      path: "../../../docs/spec/scheduler/web-ui/images/creation-editor.png",
      fullPage: true,
    });
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await preview(page);
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe("jobrunner-definitions.zip");
  const path = info.outputPath("output.zip");
  await file.saveAs(path);
  const restored = await (
    await request.post(api + "/local", {
      data: { zipBase64: (await readFile(path)).toString("base64") },
    })
  ).json();
  expect(restored.draft.roots[0].parentPath).toBe("/initial/child/extra");
  expect(
    restored.draft.execution.find(
      (e: { id: string }) => e.id === "/initial/child/extra/newnet",
    ).attributes.ha,
  ).toEqual(["y"]);
  expect(
    restored.draft.localSources.find(
      (s: { mapping: { blobPath: string } }) =>
        s.mapping.blobPath === "added.ajs",
    ).mapping.encoding,
  ).toBe("utf-16BE");
  expect((await (await request.get(api)).json()).draft.definitionHash).toBe(
    original.draft.definitionHash,
  );
});

test("遅い追加検証は新しい編集を上書きせず、別タブの追加も競合を検出する", async ({
  page,
  context,
}) => {
  await open(page);
  let release!: () => void;
  const gate = new Promise<void>((r) => {
    release = r;
  });
  let reached!: () => void;
  const started = new Promise<void>((r) => {
    reached = r;
  });
  await page.route("**/definition-editor/preview", async (route) => {
    const response = await route.fetch();
    reached();
    await gate;
    await route.fulfill({ response });
  });
  await page.getByLabel("新規グループ名", { exact: true }).fill("delayed");
  await page
    .getByRole("button", { name: "グループを追加", exact: true })
    .click();
  await started;
  await page
    .getByLabel("ネットのコメント", { exact: true })
    .fill("keep newer edit");
  release();
  await expect(
    page.getByText(
      "検証中に編集されたため、追加を中止しました。もう一度追加してください。",
      { exact: true },
    ),
  ).toBeVisible();
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("keep newer edit");
  await page.unroute("**/definition-editor/preview");
  await saved(page);
  const other = await context.newPage();
  await open(other);
  await page.getByLabel("新規グループ名", { exact: true }).fill("winner");
  await page
    .getByRole("button", { name: "グループを追加", exact: true })
    .click();
  await expect(
    page.getByText("下書きに追加しました。採用・登録はしていません。", {
      exact: true,
    }),
  ).toBeVisible();
  await saved(page);
  await other.getByLabel("新規グループ名", { exact: true }).fill("loser");
  await other
    .getByRole("button", { name: "グループを追加", exact: true })
    .click();
  await expect(
    other.getByRole("alert").filter({ hasText: /別のタブ/ }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("グループ・Unitファイルを作成", { exact: true }).click();
  await expect(
    page
      .getByLabel("グループの追加先")
      .locator("option")
      .filter({ hasText: "winner" }),
  ).toHaveCount(1);
  await expect(
    page
      .getByLabel("グループの追加先")
      .locator("option")
      .filter({ hasText: "loser" }),
  ).toHaveCount(0);
  await other.close();
});

test("版5の単独ジョブ下書きは省略を保持して移行・出力できる", async ({
  page,
  request,
}, info) => {
  const text =
    '/* unchanged */\r\nunit=solo;{ty=j;ha=y;cm="keep";te="true";}\r\n';
  const document = await (
    await request.post(api + "/local", {
      data: {
        sources: [
          {
            mapping: {
              kind: "Unit",
              blobPath: "solo.ajs",
              targetPath: "/",
              encoding: "utf-8",
            },
            base64: Buffer.from(text).toString("base64"),
          },
        ],
      },
    })
  ).json();
  await page.goto("/");
  await page.evaluate(async (document) => {
    document.draft.editorVersion = 5;
    delete document.draft.newFiles;
    delete document.draft.newGroups;
    for (const e of document.draft.execution) delete e.type;
    const draft = structuredClone(document.draft);
    delete draft.execution[0].attributes.ha;
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open("jobrunner-definition-editor", 2);
      request.onupgradeneeded = () =>
        request.result.createObjectStore("drafts");
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result,
          tx = db.transaction("drafts", "readwrite");
        tx.objectStore("drafts").put(
          { version: 1, revision: "standalone-v5", base: document, draft },
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
  await saved(page);
  await page.getByText("実行属性", { exact: true }).click();
  await expect(page.getByLabel("実行属性の対象")).toHaveValue("/solo");
  await expect(
    page.getByRole("checkbox", { name: "保留 (ha)を指定", exact: true }),
  ).not.toBeChecked();
  await preview(page);
  const downloading = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const file = await downloading,
    path = info.outputPath("solo.ajs");
  await file.saveAs(path);
  expect(await readFile(path, "utf8")).toBe(text.replace("ha=y;", ""));
});
