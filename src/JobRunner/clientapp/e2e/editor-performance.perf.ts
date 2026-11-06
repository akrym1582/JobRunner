import { test, expect } from "@playwright/test";
import { appendFile } from "node:fs/promises";
import { performance } from "node:perf_hooks";

declare global {
  interface Window {
    editorPerf: { saves: number[]; longTasks: number[] };
  }
}
const endpoint = "http://127.0.0.1:5181/api/jobrunner/ui/definition-editor";
function fixture(jobs: number, networks: number) {
  let text = "/* isolated performance input */\nunit=g;{ty=g;";
  for (let n = 0; n < networks; n++) {
    text += `unit=n${n};{ty=n;sd=0,ud;sz=100*100;`;
    for (let j = 0; j < jobs; j++) {
      text += `el=j${j},j,+${80 + (j % 100) * 160} +${48 + Math.floor(j / 100) * 96};`;
      if (j % 2) text += `ar=(f=j${j - 1},t=j${j});`;
    }
    for (let j = 0; j < jobs; j++)
      text += `unit=j${j};{ty=j;te="echo preserved ${j}";cm="原文";}`;
    text += "}";
  }
  return text + "}";
}
for (const [jobs, networks] of [
  [200, 1],
  [1000, 1],
  [200, 25],
]) {
  test(`editor performance ${jobs} x ${networks}`, async ({
    page,
    request,
    context,
  }) => {
    await page.addInitScript(() => {
      window.editorPerf = { saves: [], longTasks: [] };
      const original = IDBDatabase.prototype.transaction;
      IDBDatabase.prototype.transaction = function (
        this: IDBDatabase,
        ...args: Parameters<typeof original>
      ) {
        const started = performance.now(),
          tx = original.apply(this, args);
        if (
          this.name === "jobrunner-definition-editor" &&
          args[1] === "readwrite"
        )
          tx.addEventListener("complete", () =>
            window.editorPerf.saves.push(performance.now() - started),
          );
        return tx;
      };
      new PerformanceObserver((list) =>
        window.editorPerf.longTasks.push(
          ...list.getEntries().map((e) => e.duration),
        ),
      ).observe({ type: "longtask", buffered: true });
    });
    const source = fixture(jobs, networks),
      rows: object[] = [];
    const input = {
      sources: [
        {
          mapping: {
            kind: "Unit",
            blobPath: "performance.ajs",
            targetPath: "/",
            encoding: "utf-8",
          },
          base64: Buffer.from(source).toString("base64"),
        },
      ],
    };
    const first = await request.post(endpoint + "/local", { data: input });
    expect(first.ok()).toBe(true);
    const document = await first.json();
    for (const route of ["local", "preview", "export", "schedules"]) {
      const body =
        route === "local"
          ? input
          : route === "schedules"
            ? {
                draft: document.draft,
                networkId: "/g/n0",
                anchor: "2026-01-01",
                from: "2026-01-01",
                until: "2027-01-01",
              }
            : document.draft;
      for (let sample = 0; sample < 3; sample++) {
        const started = performance.now(),
          response = await request.post(`${endpoint}/${route}`, { data: body });
        const bytes = (await response.body()).length;
        expect(response.ok()).toBe(true);
        rows.push({
          operation: `http-${route}`,
          sample,
          milliseconds: performance.now() - started,
          responseBytes: bytes,
        });
      }
    }
    await page.goto("/");
    await page.getByRole("button", { name: "定義編集", exact: true }).click();
    await expect(page.getByLabel("編集ネット")).toBeVisible();
    await page.getByText("ローカル定義を開く", { exact: true }).click();
    await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
      name: "performance.ajs",
      mimeType: "application/octet-stream",
      buffer: Buffer.from(source),
    });
    const cdp = await context.newCDPSession(page);
    await cdp.send("Performance.enable");
    const before = await cdp.send("Performance.getMetrics");
    const started = performance.now();
    page.once("dialog", (d) => d.accept());
    await page
      .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
      .click();
    await expect(page.getByLabel("編集ネット")).toHaveValue("/g/n0");
    await expect(page.locator(".editor-unit-list button")).toHaveCount(jobs);
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
    rows.push({
      operation: "import-and-render",
      milliseconds: performance.now() - started,
    });
    await expect(
      page.getByText("ブラウザーに保存済み", { exact: true }),
    ).toBeVisible();
    await page.locator(".editor-unit-list button").first().click();
    for (let sample = 0; sample < 3; sample++) {
      const saveCount = await page.evaluate(
        () => window.editorPerf.saves.length,
      );
      const start = performance.now();
      await page
        .getByRole("textbox", { name: "コマンド", exact: true })
        .fill(`echo edited ${sample}`);
      await page.evaluate(
        () =>
          new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          ),
      );
      rows.push({
        operation: "edit-and-paint",
        sample,
        milliseconds: performance.now() - start,
      });
      await expect
        .poll(() => page.evaluate(() => window.editorPerf.saves.length))
        .toBeGreaterThan(saveCount);
      await expect(
        page.getByText("ブラウザーに保存済み", { exact: true }),
      ).toBeVisible();
      rows.push({
        operation: "edit-and-saved",
        sample,
        milliseconds: performance.now() - start,
      });
    }
    const after = await cdp.send("Performance.getMetrics");
    const metrics = (value: typeof after) =>
      Object.fromEntries(
        value.metrics
          .filter((m) =>
            [
              "JSHeapUsedSize",
              "JSHeapTotalSize",
              "Nodes",
              "TaskDuration",
              "LayoutDuration",
              "RecalcStyleDuration",
            ].includes(m.name),
          )
          .map((m) => [m.name, m.value]),
      );
    const browser = await page.evaluate(() => ({
      ...window.editorPerf,
      domNodes: document.querySelectorAll("*").length,
    }));
    const result = {
      label: process.env.EDITOR_BENCH_LABEL ?? "unspecified",
      jobs,
      networks,
      sourceBytes: Buffer.byteLength(source),
      browser: await page.evaluate(() => navigator.userAgent),
      production: !!process.env.EDITOR_BENCH_PRODUCTION,
      before: metrics(before),
      after: metrics(after),
      observations: browser,
      rows,
    };
    await appendFile(
      process.env.EDITOR_BENCH_OUTPUT ??
        "/tmp/editor-browser-performance.jsonl",
      JSON.stringify(result) + "\n",
    );
    console.log(
      JSON.stringify({ jobs, networks, metrics: result.after, timings: rows }),
    );
  });
}
