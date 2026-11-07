import { test, expect, type Page } from "@playwright/test";

const source =
  '/* original z.ajs */\nunit=z;{ty=g;unit=net;{ty=n;sd=0,ud;sz=3*1;el=job,j,+80 +48;unit=job;{ty=j;te="echo original-z";}}}unit=keep;{ty=n;sd=0,ud;}';
async function openLocal(page: Page) {
  await page.goto("/");
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット")).toBeVisible();
  await page.getByText("ローカル定義を開く", { exact: true }).click();
  await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
    name: "z.ajs",
    mimeType: "application/octet-stream",
    buffer: Buffer.from(source),
  });
  page.on("dialog", (dialog) => void dialog.accept());
  await page
    .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット")).toHaveValue("/z/net");
  await saved(page);
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

test("貼付でファイル順が変わっても変更前の原文とファイル数を維持する", async ({
  page,
}) => {
  await openLocal(page);
  await page.getByText("グループ・Unitファイルを作成", { exact: true }).click();
  await page.getByLabel("新規ファイル名", { exact: true }).fill("a.ajs");
  await page.getByLabel("初期グループ名", { exact: true }).fill("added");
  await page
    .getByRole("button", { name: "下書きにファイルを追加", exact: true })
    .click();
  await expect(
    page
      .getByRole("status")
      .filter({ hasText: "ローカル入力を基にした下書き・2ファイル" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "編集 job", exact: true }).click();
  await page
    .getByRole("button", { name: "選択をコピー（1件）", exact: true })
    .click();
  await page
    .getByRole("button", { name: "貼り付け候補を確認", exact: true })
    .click();
  await page
    .getByRole("button", { name: "確認して貼り付け", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "編集 job_copy", exact: true }),
  ).toBeVisible();
  await preview(page);
  const files = page.getByRole("combobox", {
    name: "原文ファイル",
    exact: true,
  });
  await expect(files.locator("option").first()).toContainText("a.ajs");
  await files.selectOption({
    label: await files
      .locator("option")
      .filter({ hasText: "a.ajs" })
      .innerText(),
  });
  await expect
    .soft(page.getByLabel("変更前の原文", { exact: true }))
    .toHaveText("", { timeout: 1000 });
  await expect(page.getByLabel("変更後の原文", { exact: true })).toContainText(
    "unit=added;",
  );
  await files.selectOption({
    label: await files
      .locator("option")
      .filter({ hasText: "z.ajs" })
      .innerText(),
  });
  await expect
    .soft(page.getByLabel("変更前の原文", { exact: true }))
    .toHaveText(source, { timeout: 1000 });
  await expect(page.getByLabel("変更後の原文", { exact: true })).toContainText(
    "job_copy",
  );
  await expect
    .soft(
      page
        .getByRole("status")
        .filter({ hasText: "ローカル入力を基にした下書き・2ファイル" }),
    )
    .toBeVisible();
  await expect.soft(page.locator(".editor-footer")).toContainText("2 ファイル");
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await preview(page);
  await expect(
    page
      .getByRole("status")
      .filter({ hasText: "ローカル入力を基にした下書き・2ファイル" }),
  ).toBeVisible();
  await files.selectOption({
    label: await files
      .locator("option")
      .filter({ hasText: "z.ajs" })
      .innerText(),
  });
  await expect(page.getByLabel("変更前の原文", { exact: true })).toHaveText(
    source,
  );
});

test("undoとredoの現在の内容だけを保存済みと表示し、遅い書込み後の再読込で削除を維持する", async ({
  page,
}) => {
  await openLocal(page);
  await page.getByText("グループの名前変更・削除", { exact: true }).click();
  await page.getByLabel("操作するグループ").selectOption("/z");
  await page
    .getByRole("button", { name: "グループと子孫を削除", exact: true })
    .click();
  await expect(page.getByLabel("編集ネット").locator("option")).toHaveCount(1);
  await saved(page);
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await expect(page.getByLabel("編集ネット").locator("option")).toHaveCount(2);
  await saved(page);
  await page.evaluate(() => {
    const state = { blocked: false, release: false };
    (window as unknown as { draftWriteGate: typeof state }).draftWriteGate =
      state;
    const success = Object.getOwnPropertyDescriptor(
      IDBRequest.prototype,
      "onsuccess",
    )!;
    let armed = true;
    Object.defineProperty(IDBRequest.prototype, "onsuccess", {
      ...success,
      set(handler: ((event: Event) => void) | null) {
        const request = this as IDBRequest,
          tx = request.transaction;
        if (
          armed &&
          tx?.mode === "readwrite" &&
          tx.db.name === "jobrunner-definition-editor"
        ) {
          armed = false;
          success.set!.call(request, (event: Event) => {
            state.blocked = true;
            // 比較用getの成功後、put前で止めます。要求を継続してトランザクションを有効に保ちます。
            function wait() {
              if (state.release) handler?.call(request, event);
              else
                tx!
                  .objectStore("drafts")
                  .get("write-test-keepalive").onsuccess = wait;
            }
            wait();
          });
        } else success.set!.call(request, handler);
      },
    });
  });
  const observed = await page.evaluate(async () => {
    const redo = [
      ...document.querySelectorAll<HTMLButtonElement>("button"),
    ].find((button) => button.getAttribute("aria-label") === "やり直す")!;
    redo.click();
    // UIの編集がコミットされた直後、受動Effectの保存状態更新より先も検査します。
    await Promise.resolve();
    return {
      count: document.querySelectorAll('[aria-label="編集ネット"] option')
        .length,
      status: document.querySelector(".editor-save")?.textContent,
    };
  });
  expect.soft(observed).toEqual({ count: 1, status: "下書きを保存中…" });
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as unknown as { draftWriteGate: { blocked: boolean } })
            .draftWriteGate.blocked,
      ),
    )
    .toBe(true);
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toHaveCount(0);
  // 保留中に元のスナップショットへ戻っても、先行保存より後の現在版が確定するまで待ちます。
  await page.getByRole("button", { name: "元に戻す", exact: true }).click();
  await page.getByRole("button", { name: "やり直す", exact: true }).click();
  await expect(
    page.getByText("ブラウザーに保存済み", { exact: true }),
  ).toHaveCount(0);
  await page.evaluate(() => {
    (
      window as unknown as { draftWriteGate: { release: boolean } }
    ).draftWriteGate.release = true;
  });
  await saved(page);
  await page.reload();
  await page.getByRole("button", { name: "定義編集", exact: true }).click();
  await expect(page.getByLabel("編集ネット").locator("option")).toHaveCount(1);
  await preview(page);
  await expect(
    page.getByLabel("変更後の原文", { exact: true }),
  ).not.toContainText("unit=z");
});
