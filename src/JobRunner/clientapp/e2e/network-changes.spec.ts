import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
const api = "http://127.0.0.1:5181/api/jobrunner/ui/definition-editor";
const text =
  '/* keep /a/net */\r\nunit=a;{ty=g;cl=su;el=net,n,+80 +48;unit=net,0000,owner,team;{ty=n;sd=0,ud;el=pre,j,+80 +48;el=child,n,+240 +48;ar=(f=pre,t=child);unit=pre;{ty=j;te="echo /a/net";}unit=child;{ty=n;cm="keep";}}}unit=b;{ty=g;cl=su;}unit=bad;{ty=g;cl=sa;}';
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
async function tools(page: Page) {
  await page.getByText("ネットの移動・種別切替", { exact: true }).click();
}
async function load(page: Page) {
  await open(page);
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "networks.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(text),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/a/net");
  await saved(page);
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await tools(page);
}
async function move(page: Page, persist = true) {
  await page.getByLabel("ルートの移動先", { exact: true }).selectOption("/b");
  page.once("dialog", async (d) => {
    expect(d.message()).toContain("属性・接続・子孫の削除はありません");
    await d.accept();
  });
  await page.getByRole("button", { name: "移動を検証", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/net"]'),
  ).toHaveText("/b/net");
  if (persist) await saved(page);
}
test("継承を保つ移動と種別切替を確認し、履歴・原文・出力を保つ", async ({
  page,
  request,
}, info) => {
  const active = await (await request.get(api)).json();
  await load(page);
  await page.getByLabel("ルートの移動先", { exact: true }).selectOption("/bad");
  await page.getByRole("button", { name: "移動を検証", exact: true }).click();
  await expect(
    page.getByRole("status").filter({ hasText: "継承条件が異なります" }),
  ).toBeVisible();
  await page.getByLabel("ルートの移動先", { exact: true }).selectOption("/b");
  page.once("dialog", (d) => d.dismiss());
  await page.getByRole("button", { name: "移動を検証", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/net"]'),
  ).toHaveText("/a/net");
  await move(page);
  await page.getByText("グループの名前変更・削除", { exact: true }).click();
  await page.getByLabel("操作するグループ").selectOption("/a");
  await expect(
    page.getByRole("button", { name: "グループと子孫を削除", exact: true }),
  ).toBeDisabled();
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/net"]'),
  ).toHaveText("/a/net");
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await page.getByLabel("編集ネット").selectOption("/a/net/child");
  await tools(page);
  page.once("dialog", async (d) => {
    expect(d.message()).toContain("異常・タイムアウト・中断");
    await d.accept();
  });
  await page
    .getByRole("button", { name: "リカバリーへの切替を検証", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "通常ネットへの切替を検証", exact: true }),
  ).toBeVisible();
  await saved(page);
  await page.getByLabel("編集ネット").selectOption("/a/net");
  await expect(page.locator(".editor-edge")).toHaveAttribute(
    "data-kind",
    "recovery",
  );
  await page
    .locator(".editor-unit-list button")
    .filter({ hasText: "child" })
    .click();
  await expect(
    page
      .getByRole("complementary", { name: "ジョブのプロパティ" })
      .locator(".outline-pill"),
  ).toHaveText("rn");
  await page.getByLabel("子ネット名", { exact: true }).fill("changed");
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/net/child"]'),
  ).toHaveText("/b/net/changed");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
  if (process.env.NETWORK_SCREENSHOT) {
    await tools(page);
    await page.screenshot({
      path: "../../../docs/spec/scheduler/web-ui/images/network-changes.png",
      fullPage: true,
    });
  }
  const downloading = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("changed.ajs");
  await (await downloading).saveAs(path);
  const bytes = await readFile(path);
  expect(bytes.toString()).toContain('te="echo /a/net"');
  expect(bytes.toString()).toContain("unit=net,0000,owner,team;");
  const imported = await (
    await request.post(api + "/local", {
      data: {
        sources: [
          {
            mapping: {
              kind: "Unit",
              blobPath: "changed.ajs",
              targetPath: "/",
              encoding: "utf-8",
            },
            base64: bytes.toString("base64"),
          },
        ],
      },
    })
  ).json();
  const net = imported.draft.networks.find(
    (n: { path: string }) => n.path === "/b/net",
  );
  expect(
    net.nodes.find((n: { name: string }) => n.name === "changed").type,
  ).toBe("rn");
  expect(net.edges).toHaveLength(1);
  expect((await (await request.get(api)).json()).draft.definitionHash).toBe(
    active.draft.definitionHash,
  );
});

test("版7の下書きはグループ改名と省略値を保持してネット操作へ移行できる", async ({
  page,
}) => {
  await load(page);
  await page.getByText("グループの名前変更・削除", { exact: true }).click();
  await page.getByLabel("操作するグループ").selectOption("/b");
  await page.getByLabel("変更後のグループ名", { exact: true }).fill("renamed");
  await page
    .getByRole("button", { name: "グループ名を変更", exact: true })
    .click();
  await expect(
    page.getByLabel("ルートの移動先").locator('option[value="/b"]'),
  ).toHaveText("/renamed");
  await saved(page);
  await page.evaluate(async () => {
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open("jobrunner-definition-editor", 2);
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result,
          tx = db.transaction("drafts", "readwrite"),
          store = tx.objectStore("drafts"),
          read = store.get("current");
        read.onsuccess = () => {
          const saved = read.result;
          saved.base.draft.editorVersion = 7;
          saved.draft.editorVersion = 7;
          delete saved.base.draft.networkChanges;
          delete saved.draft.networkChanges;
          saved.revision = "legacy-seven";
          store.put(saved, "current");
        };
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
  });
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await saved(page);
  await tools(page);
  await page.getByLabel("ルートの移動先").selectOption("/b");
  page.once("dialog", (d) => d.accept());
  await page.getByRole("button", { name: "移動を検証", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/net"]'),
  ).toHaveText("/renamed/net");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
  await expect(page.getByLabel("変更後の原文", { exact: true })).toBeVisible();
  await expect(
    page.getByLabel("変更後の原文", { exact: true }),
  ).not.toContainText("ha=y");
});

test("遅い移動確認と別タブの操作で新しい編集を失わない", async ({
  page,
  context,
}) => {
  await load(page);
  let release!: () => void, reached!: () => void;
  const gate = new Promise<void>((r) => {
      release = r;
    }),
    started = new Promise<void>((r) => {
      reached = r;
    });
  await page.route("**/definition-editor/preview", async (route) => {
    const response = await route.fetch();
    reached();
    await gate;
    await route.fulfill({ response });
  });
  await page.getByLabel("ルートの移動先").selectOption("/b");
  await page.getByRole("button", { name: "移動を検証", exact: true }).click();
  await started;
  await page.getByLabel("ネットのコメント", { exact: true }).fill("newer");
  release();
  await expect(
    page.getByRole("status").filter({ hasText: /検証中に編集されたため/ }),
  ).toBeVisible();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/net"]'),
  ).toHaveText("/a/net");
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("newer");
  await page.unroute("**/definition-editor/preview");
  await saved(page);
  const other = await context.newPage();
  await open(other);
  await tools(other);
  await move(page);
  await move(other, false);
  await expect(
    other.getByRole("alert").filter({ hasText: /別のタブ/ }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/net"]'),
  ).toHaveText("/b/net");
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("newer");
  await other.close();
});
