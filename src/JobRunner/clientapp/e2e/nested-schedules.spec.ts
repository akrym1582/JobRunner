import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
const api = "http://127.0.0.1:5181/api/jobrunner";
const source =
  '\uFEFF/* preserve nested source */\r\nunit=cal;{ty=g;cl=sa;cl=su;cl=2026/11/23;cl=2026/11/26;}\r\nunit=n;{ty=n;sd=1,en;cy=1,(1,d);st=1,09:00;sh=1,no;sz=1x1;el=c,n,+80 +48;unit=c;{ty=n;sz=1x1;el=h,n,+80 +48;unit=h;{ty=n;sz=1x1;el=j,j,+80 +48;unit=j;{ty=j;te="printf preserved";}}}}\r\n';
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
async function local(page: Page, text = source) {
  await open(page);
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "nested.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(text),
  });
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue(
    text.includes("unit=g;") ? "/g/n" : "/n",
  );
  await saved(page);
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
}
async function trial(
  page: Page,
  id: string,
  from = "2026-11-16",
  until = "2026-11-19",
) {
  await page.getByLabel("編集ネット").selectOption(id);
  await page.getByLabel("仮の登録基準日", { exact: true }).fill("2026-11-01");
  await page.getByLabel("試算開始業務日", { exact: true }).fill(from);
  await page
    .getByLabel("試算終了業務日（含まない）", { exact: true })
    .fill(until);
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  return page.getByRole("region", { name: "下書き予定試算", exact: true });
}
async function own(page: Page) {
  await page.getByLabel("編集ネット").selectOption("/n/c");
  await page
    .getByRole("button", { name: "依存方法を設定", exact: true })
    .click();
  await page
    .getByLabel("スケジュールの依存方法", { exact: true })
    .selectOption("own");
  await page.getByLabel("起算方向", { exact: true }).selectOption("be");
  await page.getByLabel("起算する運用日数", { exact: true }).fill("5");
  await page
    .getByLabel("簡単設定のカレンダー", { exact: true })
    .selectOption("/cal");
}
async function apply(page: Page) {
  page.once("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
    .click();
  await saved(page);
}

test("子の独自設定と孫の継承を試算し、依存切替・undo・保存・改名・再取込で原文を保つ", async ({
  page,
  request,
}, info) => {
  const before = await (await request.get(api + "/ui/runtime")).json();
  const state = await (await request.get(api + "/executions")).json();
  await local(page);
  await own(page);
  await expect(page.getByLabel("生成するJP1属性")).toContainText("de=n;");
  await expect(page.getByLabel("生成するJP1属性")).toContainText("ln=1,1;");
  const preview = await trial(page, "/n/c");
  await expect(preview).toContainText("スケジュール上の対象 1件 / 判定 3行");
  await expect(preview.locator("tbody tr").nth(1)).toContainText("2026-11-17");
  await expect(preview.locator("tbody tr").nth(1)).toContainText(
    "独自ルールを採用",
  );
  if (process.env.NESTED_SCREENSHOTS) {
    await page
      .getByRole("region", { name: "スケジュール簡単設定", exact: true })
      .screenshot({
        path: resolve(
          "../../../docs/spec/scheduler/web-ui/images/nested-schedule-editor.png",
        ),
      });
    await preview.screenshot({
      path: resolve(
        "../../../docs/spec/scheduler/web-ui/images/nested-schedule-preview.png",
      ),
    });
  }
  await apply(page);
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("1,25");
  await trial(page, "/n/c/h");
  await expect(preview).toContainText("スケジュール上の対象 1件 / 判定 3行");
  await expect(preview.locator("tbody tr").nth(1)).toContainText(
    "上位のスケジュールを使用",
  );
  await expect(preview.locator("tbody tr").first()).toContainText(
    "祖先が対象外",
  );
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
  await page.getByLabel("編集ネット").selectOption("/n/c");
  await page.getByLabel("ネット名", { exact: true }).fill("renamed");
  await trial(page, "/n/c/h");
  await preview
    .locator("tbody tr")
    .nth(1)
    .getByText("階層ごとの判定", { exact: true })
    .click();
  await expect(preview.locator("tbody tr").nth(1)).toContainText(
    "/n/renamed/h",
  );
  await page.getByLabel("編集ネット").selectOption("/n/c");
  await page
    .getByRole("button", { name: "上位依存への切替を設定", exact: true })
    .click();
  page.once("dialog", (dialog) => dialog.dismiss());
  await page
    .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("1,25");
  await apply(page);
  await expect(
    page.getByRole("checkbox", { name: "開始日 (sd)を指定", exact: true }),
  ).not.toBeChecked();
  await trial(page, "/n/c");
  await expect(preview).toContainText("スケジュール上の対象 3件 / 判定 3行");
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByRole("textbox", { name: "開始日 (sd)", exact: true }),
  ).toHaveValue("1,25");
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(
    page.getByRole("checkbox", { name: "開始日 (sd)を指定", exact: true }),
  ).not.toBeChecked();
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
  const downloading = page.waitForEvent("download");
  await page.getByRole("button", { name: "ダウンロード", exact: true }).click();
  const path = info.outputPath("nested.ajs");
  await (await downloading).saveAs(path);
  const bytes = await readFile(path);
  expect(bytes.subarray(0, 3)).toEqual(Buffer.from([0xef, 0xbb, 0xbf]));
  expect(bytes.toString()).toContain('unit=j;{ty=j;te="printf preserved";}');
  expect(bytes.toString()).toContain("/* preserve nested source */\r\n");
  const imported = await request.post(api + "/ui/definition-editor/local", {
    data: {
      sources: [
        {
          mapping: {
            kind: "Unit",
            blobPath: "nested.ajs",
            targetPath: "/",
            encoding: "utf-8",
          },
          base64: bytes.toString("base64"),
        },
      ],
    },
  });
  expect(imported.ok()).toBeTruthy();
  const reimported = await imported.json();
  expect(
    reimported.draft.schedules.find(
      (s: { id: string }) => s.id === "/n/renamed",
    ).attributes,
  ).toMatchObject({ de: ["n"], cftd: ["1,be,5,10"], ln: ["1,1"] });
  expect(
    (await (await request.get(api + "/ui/runtime")).json()).active,
  ).toEqual(before.active);
  expect(await (await request.get(api + "/executions")).json()).toEqual(state);
  const invalid = await request.post(
    api + "/ui/definition-editor/nested-schedules",
    {
      data: {
        draft: reimported.draft,
        networkId: "/n",
        anchor: "2026-11-01",
        from: "2026-11-01",
        until: "2026-11-02",
      },
    },
  );
  expect(invalid.status()).toBe(400);
});

test("上位予定なし・基準時刻・祖先対象外を表示し、48時間の候補を継承する", async ({
  page,
}) => {
  await local(
    page,
    "unit=g;{ty=g;stt=06:00;unit=n;{ty=n;sd=fr;cy=(1,w);st=10:00;sh=no;unit=c;{ty=n;de=n;sd=2026/11/6;st=05:00;sh=no;unit=h;{ty=n;}}}}",
  );
  // localの初期ルートはこのケースでは/g/nです。
  await page.getByLabel("編集ネット").selectOption("/g/n/c");
  const preview = await trial(page, "/g/n/c", "2026-11-05", "2026-11-08");
  await expect(preview).toContainText("スケジュール上の対象 0件 / 判定 3行");
  await expect(preview.locator("tbody tr").first()).toContainText(
    "上位予定なし",
  );
  await expect(preview.locator("tbody tr").nth(1)).toContainText(
    "基準時刻の条件が一致しません",
  );
  await trial(page, "/g/n/c/h", "2026-11-05", "2026-11-08");
  await expect(preview.locator("tbody tr").nth(1)).toContainText(
    "祖先が対象外",
  );
  await page.getByLabel("編集ネット").selectOption("/g/n/c");
  await page
    .getByRole("button", { name: "このルールを簡単設定で編集", exact: true })
    .click();
  await page.getByLabel("簡単設定の開始時刻", { exact: true }).fill("23:00");
  await page
    .getByRole("checkbox", { name: "基準時刻からの相対時刻", exact: true })
    .check();
  await page
    .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
    .click();
  await trial(page, "/g/n/c/h", "2026-11-05", "2026-11-08");
  await expect(preview).toContainText("スケジュール上の対象 1件 / 判定 3行");
  await expect(preview.locator("tbody tr").nth(1)).toContainText("05:00");
});

test("同じ子日時の別ルート世代を分け、リカバリーの起動条件を明記する", async ({
  page,
}) => {
  await local(
    page,
    'unit=n;{ty=n;sd=1,2026/11/5;sd=2,2026/11/5;st=1,09:00;st=2,10:00;sh=1,no;sh=2,no;ar=(f=fail,t=rc);unit=fail;{ty=j;te="false";}unit=c;{ty=n;de=n;sd=1,2026/11/5;sd=2,2026/11/5;st=1,11:00;st=2,11:00;ln=1,1;ln=2,2;sh=1,no;sh=2,no;}unit=rc;{ty=rn;}}',
  );
  const preview = await trial(page, "/n/c", "2026-11-05", "2026-11-06");
  await expect(preview).toContainText("スケジュール上の対象 2件 / 判定 2行");
  await expect(preview.locator("tbody tr")).toHaveCount(2);
  await expect(preview.locator("tbody tr").first()).toContainText("09:00");
  await expect(preview.locator("tbody tr").nth(1)).toContainText("10:00");
  await trial(page, "/n/rc", "2026-11-05", "2026-11-06");
  await expect(preview).toContainText("スケジュール上の対象 2件 / 判定 2行");
  await expect(preview).toContainText("実際の起動には異常時の条件が必要");
});

test("遅い上位連動試算を表示せず、親編集後の古い候補を適用しない", async ({
  page,
}) => {
  await local(page);
  await own(page);
  let release!: () => void;
  let started!: () => void;
  const received = new Promise<void>((resolve) => {
    started = resolve;
  });
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route(
    "**/definition-editor/nested-schedules",
    async (route) => {
      const response = await route.fetch();
      started();
      await gate;
      await route.fulfill({ response });
    },
    { times: 1 },
  );
  await trial(page, "/n/c");
  await received;
  await page.getByLabel("起算する運用日数", { exact: true }).fill("4");
  const response = page.waitForResponse(
    "**/definition-editor/nested-schedules",
  );
  release();
  await (await response).finished();
  await expect(page.getByText(/スケジュール上の対象 .*件/)).toHaveCount(0);
  await trial(page, "/n/c");
  await expect(page.getByText(/スケジュール上の対象 .*件/)).toBeVisible();
  await page.getByLabel("編集ネット").selectOption("/n");
  await page
    .getByRole("textbox", { name: "開始時刻 (st)", exact: true })
    .fill("1,10:00");
  await page.getByLabel("編集ネット").selectOption("/n/c");
  await expect(
    page.getByRole("button", { name: "簡単設定を下書きへ適用", exact: true }),
  ).toBeDisabled();
  await expect(page.getByText(/古い候補は適用できません/)).toBeVisible();
});

test("ネストの独自設定でも別タブの新しい保存を上書きしない", async ({
  page,
  context,
}) => {
  await local(page);
  await own(page);
  await apply(page);
  const other = await context.newPage();
  await open(other);
  await other.getByLabel("編集ネット").selectOption("/n/c");
  await other.getByText("スケジュール・カレンダー", { exact: true }).click();
  await other
    .getByRole("button", { name: "このルールを簡単設定で編集", exact: true })
    .click();
  await other.getByLabel("リンクする上位ルール番号", { exact: true }).fill("2");
  await expect(other.getByText(/指定した対応先がないため/)).toBeVisible();
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
  other.once("dialog", (dialog) => dialog.accept());
  await other
    .getByRole("button", { name: "保存済み下書きを読み直す", exact: true })
    .click();
  await expect(
    other.getByRole("textbox", { name: "上位ルール参照 (ln)", exact: true }),
  ).toHaveValue("1,1");
  await expect(
    other.getByRole("textbox", { name: "開始時刻 (st)", exact: true }),
  ).toHaveValue("1,10:00");
});
