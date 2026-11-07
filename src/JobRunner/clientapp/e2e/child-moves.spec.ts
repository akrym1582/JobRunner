import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";

const api = "http://127.0.0.1:5181/api/jobrunner/ui/definition-editor";
const source =
  '/* source unchanged */\nunit=a;{ty=n;sd=0,ud;sz=8*2;ar=(f=pre,t=j,seq);ar=(f=j,t=k,seq);ar=(f=k,t=n,seq);ar=(f=n,t=post,seq);el=pre,j,+80 +48;el=j,j,+240 +48;el=k,j,+400 +48;el=n,n,+560 +48;el=post,j,+720 +48;unit=pre;{ty=j;te="true";}unit=j;{ty=j;te="echo j";}unit=k;{ty=j;te="echo k";}unit=n;{ty=n;unit=inner;{ty=n;}}unit=post;{ty=j;te="true";}}unit=b;{ty=n;sd=0,ud;}';
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}
async function open(page: Page, load = true) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
  if (!load) return;
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "moves.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(source),
  });
  page.once("dialog", (d) => void d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/a");
  await saved(page);
}
async function select(page: Page) {
  const graph = page.locator("svg.editor-graph");
  await graph.getByRole("button", { name: "編集 j", exact: true }).click();
  await graph
    .getByRole("button", { name: "編集 k", exact: true })
    .click({ modifiers: ["Control"] });
  await graph
    .getByRole("button", { name: "編集 n", exact: true })
    .click({ modifiers: ["Control"] });
  await expect(
    page.getByRole("button", {
      name: "選択の移動候補を確認（3件）",
      exact: true,
    }),
  ).toBeEnabled();
  await page.getByLabel("選択の移動先ネット").selectOption("/b");
}
async function move(page: Page, accept = true) {
  const dialog = page.waitForEvent("dialog");
  const clicking = page
    .getByRole("button", { name: "選択の移動候補を確認（3件）", exact: true })
    .click();
  const confirmation = await dialog;
  expect(confirmation.message()).toContain("/a/pre → /a/j");
  expect(confirmation.message()).toContain("/a/n → /a/post");
  expect(confirmation.message()).toContain("通常接続");
  if (accept) await confirmation.accept();
  else await confirmation.dismiss();
  await clicking;
}
async function showsMoved(page: Page) {
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/b/n/inner"]'),
  ).toHaveCount(1);
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/n"]'),
  ).toHaveCount(0);
}

test("接続の切断を確認して複数のジョブと子ネットを移動、undo/redoと再取込ができる", async ({
  page,
  request,
}, info) => {
  const activeBefore = await (
    await request.get("http://127.0.0.1:5181/api/jobrunner/ui/jobnets")
  ).json();
  await open(page);
  await select(page);
  await move(page, false);
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/n"]'),
  ).toHaveCount(1);
  await move(page);
  await showsMoved(page);
  await saved(page);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/n"]'),
  ).toHaveCount(1);
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await showsMoved(page);
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await showsMoved(page);
  await page.getByLabel("編集ネット").selectOption("/b");
  await expect(
    page.getByRole("button", { name: "編集 j", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
  const downloading = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("moved.ajs");
  await (await downloading).saveAs(path);
  const bytes = await readFile(path);
  expect(bytes.toString()).toContain("/* source unchanged */");
  const imported = await request.post(api + "/local", {
    data: {
      sources: [
        {
          mapping: {
            kind: "Unit",
            blobPath: "moved.ajs",
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
  const destination = document.draft.networks.find(
    (n: { path: string }) => n.path === "/b",
  );
  expect(destination.nodes.map((n: { name: string }) => n.name)).toEqual([
    "j",
    "k",
    "n",
  ]);
  expect(destination.edges).toHaveLength(2);
  expect(
    document.draft.networks.find((n: { path: string }) => n.path === "/a")
      .edges,
  ).toHaveLength(0);
  expect(
    await (
      await request.get("http://127.0.0.1:5181/api/jobrunner/ui/jobnets")
    ).json(),
  ).toEqual(activeBefore);
});

test("遅い移動候補応答で応答待ちの新しい編集を失わない", async ({ page }) => {
  await open(page);
  await select(page);
  let release!: () => void;
  const gate = new Promise<void>((r) => {
    release = r;
  });
  let arrived!: () => void;
  const intercepted = new Promise<void>((r) => {
    arrived = r;
  });
  await page.route("**/definition-editor/move-selection", async (route) => {
    const response = await route.fetch();
    arrived();
    await gate;
    await route.fulfill({ response });
  });
  await page
    .getByRole("button", { name: "選択の移動候補を確認（3件）", exact: true })
    .click();
  await intercepted;
  // 属性入力は候補の通信中にも行えます。古い候補は現在の編集版へ適用しません。
  await page.getByLabel("ネット名", { exact: true }).fill("renamed-a");
  release();
  await expect(
    page.getByText("読込中に編集されたため、取り込みを中止しました。", {
      exact: true,
    }),
  ).toBeVisible();
  await expect(page.getByLabel("ネット名", { exact: true })).toHaveValue(
    "renamed-a",
  );
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page
      .getByLabel("編集ネット")
      .locator("option")
      .filter({ hasText: /^\/renamed-a\/n$/ }),
  ).toHaveCount(1);
});

test("別タブの移動を古い下書きが上書きせず、保存済みの移動を読み直せる", async ({
  page,
  context,
}) => {
  await open(page);
  const stale = await context.newPage();
  await open(stale, false);
  await expect(
    stale.getByLabel("編集ネット").locator('option[value="/a/n"]'),
  ).toHaveCount(1);
  await select(page);
  await move(page);
  await saved(page);
  await stale.getByRole("button", { name: "編集 pre", exact: true }).click();
  await stale
    .getByRole("textbox", { name: "コマンド", exact: true })
    .fill("echo stale");
  await expect(
    stale.getByText(/別のタブで下書きが更新されました/).first(),
  ).toBeVisible();
  // 競合後もダウンロード用のローカル編集は継続できます。追加の移動でも共有済み版を上書きしません。
  await stale.getByLabel("選択の移動先ネット").selectOption("/b");
  stale.once("dialog", (d) => void d.accept());
  await stale
    .getByRole("button", { name: "選択の移動候補を確認（1件）", exact: true })
    .click();
  await stale.getByLabel("編集ネット").selectOption("/b");
  await expect(
    stale.getByRole("button", { name: "編集 pre", exact: true }),
  ).toBeVisible();
  stale.once("dialog", (d) => void d.accept());
  await stale
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await showsMoved(stale);
  await stale.getByLabel("編集ネット").selectOption("/a");
  await stale.getByRole("button", { name: "編集 pre", exact: true }).click();
  await expect(
    stale.getByRole("textbox", { name: "コマンド", exact: true }),
  ).toHaveValue("true");
  await stale.close();
});

test("移動の遅い保存中にundo/redoしても現在の内容だけを保存済みと表示する", async ({
  page,
}) => {
  await open(page);
  await select(page);
  await page.evaluate(() => {
    const gate = { blocked: false, release: false };
    (window as unknown as { moveWriteGate: typeof gate }).moveWriteGate = gate;
    const descriptor = Object.getOwnPropertyDescriptor(
      IDBRequest.prototype,
      "onsuccess",
    )!;
    let armed = true;
    Object.defineProperty(IDBRequest.prototype, "onsuccess", {
      ...descriptor,
      set(handler: ((event: Event) => void) | null) {
        const request = this as IDBRequest,
          tx = request.transaction;
        if (
          armed &&
          tx?.mode === "readwrite" &&
          tx.db.name === "jobrunner-definition-editor"
        ) {
          armed = false;
          descriptor.set!.call(request, (event: Event) => {
            gate.blocked = true;
            function wait() {
              if (gate.release) handler?.call(request, event);
              else
                tx!.objectStore("drafts").get("move-keepalive").onsuccess =
                  wait;
            }
            wait();
          });
        } else descriptor.set!.call(request, handler);
      },
    });
  });
  await move(page);
  await showsMoved(page);
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as unknown as { moveWriteGate: { blocked: boolean } })
            .moveWriteGate.blocked,
      ),
    )
    .toBe(true);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toHaveCount(0);
  await page.evaluate(() => {
    (
      window as unknown as { moveWriteGate: { release: boolean } }
    ).moveWriteGate.release = true;
  });
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await showsMoved(page);
});
