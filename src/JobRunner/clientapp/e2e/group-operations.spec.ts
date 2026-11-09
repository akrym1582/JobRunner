import { test, expect } from "@playwright/test";
import { readFile } from "node:fs/promises";

const text =
  'unit=business;{ty=g;unit=net;{ty=n;sd=0,ud;unit=job;{ty=j;te="echo original";}}}unit=other;{ty=g;}';

test("グループZIPの追加とundoを保存して再読込できる", async ({
  page,
  request,
}) => {
  const before = await (
    await request.get("http://127.0.0.1:5181/api/jobrunner/ui/runtime")
  ).json();
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "group.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(text),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/business/net");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page
    .getByText("グループのエクスポート・インポート", { exact: true })
    .click();
  await page.getByLabel("入出力グループ").selectOption("/business");
  const downloaded = page.waitForEvent("download");
  await page.getByRole("button", { name: "グループZIPをダウンロード" }).click();
  const zip = await readFile((await (await downloaded).path())!);
  await page.getByLabel("グループ取込ファイル", { exact: true }).setInputFiles({
    name: "group.zip",
    mimeType: "application/zip",
    buffer: zip,
  });
  await page.getByLabel("追加グループ名", { exact: true }).fill("copied");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループを下書きへ取り込む" })
    .click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/copied/net"]'),
  ).toHaveCount(1);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/copied/net"]'),
  ).toHaveCount(1);
  // 再読込後は履歴がリセットされるため、同じZIPをもう一度追加してundoの永続化を確認する。
  await page
    .getByText("グループのエクスポート・インポート", { exact: true })
    .click();
  await page.getByLabel("グループ取込ファイル", { exact: true }).setInputFiles({
    name: "group.zip",
    mimeType: "application/zip",
    buffer: zip,
  });
  await page.getByLabel("追加グループ名", { exact: true }).fill("undo_group");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループを下書きへ取り込む" })
    .click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/undo_group/net"]'),
  ).toHaveCount(1);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/undo_group/net"]'),
  ).toHaveCount(0);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/undo_group/net"]'),
  ).toHaveCount(0);
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/business/net"]'),
  ).toHaveCount(1);
  const after = await (
    await request.get("http://127.0.0.1:5181/api/jobrunner/ui/runtime")
  ).json();
  expect(after.active).toEqual(before.active);
});

test("親からの停止を表示し、子の再開は版と固定IDを送る", async ({ page }) => {
  await page.route("**/api/jobrunner/ui/groups", (route) =>
    route.fulfill({
      json: [
        {
          id: "parent-id",
          path: "/parent",
          definitionRevision: 5,
          control: { revision: 2, paused: true },
          pausedBy: ["/parent"],
          retired: false,
        },
        {
          id: "child-id",
          path: "/parent/child",
          definitionRevision: 5,
          control: { revision: 3, paused: true },
          pausedBy: ["/parent", "/parent/child"],
          retired: false,
        },
      ],
    }),
  );
  await page.route("**/api/jobrunner/operations", async (route) => {
    const value = route.request().postDataJSON();
    expect(value.kind).toBe("ResumeGroup");
    expect(value.target).toBe("child-id");
    expect(value.expectedControlRevision).toBe(3);
    expect(value.expectedDefinitionRevision).toBe(5);
    await route.fulfill({ status: 202, json: { ...value, status: "Pending" } });
  });
  await page.route("**/api/jobrunner/operations/*", (route) =>
    route.fulfill({ json: { status: "Completed", result: "Running" } }),
  );
  await page.goto("/");
  await page
    .getByLabel("運用グループ", { exact: true })
    .selectOption("child-id");
  const control = page.getByRole("region", { name: "グループの実行制御" });
  await expect(control).toContainText("/parent、/parent/child");
  page.once("dialog", async (d) => {
    expect(d.message()).toContain("親グループが停止中");
    await d.accept();
  });
  const request = page.waitForRequest(
    (r) =>
      r.url().endsWith("/api/jobrunner/operations") && r.method() === "POST",
  );
  await page
    .getByRole("button", { name: "グループの予定実行を再開", exact: true })
    .click();
  await request;
  await expect(control).toContainText("停止中");
});

for (const accepted of [false, true]) {
  test(`通信失敗と再読込後に同じグループ操作を再送できる（受付済み=${accepted}）`, async ({
    page,
  }) => {
    await page.route("**/api/jobrunner/ui/groups", (route) =>
      route.fulfill({
        json: [
          {
            id: "recover-group",
            path: "/recover",
            definitionRevision: 5,
            control: { revision: 2, paused: true },
            pausedBy: ["/recover"],
            retired: false,
          },
        ],
      }),
    );
    const sent: unknown[] = [];
    let recovered = false;
    await page.route("**/api/jobrunner/operations", async (route) => {
      sent.push(route.request().postDataJSON());
      if (sent.length === 1) return route.abort("failed");
      recovered = true;
      await route.fulfill({
        status: 202,
        json: { ...route.request().postDataJSON(), status: "Completed" },
      });
    });
    await page.route("**/api/jobrunner/operations/*", (route) =>
      route.fulfill(
        recovered
          ? { json: { status: "Completed", result: "Running" } }
          : accepted
            ? { json: { status: "Pending" } }
            : { status: 404, json: { message: "Not found" } },
      ),
    );
    await page.goto("/");
    await page
      .getByLabel("運用グループ", { exact: true })
      .selectOption("recover-group");
    page.once("dialog", (d) => d.accept());
    await page
      .getByRole("button", { name: "グループの予定実行を再開", exact: true })
      .click();
    const control = page.getByRole("region", { name: "グループの実行制御" });
    await expect(
      control.getByRole("button", { name: "同じ操作IDで再照会・再送" }),
    ).toBeVisible();
    await page.reload();
    await expect(
      control.getByRole("button", { name: "同じ操作IDで再照会・再送" }),
    ).toBeVisible();
    await control
      .getByRole("button", { name: "同じ操作IDで再照会・再送" })
      .click();
    await expect(
      control.getByText("グループ操作の結果を確認中…", { exact: true }),
    ).toHaveCount(0);
    expect(sent).toHaveLength(2);
    expect(sent[1]).toEqual(sent[0]);
    await page
      .getByLabel("運用グループ", { exact: true })
      .selectOption("recover-group");
    await expect(
      page.getByRole("button", {
        name: "グループの予定実行を再開",
        exact: true,
      }),
    ).toBeEnabled();
  });
}

test("グループ移動を確認して保存しundoできる", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "move.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(
      "unit=a;{ty=g;unit=g;{ty=g;unit=n;{ty=n;sd=0,ud;}}}unit=b;{ty=g;cl=su;}",
    ),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/a/g/n");
  await page.getByText("グループ・ルートネットの移動", { exact: true }).click();
  await page
    .getByLabel("移動するユニット", { exact: true })
    .selectOption("/a/g");
  await page.getByLabel("移動先グループ", { exact: true }).selectOption("/b");
  page.once("dialog", async (d) => {
    expect(d.message()).toContain("移動先の継承");
    await d.accept();
  });
  await page
    .getByRole("button", { name: "移動候補を確認", exact: true })
    .click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/b/g/n"]'),
  ).toHaveCount(1);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/g/n"]'),
  ).toHaveCount(1);
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/b/g/n"]'),
  ).toHaveCount(1);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/b/g/n"]'),
  ).toHaveCount(1);
});

test("外部カレンダーを同梱しないZIPを不足状態で保存できる", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  const input = page.getByLabel("定義ファイル", { exact: true });
  await input.setInputFiles({
    name: "external.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(
      'unit=calendar;{ty=g;cl=su;}unit=g;{ty=g;unit=n;{ty=n;sd=0,ud;jc="/calendar";}}',
    ),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await page
    .getByText("グループのエクスポート・インポート", { exact: true })
    .click();
  await page.getByLabel("入出力グループ").selectOption("/g");
  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "グループZIPをダウンロード" }).click();
  const zip = await readFile((await (await download).path())!);
  await input.setInputFiles({
    name: "target.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from("unit=keep;{ty=g;}"),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("入出力グループ")).toHaveValue("/keep");
  await page
    .getByLabel("グループ取込ファイル", { exact: true })
    .setInputFiles({ name: "g.zip", mimeType: "application/zip", buffer: zip });
  await page.getByLabel("追加グループ名", { exact: true }).fill("copied");
  page.once("dialog", async (d) => {
    expect(d.message()).toContain("参照先不足");
    await d.accept();
  });
  await page
    .getByRole("button", { name: "グループを下書きへ取り込む" })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/copied/n");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/copied/n");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(
    page.getByText("検証エラー — 修正してください", { exact: true }),
  ).toBeVisible();
  const refused = page.waitForResponse((r) =>
    r.url().endsWith("/api/jobrunner/ui/definition-editor/publication-preview"),
  );
  await page
    .getByRole("button", { name: "運用反映の差分を確認", exact: true })
    .click();
  const response = await refused;
  expect(response.status()).toBe(400);
  expect(JSON.stringify(await response.json())).toContain(
    "参照カレンダーのグループが存在しません",
  );
  await expect(
    page.getByRole("dialog", { name: "運用反映の確認" }),
  ).toHaveCount(0);
  await expect(
    page.getByText(/参照カレンダーのグループが存在しません/).first(),
  ).toBeVisible();
});

test("所属の古い下書きは運用反映前に拒否して採用版を保持する", async ({
  request,
}) => {
  const api = "http://127.0.0.1:5181/api/jobrunner/ui";
  const before = await (await request.get(api + "/runtime")).json();
  const current = await (await request.get(api + "/definition-editor")).json();
  expect(current.draft.originCatalogHash).toBe(before.active.groupCatalogHash);
  const fresh = await request.post(
    api + "/definition-editor/publication-preview",
    { data: current.draft },
  );
  expect(fresh.status()).toBe(200);
  const stale = await request.post(
    api + "/definition-editor/publication-preview",
    { data: { ...current.draft, originCatalogHash: "0".repeat(64) } },
  );
  expect(stale.status()).toBe(400);
  expect(JSON.stringify(await stale.json())).toContain("所属が変わりました");
  expect((await (await request.get(api + "/runtime")).json()).active).toEqual(
    before.active,
  );
});

test("旧ルート移動の下書きから続けて移動し保存・再読込できる", async ({
  page,
}) => {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "chain.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(
      "unit=a;{ty=g;unit=n;{ty=n;sd=0,ud;}}unit=b;{ty=g;}unit=c;{ty=g;}",
    ),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/a/n");
  await page.getByText("ネットの移動・種別切替", { exact: true }).click();
  await page.getByLabel("ルートの移動先", { exact: true }).selectOption("/b");
  page.once("dialog", (d) => d.accept());
  await page.getByRole("button", { name: "移動を検証", exact: true }).click();
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/a/n"]'),
  ).toContainText("/b/n");
  await page.getByText("グループ・ルートネットの移動", { exact: true }).click();
  await page
    .getByLabel("移動するユニット", { exact: true })
    .selectOption("/b/n");
  await page.getByLabel("移動先グループ", { exact: true }).selectOption("/c");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "移動候補を確認", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/c/n");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/c/n");
});

test("JP1テキストのグループ追加・置換と保存・undo", async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "original.txt",
    mimeType: "text/plain",
    buffer: Buffer.from(text),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/business/net");
  await page
    .getByText("グループのエクスポート・インポート", { exact: true })
    .click();
  await page.getByLabel("グループ取込ファイル", { exact: true }).setInputFiles({
    name: "jp1.txt",
    mimeType: "text/plain",
    buffer: Buffer.from(
      '\uFEFFunit=新ネット;{ty=n;sd=0,ud;unit=ジョブ;{ty=j;te="echo text";}}',
      "utf16le",
    ),
  });
  await page.getByLabel("グループ取込文字コード").selectOption("utf-16");
  await page.getByLabel("追加グループ名", { exact: true }).fill("from_text");
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループを下書きへ取り込む" })
    .click();
  const imported = () =>
    page
      .getByLabel("編集ネット")
      .locator('option[value="/from_text/新ネット"]');
  await expect(imported()).toHaveCount(1);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(imported()).toHaveCount(0);
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(imported()).toHaveCount(1);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(imported()).toHaveCount(1);
  await page
    .getByText("グループのエクスポート・インポート", { exact: true })
    .click();
  await page.getByLabel("入出力グループ").selectOption("/from_text");
  await page.getByLabel("グループ取込方法").selectOption("replace");
  await page.getByLabel("グループ取込ファイル", { exact: true }).setInputFiles({
    name: "replace.ajs",
    mimeType: "text/plain",
    buffer: Buffer.from("unit=g;{ty=g;unit=replaced;{ty=n;sd=0,ud;}}"),
  });
  page.once("dialog", (d) => d.accept());
  await page
    .getByRole("button", { name: "グループを下書きへ取り込む" })
    .click();
  await expect(imported()).toHaveCount(0);
  await expect(
    page
      .getByLabel("編集ネット")
      .locator('option[value="/from_text/replaced"]'),
  ).toHaveCount(1);
  await expect(
    page.getByLabel("編集ネット").locator('option[value="/business/net"]'),
  ).toHaveCount(1);
});
