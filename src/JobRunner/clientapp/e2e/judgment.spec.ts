import {
  test,
  expect,
  type Page,
  type APIRequestContext,
} from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import type { EditorDocument, EditorDraft } from "../src/api/@types";
const api = "http://127.0.0.1:5181/api/jobrunner";
async function open(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByLabel("編集ネット").selectOption("/判定検証");
}
async function select(page: Page, name: string) {
  await page.getByRole("button", { name: `編集 ${name}`, exact: true }).click();
}
async function preview(page: Page) {
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
}
async function connect(
  page: Page,
  from: string,
  to: string,
  conditional = false,
) {
  await page.getByRole("button", { name: "接続", exact: true }).click();
  await page
    .getByLabel("接続種別")
    .selectOption(conditional ? "condition" : "normal");
  await select(page, from);
  await select(page, to);
}
async function added(page: Page, type: string) {
  await page.getByLabel("追加するジョブ種別").selectOption(type);
  await page.getByRole("button", { name: "ジョブ追加", exact: true }).click();
  return await page.getByLabel("ジョブ名", { exact: true }).inputValue();
}

test("既存判定の範囲・保留・文字列と数値境界を編集し原文を再解析する", async ({
  page,
  request,
}, testInfo) => {
  const original: EditorDocument = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await open(page);
  await select(page, "既存判定");
  await expect(page.getByLabel("判定方式")).toHaveValue("ri");
  await page.getByLabel("判定値の下限").fill("2");
  await page.getByLabel("判定値の上限").fill("8");
  await page.getByLabel("判定ジョブの保留").selectOption("y");
  await preview(page);
  await expect(page.getByLabel("変更後の原文")).toContainText(
    "ejl=2;ejh=8;ejm=gt;eju=le;ha=y;",
  );
  if (process.env.SCREENSHOTS)
    await page.screenshot({
      path: resolve(
        "../../../docs/spec/scheduler/web-ui/images/judgment-editor.png",
      ),
      fullPage: true,
    });
  await page.getByLabel("判定方式").selectOption("eq");
  await page.getByLabel("判定値", { exact: true }).fill("4294967296");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText("判定数値が範囲外");
  await expect(
    page.getByRole("button", { name: "ダウンロード", exact: true }),
  ).toBeDisabled();
  await page.getByLabel("判定値", { exact: true }).fill("4294967295");
  await preview(page);
  page.once("dialog", (dialog) => dialog.dismiss());
  await page.getByLabel("判定方式").selectOption("ef");
  await expect(page.getByLabel("判定方式")).toHaveValue("eq");
  page.once("dialog", (dialog) => dialog.accept());
  await page.getByLabel("判定方式").selectOption("ef");
  await page.getByLabel("判定ファイル").fill("/tmp/not-evaluated-by-editor");
  await preview(page);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(page.getByLabel("判定方式")).toHaveValue("eq");
  await expect(page.getByLabel("判定値", { exact: true })).toHaveValue(
    "4294967295",
  );
  await page.getByLabel("判定方式").selectOption("sce");
  await page.getByLabel("判定変数名").fill("?AJS2RESULT?");
  await page.getByLabel("文字列の判定値").fill('日本語"#;{}');
  await preview(page);
  await page.getByLabel("文字列の判定値").fill("");
  await preview(page);
  const downloaded = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const file = testInfo.outputPath("judgment.ajs");
  await (await downloaded).saveAs(file);
  const bytes = await readFile(file);
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
  const root = (await result.json()).units.find(
    (n: { path: string }) => n.path === "/判定検証",
  );
  const judge = root.children.find(
    (n: { name: string }) => n.name === "既存判定",
  );
  expect(judge.attributes.ejt).toEqual([""]);
  expect(judge.attributes.ejl).toEqual(["2"]);
  expect(judge.attributes.ha).toEqual(["y"]);
  expect(judge.ownerName).toBe("owner");
  expect(
    (await (await request.get(api + "/ui/definition-editor")).json()).draft
      .definitionHash,
  ).toBe(original.draft.definitionHash);
});

for (const type of ["jdj", "rjdj"])
  test(`${type}を追加して接続・名前変更・削除とundo/redoができる`, async ({
    page,
  }) => {
    await open(page);
    let predecessor = "先行";
    if (type === "rjdj") {
      predecessor = await added(page, "rj");
      await connect(page, "先行", predecessor);
    }
    const condition = await added(page, type);
    await page.getByLabel("ジョブ名", { exact: true }).fill("追加判定");
    await expect(
      page.getByRole("textbox", { name: "コマンド", exact: true }),
    ).toHaveCount(0);
    expect(condition).toBe("判定1");
    const dependent = await added(page, type === "rjdj" ? "rp" : "j");
    await connect(page, predecessor, "追加判定");
    await connect(page, "追加判定", dependent, true);
    await preview(page);
    await expect(page.getByLabel("変更後の原文")).toContainText(`ty=${type};`);
    await expect(page.getByLabel("変更後の原文")).toContainText(
      `t=${dependent},con)`,
    );
    await select(page, "追加判定");
    page.once("dialog", (dialog) => dialog.accept());
    await page
      .getByRole("button", { name: "ジョブを削除", exact: true })
      .click();
    await expect(
      page.getByRole("button", { name: "編集 追加判定", exact: true }),
    ).toHaveCount(0);
    await page.getByRole("button", { name: "元に戻す", exact: true }).click();
    await select(page, "追加判定");
    await expect(page.getByLabel("判定方式")).toHaveValue("gt");
    await preview(page);
    await page.getByRole("button", { name: "やり直す", exact: true }).click();
    await expect(
      page.getByRole("button", { name: "編集 追加判定", exact: true }),
    ).toHaveCount(0);
    await preview(page);
  });

async function seedLegacy(page: Page, request: APIRequestContext) {
  const document: EditorDocument = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  await page.goto("/");
  await page.evaluate(async (document) => {
    const old = (value: EditorDraft) => {
      delete (value as Partial<EditorDraft>).editorVersion;
      delete value.roots;
      for (const network of value.networks)
        for (const node of network.nodes)
          if (["jdj", "rjdj"].includes(node.type))
            node.attributes = Object.fromEntries(
              Object.entries(node.attributes).filter(([key]) => key === "cm"),
            );
    };
    const base = structuredClone(document);
    old(base.draft);
    const draft = structuredClone(base.draft);
    const network = draft.networks.find((n) => n.path === "/判定検証")!;
    network.nodes.find((n) => n.name === "先行")!.attributes.te = [
      "printf legacy-edit",
    ];
    network.nodes.find((n) => n.name === "既存判定")!.x = 560;
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open("jobrunner-definition-editor", 2);
      request.onupgradeneeded = () =>
        request.result.createObjectStore("drafts");
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result;
        const tx = db.transaction("drafts", "readwrite");
        tx.objectStore("drafts").put(
          { version: 1, revision: "legacy-revision", base, draft },
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
  return document.draft.definitionHash;
}

test("旧下書きは元版の判定属性を補い、コマンド編集と座標を保つ", async ({
  page,
  request,
}) => {
  const hash = await seedLegacy(page, request);
  const loaded: string[] = [];
  await page.route("**/ui/definition-editor*", async (route) => {
    const response = await route.fetch();
    const url = new URL(route.request().url());
    if (url.searchParams.has("definitionHash")) {
      loaded.push(url.searchParams.get("definitionHash")!);
      await route.fulfill({ response });
    } else {
      const document = await response.json();
      document.draft.definitionHash = "a".repeat(64);
      await route.fulfill({ json: document });
    }
  });
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByLabel("編集ネット").selectOption("/判定検証");
  await select(page, "既存判定");
  await expect(page.getByLabel("判定方式")).toHaveValue("ri");
  await expect(page.getByLabel("判定値の下限")).toHaveValue("1");
  await expect(page.getByLabel("水平 H")).toHaveValue("560");
  await select(page, "先行");
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf legacy-edit");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  expect(loaded).toEqual([hash]);
  await preview(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByLabel("編集ネット").selectOption("/判定検証");
  await select(page, "既存判定");
  await expect(page.getByLabel("判定値の上限")).toHaveValue("9");
});

test("旧下書きの原文取得失敗でも編集を保持し、読み直しで復旧する", async ({
  page,
  request,
}) => {
  await seedLegacy(page, request);
  await page.route("**/ui/definition-editor?*", (route) =>
    route.fulfill({ status: 503, json: { title: "original unavailable" } }),
  );
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText(
    "旧下書きの編集情報を読み込めません",
  );
  await page.getByLabel("編集ネット").selectOption("/判定検証");
  await select(page, "先行");
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf legacy-edit");
  await select(page, "既存判定");
  await expect(page.getByLabel("判定方式")).toHaveCount(0);
  await page.unroute("**/ui/definition-editor?*");
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await select(page, "既存判定");
  await expect(page.getByLabel("判定方式")).toHaveValue("ri");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
});

test("旧下書きの移行応答が遅くても別タブの保存を上書きしない", async ({
  page,
  context,
  request,
}) => {
  await seedLegacy(page, request);
  let release!: () => void, arrived!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const received = new Promise<void>((resolve) => {
    arrived = resolve;
  });
  await page.route("**/ui/definition-editor?*", async (route) => {
    const response = await route.fetch();
    arrived();
    await gate;
    await route.fulfill({ response });
  });
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await received;
  const other = await context.newPage();
  await open(other);
  await select(other, "先行");
  await other
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("printf other-tab");
  await expect(
    other.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  release();
  await expect(page.getByRole("alert")).toContainText("別のタブ");
  await page.getByLabel("編集ネット").selectOption("/判定検証");
  await select(page, "先行");
  await expect(
    page.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf legacy-edit");
  await other.reload();
  await other.getByRole("button", { name: "定義編集", exact: true }).click();
  await other.getByLabel("編集ネット").selectOption("/判定検証");
  await select(other, "先行");
  await expect(
    other.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("printf other-tab");
});
