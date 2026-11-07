import { test, expect, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

const api = "http://127.0.0.1:5181/api/jobrunner";
function add(date: string, days: number) {
  const value = new Date(date + "T00:00:00Z");
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
}
function dateValue(date: string) {
  return date.split("-").map(Number).join("/");
}
async function trial(page: Page, network: string, from: string) {
  await page.getByLabel("編集ネット").selectOption(network);
  await page.getByLabel("仮の登録基準日").fill(from);
  await page.getByLabel("試算開始業務日").fill(from);
  await page.getByLabel("試算終了業務日（含まない）").fill(add(from, 3));
  await page.getByRole("button", { name: "下書きを試算", exact: true }).click();
  return page.getByRole("region", { name: "下書き予定試算", exact: true });
}
async function saved(page: Page) {
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
}

test("月表示で指定した祝日を試算・保存・再取込・運用反映後の確定予定から除外する", async ({
  page,
  request,
}, info) => {
  const original = await (await request.get(api + "/ui/runtime")).json();
  const originalHash = original.active.definitionHash;
  // 実時計は変更せず、次週以降の月曜日から3日間を固定した入力にします。
  const today = new Intl.DateTimeFormat("sv-SE", {
    timeZone: "Asia/Tokyo",
  }).format(new Date());
  let monday = add(today, 7);
  while (new Date(monday + "T00:00:00Z").getUTCDay() !== 1)
    monday = add(monday, 1);
  const holiday = add(monday, 1),
    wednesday = add(monday, 2);
  const [year, month, day] = monday.split("-").map(Number);
  let workingIndex = 0;
  for (let i = 1; i <= day; i++) {
    const weekday = new Date(Date.UTC(year, month - 1, i)).getUTCDay();
    if (weekday !== 0 && weekday !== 6) workingIndex++;
  }
  const working = "/業務/運用日実行",
    skip = "/業務/休日中止";
  const source = `unit=営業カレンダー;{ty=g;op=mo;op=tu;op=we;op=th;op=fr;cl=sa;cl=su;unit=本番暦;{ty=g;}}
unit=業務;{ty=g;
unit=運用日実行;{ty=n;jc="/営業カレンダー/本番暦";sd=1,${year}/${month}/*${workingIndex};st=09:00;cy=(1,d);sh=no;el=処理,j,+80 +48;unit=処理;{ty=j;te="true";}}
unit=休日中止;{ty=n;jc="/営業カレンダー/本番暦";sd=1,${dateValue(monday)};st=09:00;cy=(1,d);sh=ca;el=処理,j,+80 +48;unit=処理;{ty=j;te="true";}}}`;
  page.on("dialog", (dialog) => void dialog.accept());
  try {
    await page.goto("/");
    await page.getByRole("button", { name: "定義編集", exact: true }).click();
    await expect(page.getByLabel("編集ネット")).toBeVisible();
    await page.getByText("ローカル定義を開く", { exact: true }).click();
    await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
      name: "calendar.ajs",
      mimeType: "application/octet-stream",
      buffer: Buffer.from(source),
    });
    await page
      .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
      .click();
    await expect(page.getByLabel("編集ネット")).toHaveValue(working);
    await saved(page);
    await page.getByText("スケジュール・カレンダー", { exact: true }).click();
    const preview = await trial(page, working, monday);
    await expect(preview.locator("tbody tr")).toHaveCount(3);
    await expect(preview.locator("tbody")).toContainText(holiday);
    await page.getByLabel("編集するカレンダー").selectOption("/営業カレンダー");
    const calendar = page.getByRole("region", { name: "カレンダー月編集" });
    await calendar.getByLabel("表示月").fill(holiday.slice(0, 7));
    await calendar
      .getByRole("button", { name: "実効カレンダーを表示", exact: true })
      .click();
    await calendar
      .getByRole("button", {
        name: new RegExp(`^${Number(holiday.slice(-2))} 稼働`),
      })
      .click();
    await calendar
      .getByRole("button", { name: "この日を休業日", exact: true })
      .click();
    await saved(page);
    await calendar
      .getByRole("button", { name: "実効カレンダーを表示", exact: true })
      .click();
    await expect(
      calendar.getByRole("button", {
        name: new RegExp(`^${Number(holiday.slice(-2))} 休業`),
      }),
    ).toContainText("/営業カレンダー (cl日付)");
    if (process.env.CALENDAR_SCREENSHOTS) {
      await calendar.screenshot({
        path: resolve(
          "../../../docs/spec/scheduler/web-ui/images/calendar-month-editor.png",
        ),
      });
    }
    // 子グループ参照は上位の日付例外を継承し、運用日指定とsh=caの双方で除外します。
    for (const network of [working, skip]) {
      const result = await trial(page, network, monday);
      await expect(result.locator("tbody tr")).toHaveCount(2);
      await expect(result.locator("tbody tr td:nth-child(2)")).toHaveText([
        monday,
        wednesday,
      ]);
    }
    if (process.env.CALENDAR_SCREENSHOTS) {
      await preview.screenshot({
        path: resolve(
          "../../../docs/spec/scheduler/web-ui/images/calendar-holiday-preview.png",
        ),
      });
    }
    expect(
      (await (await request.get(api + "/ui/runtime")).json()).active
        .definitionHash,
    ).toBe(originalHash);
    await page
      .getByRole("button", { name: "検証・プレビュー", exact: true })
      .click();
    await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
    const downloading = page.waitForEvent("download");
    await page
      .getByRole("button", { name: "ダウンロード", exact: true })
      .click();
    const path = info.outputPath("calendar-holiday.ajs");
    await (await downloading).saveAs(path);
    const bytes = await readFile(path);
    const imported = await request.post(api + "/ui/definition-editor/local", {
      data: {
        sources: [
          {
            mapping: {
              kind: "Unit",
              blobPath: "calendar-holiday.ajs",
              encoding: "utf-8",
              targetPath: "/",
            },
            base64: bytes.toString("base64"),
          },
        ],
      },
    });
    expect(imported.ok()).toBeTruthy();
    const reimported = await imported.json();
    expect(
      reimported.draft.calendars.find(
        (c: { id: string }) => c.id === "/営業カレンダー",
      ).attributes.cl,
    ).toContain(dateValue(holiday));
    await saved(page);
    await page.reload();
    await page.getByRole("button", { name: "定義編集", exact: true }).click();
    await page.getByText("スケジュール・カレンダー", { exact: true }).click();
    const restored = await trial(page, working, monday);
    await expect(restored.locator("tbody tr td:nth-child(2)")).toHaveText([
      monday,
      wednesday,
    ]);
    await page
      .getByRole("button", { name: "運用反映の差分を確認", exact: true })
      .click();
    const confirmation = page.getByRole("dialog", { name: "運用反映の確認" });
    await expect(confirmation).toBeVisible();
    await confirmation
      .getByRole("button", { name: "確認して運用に反映", exact: true })
      .click();
    await expect(
      page.getByText(/運用版.*への反映が完了しました/),
    ).toBeVisible();
    const active = (await (await request.get(api + "/ui/runtime")).json())
      .active;
    expect(active.definitionHash).not.toBe(originalHash);
    for (const network of [working, skip]) {
      await expect
        .poll(async () => {
          const values = await (await request.get(api + "/executions")).json();
          return values
            .filter(
              (o: {
                id: string;
                rootId: string;
                rootPath: string;
                generationRevision: number;
              }) =>
                o.id === o.rootId &&
                o.rootPath === network &&
                o.generationRevision === active.revision,
            )
            .map((o: { businessDate: string }) => o.businessDate)
            .sort();
        })
        .toEqual([monday, wednesday]);
    }
  } finally {
    const runtime = await (await request.get(api + "/ui/runtime")).json();
    if (runtime.active.definitionHash !== originalHash) {
      const id = crypto.randomUUID();
      const response = await request.post(api + "/operations", {
        data: { id, kind: "Activate", target: originalHash },
      });
      expect(response.ok()).toBeTruthy();
      await expect
        .poll(
          async () =>
            (await (await request.get(api + "/operations/" + id)).json())
              .status,
        )
        .toBe("Completed");
    }
  }
});
