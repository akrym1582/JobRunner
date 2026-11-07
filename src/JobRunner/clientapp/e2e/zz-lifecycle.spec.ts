import { test, expect, type Page } from "@playwright/test";
const api = "http://127.0.0.1:5181/api/jobrunner";
async function editor(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
}

test("複数ノードの原文コピペ・外部接続の確認・undoと保存復元", async ({
  page,
}) => {
  await editor(page);
  await page
    .getByRole("button", { name: "編集 受注データ集計", exact: true })
    .click();
  await page
    .getByRole("button", { name: "編集 在庫チェック", exact: true })
    .click({ modifiers: ["Control"] });
  await page
    .getByRole("button", { name: "選択をコピー（2件）", exact: true })
    .click();
  await page
    .getByRole("button", { name: "貼り付け候補を確認", exact: true })
    .click();
  const confirmation = page.getByRole("dialog", { name: "貼り付けの確認" });
  await expect(confirmation).toContainText("外部接続を除外");
  await confirmation
    .getByRole("button", { name: "確認して貼り付け", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "編集 受注データ集計_copy", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "編集 受注データ集計_copy", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 在庫チェック_copy", exact: true })
    .click();
  await page
    .getByRole("textbox", { name: "コメント", exact: true })
    .fill("複製後のコメント");
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toBeVisible();
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await page
    .getByRole("button", { name: "編集 在庫チェック_copy", exact: true })
    .click();
  await expect(
    page.getByRole("textbox", { name: "コメント", exact: true }),
  ).toHaveValue("複製後のコメント");
  await page
    .getByRole("button", { name: "検証・プレビュー", exact: true })
    .click();
  await expect(page.getByRole("heading", { name: /検証OK/ })).toBeVisible();
});

test("月表示で日付を上書きし、指定解除で親カレンダーへ戻る", async ({
  page,
}) => {
  await editor(page);
  await page.getByText("スケジュール・カレンダー", { exact: true }).click();
  await page.getByLabel("編集するカレンダー").selectOption("/運用暦/継承暦");
  const section = page.getByRole("region", { name: "カレンダー月編集" });
  await section.getByLabel("表示月").fill("2028-02");
  await section
    .getByRole("button", { name: "実効カレンダーを表示", exact: true })
    .click();
  await section.getByRole("button", { name: /^6 休業/ }).click();
  await section
    .getByRole("button", { name: "この日を稼働日", exact: true })
    .click();
  await section
    .getByRole("button", { name: "実効カレンダーを表示", exact: true })
    .click();
  await expect(section.getByRole("button", { name: /^6 稼働/ })).toContainText(
    "/運用暦/継承暦",
  );
  await section
    .getByRole("button", { name: "この日の指定を解除", exact: true })
    .click();
  await section
    .getByRole("button", { name: "実効カレンダーを表示", exact: true })
    .click();
  await expect(section.getByRole("button", { name: /^6 休業/ })).toContainText(
    "/運用暦 (cl曜日)",
  );
});

test("別ブラウザーの共有保存を上書きせず、遅い再読込中の編集も保持", async ({
  browser,
  request,
}) => {
  const document = await (
    await request.get(api + "/ui/definition-editor")
  ).json();
  const previous = await request.get(api + "/ui/definition-editor/shared");
  const revision =
    previous.status() === 204 ? 0 : (await previous.json()).revision;
  const runtime = await (await request.get(api + "/ui/runtime")).json();
  expect(
    (
      await request.put(api + "/ui/definition-editor/shared", {
        data: {
          revision,
          baseRevision: runtime.active.revision,
          base: document,
          draft: document.draft,
        },
      })
    ).ok(),
  ).toBeTruthy();
  const left = await browser.newContext();
  const right = await browser.newContext();
  try {
    const one = await left.newPage(),
      two = await right.newPage();
    for (const page of [one, two]) {
      page.on("dialog", (dialog) => void dialog.accept());
      await editor(page);
      await page
        .getByRole("button", { name: "Storageの下書きを読み込む", exact: true })
        .click();
      await expect(
        page.getByText(/Storageの下書き版.*を読み込みました/),
      ).toBeVisible();
      await page
        .getByRole("button", { name: "編集 受注データ集計", exact: true })
        .click();
    }
    await one
      .getByRole("textbox", { name: "コメント", exact: true })
      .fill("先に共有保存");
    await one
      .getByRole("button", { name: "Storageへ下書きを保存", exact: true })
      .click();
    await expect(
      one.getByText(/Storageへ下書き版.*を保存しました/),
    ).toBeVisible();
    await two
      .getByRole("textbox", { name: "コメント", exact: true })
      .fill("競合でも消さない");
    await two
      .getByRole("button", { name: "Storageへ下書きを保存", exact: true })
      .click();
    await expect(
      two.getByText(/別の画面が共有下書きを保存しました/),
    ).toBeVisible();
    await expect(
      two.getByRole("textbox", { name: "コメント", exact: true }),
    ).toHaveValue("競合でも消さない");
    let release!: () => void;
    const waiting = new Promise<void>((resolve) => {
      release = resolve;
    });
    let intercepted!: () => void;
    const reached = new Promise<void>((resolve) => {
      intercepted = resolve;
    });
    await two.route("**/definition-editor/shared", async (route) => {
      if (route.request().method() !== "GET") return route.continue();
      const response = await route.fetch();
      intercepted();
      await waiting;
      await route.fulfill({ response });
    });
    await two
      .getByRole("button", { name: "Storageの下書きを読み込む", exact: true })
      .click();
    await reached;
    await two
      .getByRole("textbox", { name: "コメント", exact: true })
      .fill("読込待ちの新しい編集");
    release();
    await expect(two.getByText(/読込中に編集されたため/)).toBeVisible();
    await expect(
      two.getByRole("textbox", { name: "コメント", exact: true }),
    ).toHaveValue("読込待ちの新しい編集");
  } finally {
    await left.close();
    await right.close();
  }
});

test("全体停止の適用表示・停止中の一式反映・再開", async ({
  page,
  request,
}) => {
  page.on("dialog", (dialog) => void dialog.accept());
  const runtime = await (await request.get(api + "/ui/runtime")).json();
  const originalHash = runtime.active.definitionHash;
  try {
    await editor(page);
    await page
      .getByRole("button", { name: "全体を一時停止", exact: true })
      .click();
    await expect(
      page.getByText("全体停止を適用済み", { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: "全体を再開", exact: true }),
    ).toBeEnabled();
    await page
      .getByRole("button", { name: "編集 受注データ集計", exact: true })
      .click();
    await page
      .getByRole("textbox", { name: "コメント", exact: true })
      .fill("運用反映テスト");
    await page
      .getByRole("button", { name: "運用反映の差分を確認", exact: true })
      .click();
    const dialog = page.getByRole("dialog", { name: "運用反映の確認" });
    await expect(dialog).toContainText("通常予定");
    await dialog
      .getByRole("button", { name: "確認して運用に反映", exact: true })
      .click();
    await expect(
      page.getByText(/運用版.*への反映が完了しました/),
    ).toBeVisible();
    expect(
      (await (await request.get(api + "/ui/runtime")).json()).control.paused,
    ).toBe(true);
    const current = await (
      await request.get(api + "/ui/definition-editor")
    ).json();
    expect(
      current.draft.networks
        .flatMap(
          (n: { nodes: { name: string; attributes: { cm?: string[] } }[] }) =>
            n.nodes,
        )
        .find((n: { name: string }) => n.name === "受注データ集計").attributes
        .cm,
    ).toEqual(["運用反映テスト"]);
    await page.getByRole("button", { name: "全体を再開", exact: true }).click();
    await expect(
      page.getByText("再開を適用済み", { exact: true }),
    ).toBeVisible();
  } finally {
    const id = crypto.randomUUID();
    await request.post(api + "/operations", {
      data: { id, kind: "Activate", target: originalHash },
    });
    await expect
      .poll(
        async () =>
          (await (await request.get(api + "/operations/" + id)).json()).status,
      )
      .toBe("Completed");
    const status = await (await request.get(api + "/ui/runtime")).json();
    if (status.control.paused) {
      const resume = crypto.randomUUID();
      await request.post(api + "/operations", {
        data: {
          id: resume,
          kind: "ResumeAll",
          expectedControlRevision: status.control.revision,
        },
      });
      await expect
        .poll(
          async () =>
            (await (await request.get(api + "/operations/" + resume)).json())
              .status,
        )
        .toBe("Completed");
    }
  }
});
