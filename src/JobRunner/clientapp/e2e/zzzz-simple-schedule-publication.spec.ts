import { test, expect } from "@playwright/test";
const api = "http://127.0.0.1:5181/api/jobrunner";

test("簡単設定の標準属性を確認して運用反映し、試算と同じ確定世代を生成する", async ({
  page,
  request,
}) => {
  const original = (await (await request.get(api + "/ui/runtime")).json())
    .active.definitionHash;
  const now = new Date();
  const first = new Date(
    Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1),
  );
  const end = new Date(
    Date.UTC(first.getUTCFullYear(), first.getUTCMonth() + 2, 1),
  );
  const from = first.toISOString().slice(0, 10),
    until = end.toISOString().slice(0, 10);
  page.on("dialog", (dialog) => void dialog.accept());
  try {
    await page.goto("/");
    await page.getByRole("button", { name: "定義編集", exact: true }).click();
    await expect(page.getByLabel("編集ネット")).toBeVisible();
    await page.getByText("ローカル定義を開く", { exact: true }).click();
    await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
      name: "offset.ajs",
      mimeType: "application/octet-stream",
      buffer: Buffer.from(
        'unit=calendar;{ty=g;cl=sa;cl=su;}unit=offset;{ty=n;ha=y;sd=0,ud;unit=j;{ty=j;te="true";}}',
      ),
    });
    await page
      .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
      .click();
    await expect(page.getByLabel("編集ネット")).toHaveValue("/offset");
    await page.getByText("スケジュール・カレンダー", { exact: true }).click();
    await page
      .getByRole("button", { name: "簡単設定でルールを追加", exact: true })
      .click();
    await page
      .getByLabel("開始年月（省略は登録月）", { exact: true })
      .fill(from.slice(0, 7));
    await page.getByLabel("毎月の暦日", { exact: true }).fill("20");
    await page.getByLabel("起算する運用日数", { exact: true }).fill("3");
    await page
      .getByLabel("簡単設定のカレンダー", { exact: true })
      .selectOption("/calendar");
    await page.getByLabel("仮の登録基準日", { exact: true }).fill(from);
    await page.getByLabel("試算開始業務日", { exact: true }).fill(from);
    await page
      .getByLabel("試算終了業務日（含まない）", { exact: true })
      .fill(until);
    const previewed = page.waitForResponse("**/definition-editor/schedules");
    await page
      .getByRole("button", { name: "下書きを試算", exact: true })
      .click();
    const points = (await (await previewed).json()).points;
    expect(points).toHaveLength(2);
    await page
      .getByRole("button", { name: "簡単設定を下書きへ適用", exact: true })
      .click();
    await expect(
      page.getByText("ブラウザーに保存済み", { exact: true }),
    ).toBeVisible();
    expect(
      (await (await request.get(api + "/ui/runtime")).json()).active
        .definitionHash,
    ).toBe(original);
    await page
      .getByRole("button", { name: "運用反映の差分を確認", exact: true })
      .click();
    const dialog = page.getByRole("dialog", { name: "運用反映の確認" });
    await expect(dialog).toBeVisible();
    await dialog
      .getByRole("button", { name: "確認して運用に反映", exact: true })
      .click();
    await expect(
      page.getByText(/運用版.*への反映が完了しました/),
    ).toBeVisible();
    const active = (await (await request.get(api + "/ui/runtime")).json())
      .active;
    await expect
      .poll(async () => {
        const executions = await (
          await request.get(api + "/executions")
        ).json();
        return executions
          .filter(
            (o: {
              id: string;
              rootId: string;
              rootPath: string;
              generationRevision: number;
            }) =>
              o.id === o.rootId &&
              o.rootPath === "/offset" &&
              o.generationRevision === active.revision,
          )
          .map((o: { businessDate: string; due: string; state: string }) => ({
            businessDate: o.businessDate,
            due: o.due,
            state: o.state,
          }))
          .sort((a: { businessDate: string }, b: { businessDate: string }) =>
            a.businessDate.localeCompare(b.businessDate),
          );
      })
      .toEqual(
        points.map((p: { businessDate: string; due: string }) => ({
          businessDate: p.businessDate,
          due: p.due,
          state: "Held",
        })),
      );
  } finally {
    if (
      (await (await request.get(api + "/ui/runtime")).json()).active
        .definitionHash !== original
    ) {
      const id = crypto.randomUUID();
      expect(
        (
          await request.post(api + "/operations", {
            data: { id, kind: "Activate", target: original },
          })
        ).ok(),
      ).toBeTruthy();
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
