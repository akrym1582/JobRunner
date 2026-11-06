import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
const api = "http://127.0.0.1:5181/api/jobrunner/ui/definition-editor";
const text =
  '/* /group keep */\r\nunit=group,0000,owner,team;{ty=g;cl=su;stt=06:00;el=child,g,+80 +48;unit=child;{ty=g;unit=net;{ty=n;sd=1,2026/11/1;st=1,07:00;cy=1,(1,d);el=job,j,+80 +48;unit=job;{ty=j;te="echo /group/child";}}}}unit=keep;{ty=g;}unit=external;{ty=n;sd=0,ud;jc="/group/child";}\r\n';
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
  await open(page);
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "groups.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(text),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/group/child/net");
  await saved(page);
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByText("グループの名前変更・削除", { exact: true }).click();
}
async function rename(page: Page, id: string, name: string, persist = true) {
  await page.getByLabel("操作するグループ").selectOption(id);
  await page.getByLabel("変更後のグループ名", { exact: true }).fill(name);
  await page
    .getByRole("button", { name: "グループ名を変更", exact: true })
    .click();
  await expect(
    page.getByLabel("操作するグループ").locator(`option[value="${id}"]`),
  ).toHaveText(new RegExp(name + "$"));
  if (persist) await saved(page);
}
async function preview(page: Page) {
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
}

test("親子改名・参照・試算と子孫削除を保存し、再取込できる", async ({
  page,
  request,
}, info) => {
  const active = await (await request.get(api)).json();
  await load(page);
  await rename(page, "/group", "renamed");
  await rename(page, "/group/child", "calendar");
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/group/child/net"]'),
  ).toHaveText("/renamed/calendar/net");
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
  await page.getByLabel("仮の登録基準日").fill("2026-11-01");
  await page.getByLabel("試算開始業務日").fill("2026-11-01");
  await page.getByLabel("試算終了業務日（含まない）").fill("2026-11-10");
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  await expect(
    page.getByRole("status").filter({ hasText: /下書き候補 [1-9]/ }),
  ).toBeVisible();
  await page.getByLabel("編集ネット").selectOption("/external");
  await expect(
    page.getByLabel("参照カレンダー (jc)", { exact: true }),
  ).toHaveValue("/renamed/calendar");
  await preview(page);
  if (process.env.GROUP_SCREENSHOT)
    await page.screenshot({
      path: "../../../docs/spec/scheduler/web-ui/images/group-editor.png",
      fullPage: true,
    });
  const downloading = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const file = await downloading,
    path = info.outputPath("renamed.ajs");
  await file.saveAs(path);
  const bytes = await readFile(path);
  expect(bytes.toString()).toContain('te="echo /group/child"');
  expect(bytes.toString()).toContain('unit="renamed",0000,owner,team;');
  const imported = await (
    await request.post(api + "/local", {
      data: {
        sources: [
          {
            mapping: {
              kind: "Unit",
              blobPath: "renamed.ajs",
              targetPath: "/",
              encoding: "utf-8",
            },
            base64: bytes.toString("base64"),
          },
        ],
      },
    })
  ).json();
  expect(
    imported.draft.schedules.find((s: { id: string }) => s.id === "/external")
      .attributes.jc,
  ).toEqual(["/renamed/calendar"]);
  await page.getByLabel("操作するグループ").selectOption("/group");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループと子孫を削除", exact: true })
    .click();
  await expect(
    page.getByRole("status").filter({ hasText: /jcが削除対象/ }),
  ).toBeVisible();
  await page.getByLabel("参照カレンダー (jc)", { exact: true }).fill("/keep");
  page.once("dialog", (d) => d.dismiss());
  await page
    .getByRole("button", { name: "グループと子孫を削除", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット").locator("option")).toHaveCount(2);
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループと子孫を削除", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット").locator("option")).toHaveCount(1);
  await saved(page);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(page.getByLabel("編集ネット").locator("option")).toHaveCount(2);
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット").locator("option")).toHaveCount(1);
  await preview(page);
  expect((await (await request.get(api)).json()).draft.definitionHash).toBe(
    active.draft.definitionHash,
  );
});

test("仮想配置先の追加ファイルを版6から移行して改名・ZIP再取込できる", async ({
  page,
  request,
}, info) => {
  await load(page);
  await page.getByText("グループ・Unitファイルを作成", { exact: true }).click();
  await page.getByLabel("新規ファイル名", { exact: true }).fill("extra.ajs");
  await page
    .getByLabel("新規ファイルの論理配置先", { exact: true })
    .fill("/newarea/nested");
  await page.getByLabel("初期グループ名", { exact: true }).fill("extra");
  await page
    .getByRole("button", { name: "下書きにファイルを追加", exact: true })
    .click();
  await expect(
    page
      .getByLabel("操作するグループ")
      .locator('option[value="/newarea/nested/extra"]'),
  ).toHaveCount(1);
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
          saved.base.draft.editorVersion = 6;
          saved.draft.editorVersion = 6;
          delete saved.base.draft.groupChanges;
          delete saved.draft.groupChanges;
          saved.revision = "legacy-six";
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
  await page.getByText("グループの名前変更・削除", { exact: true }).click();
  await rename(page, "/newarea/nested/extra", "renamed");
  await preview(page);
  const downloading = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("virtual.zip");
  await (await downloading).saveAs(path);
  const imported = await (
    await request.post(api + "/local", {
      data: { zipBase64: (await readFile(path)).toString("base64") },
    })
  ).json();
  expect(
    imported.draft.calendars.some(
      (g: { id: string }) => g.id === "/newarea/nested/renamed",
    ),
  ).toBe(true);
  expect(
    imported.draft.localSources.find(
      (s: { mapping: { blobPath: string } }) =>
        s.mapping.blobPath === "groups.ajs",
    ).base64,
  ).toBe(Buffer.from(text).toString("base64"));
});

test("遅い改名と別タブのグループ操作で編集を失わない", async ({
  page,
  context,
}) => {
  await load(page);
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
  await page.getByLabel("変更後のグループ名", { exact: true }).fill("stale");
  await page
    .getByRole("button", { name: "グループ名を変更", exact: true })
    .click();
  await started;
  await page.getByLabel("ネットのコメント", { exact: true }).fill("newer edit");
  release();
  await expect(
    page.getByRole("status").filter({ hasText: /検証中に編集されたため/ }),
  ).toBeVisible();
  await expect(
    page.getByLabel("ネットのコメント", { exact: true }),
  ).toHaveValue("newer edit");
  await page.unroute("**/definition-editor/preview");
  await saved(page);
  const other = await context.newPage();
  await open(other);
  await other.getByText("グループの名前変更・削除", { exact: true }).click();
  await rename(page, "/group", "winner");
  await rename(other, "/group", "loser", false);
  await expect(
    other.getByRole("alert").filter({ hasText: /別のタブ/ }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("グループの名前変更・削除", { exact: true }).click();
  await expect(
    page.getByLabel("操作するグループ").locator('option[value="/group"]'),
  ).toHaveText("/winner");
  await other.close();
});
