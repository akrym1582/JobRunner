import { test, expect } from "@playwright/test";
import { appendFile, writeFile } from "node:fs/promises";
import { performance } from "node:perf_hooks";

declare global {
  interface Window {
    interactionPerf: {
      transactions: number[];
      puts: number[];
      strings: { milliseconds: number; characters: number }[];
      longTasks: number[];
      inputAt?: number;
      paintedMs?: number;
      savedMs?: number;
    };
  }
}
function fixture(jobs: number) {
  let text =
    "/* unchanged bytes */\nunit=g;{ty=g;unit=net;{ty=n;ha=y;sd=0,ud;sz=100*100;";
  for (let j = 0; j < jobs; j++) {
    text += `el=j${j},j,+${80 + (j % 100) * 160} +${48 + Math.floor(j / 100) * 96};`;
    if (j % 2) text += `ar=(f=j${j - 1},t=j${j});`;
  }
  for (let j = 0; j < jobs; j++)
    text += `unit=j${j};{ty=j;te="true";cm="保持 ${j}";}`;
  return text + "}}";
}
for (const jobs of [200, 1000, 9000]) {
  test(`editor interaction ${jobs} jobs`, async ({ page, context }) => {
    await page.addInitScript(() => {
      window.interactionPerf = {
        transactions: [],
        puts: [],
        strings: [],
        longTasks: [],
      };
      const transaction = IDBDatabase.prototype.transaction;
      IDBDatabase.prototype.transaction = function (
        ...args: Parameters<typeof transaction>
      ) {
        const start = performance.now(),
          tx = transaction.apply(this, args);
        if (
          this.name === "jobrunner-definition-editor" &&
          args[1] === "readwrite"
        )
          tx.addEventListener("complete", () =>
            window.interactionPerf.transactions.push(performance.now() - start),
          );
        return tx;
      };
      const put = IDBObjectStore.prototype.put;
      IDBObjectStore.prototype.put = function (
        ...args: Parameters<typeof put>
      ) {
        const start = performance.now(),
          result = put.apply(this, args);
        if (this.transaction.db.name === "jobrunner-definition-editor")
          window.interactionPerf.puts.push(performance.now() - start);
        return result;
      };
      const stringify = JSON.stringify;
      JSON.stringify = function (...args: Parameters<typeof stringify>) {
        const start = performance.now(),
          result = stringify(...args);
        if (args[0]?.networks || (args[0]?.version === 1 && args[0]?.base))
          window.interactionPerf.strings.push({
            milliseconds: performance.now() - start,
            characters: result.length,
          });
        return result;
      };
      new PerformanceObserver((list) =>
        window.interactionPerf.longTasks.push(
          ...list.getEntries().map((e) => e.duration),
        ),
      ).observe({ type: "longtask", buffered: true });
    });
    const source = fixture(jobs),
      rows: object[] = [];
    await page.goto("/");
    await page.getByRole("button", { name: "定義編集", exact: true }).click();
    await expect(page.getByLabel("編集ネット")).toBeVisible();
    await page.getByText("ローカル定義を開く", { exact: true }).click();
    await page.getByLabel("定義ファイル", { exact: true }).setInputFiles({
      name: "performance.ajs",
      mimeType: "application/octet-stream",
      buffer: Buffer.from(source),
    });
    page.on("dialog", (d) => void d.accept());
    const cdp = await context.newCDPSession(page);
    await cdp.send("Performance.enable");
    const paint = () =>
      page.evaluate(
        () =>
          new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          ),
      );
    const start = performance.now();
    await page
      .getByRole("button", { name: "ローカル定義を読み込む", exact: true })
      .click();
    await expect(page.getByLabel("編集ネット")).toHaveValue("/g/net");
    await expect(page.locator(".editor-unit-list button")).toHaveCount(jobs);
    await paint();
    rows.push({
      operation: "load-and-paint",
      milliseconds: performance.now() - start,
    });
    await expect(page.locator(".editor-save")).toHaveText(
      "ブラウザーに保存済み",
    );
    const selectStart = performance.now();
    await page.locator(".editor-unit-list button").first().click();
    await paint();
    rows.push({
      operation: "select-and-paint",
      milliseconds: performance.now() - selectStart,
    });
    const command = await page
      .getByRole("textbox", { name: "コマンド", exact: true })
      .elementHandle();
    expect(command).not.toBeNull();
    await page.evaluate(() => {
      document.addEventListener(
        "input",
        (event) => {
          if (
            !(event.target instanceof HTMLTextAreaElement) ||
            !event.target.closest(".editor-properties")
          )
            return;
          const sample = window.interactionPerf;
          sample.inputAt = performance.now();
          requestAnimationFrame(() =>
            requestAnimationFrame(() => {
              sample.paintedMs = performance.now() - sample.inputAt!;
            }),
          );
        },
        true,
      );
      const status = document.querySelector(".editor-save")!;
      new MutationObserver(() => {
        const sample = window.interactionPerf;
        if (
          sample.inputAt !== undefined &&
          status.textContent === "ブラウザーに保存済み"
        )
          sample.savedMs = performance.now() - sample.inputAt;
      }).observe(status, {
        childList: true,
        characterData: true,
        subtree: true,
      });
    });
    if (process.env.EDITOR_INTERACTION_PROFILE) {
      await cdp.send("Profiler.enable");
      await cdp.send("Profiler.start");
    }
    for (let sample = 0; sample < 3; sample++) {
      const observations = await page.evaluate(() => {
        const previous = window.interactionPerf;
        window.interactionPerf = {
          transactions: [],
          puts: [],
          strings: [],
          longTasks: [],
        };
        return previous;
      });
      if (sample === 0)
        rows.push({ operation: "load-observations", observations });
      const before = await cdp.send("Performance.getMetrics");
      const editStart = performance.now();
      await command!.fill(`true # edited ${sample}`);
      await paint();
      const paintMs = performance.now() - editStart;
      await expect
        .poll(() =>
          page.evaluate(() => window.interactionPerf.transactions.length),
        )
        .toBeGreaterThan(0);
      await expect(page.locator(".editor-save")).toHaveText(
        "ブラウザーに保存済み",
      );
      const savedMs = performance.now() - editStart;
      const after = await cdp.send("Performance.getMetrics");
      const metrics = Object.fromEntries(
        after.metrics.map((m) => [
          m.name,
          m.value - (before.metrics.find((b) => b.name === m.name)?.value ?? 0),
        ]),
      );
      rows.push({
        operation: "edit",
        sample,
        paintMs,
        savedMs,
        metrics,
        observations: await page.evaluate(() => window.interactionPerf),
      });
      const observed = await page.evaluate(() => window.interactionPerf);
      expect(observed.paintedMs).toBeGreaterThan(0);
      expect(observed.savedMs).toBeGreaterThan(0);
      console.log(
        JSON.stringify({
          jobs,
          sample,
          paintMs,
          savedMs,
          inputPaintMs: observed.paintedMs,
          inputSavedMs: observed.savedMs,
        }),
      );
    }
    if (process.env.EDITOR_INTERACTION_PROFILE) {
      const { profile } = await cdp.send("Profiler.stop");
      await writeFile(
        `${process.env.EDITOR_INTERACTION_PROFILE}-${jobs}.cpuprofile`,
        JSON.stringify(profile),
      );
    }
    // 測定が古い保存表示を拾っていないことを、実データと再読込でも確認します。
    const stored = await page.evaluate(
      () =>
        new Promise<string>((resolve, reject) => {
          const open = indexedDB.open("jobrunner-definition-editor", 2);
          open.onerror = () => reject(open.error);
          open.onsuccess = () => {
            const tx = open.result.transaction("drafts"),
              request = tx.objectStore("drafts").get("current");
            tx.oncomplete = () => {
              open.result.close();
              resolve(
                request.result.draft.networks[0].nodes[0].attributes.te[0],
              );
            };
          };
        }),
    );
    expect(stored).toBe("true # edited 2");
    const metrics = await cdp.send("Performance.getMetrics");
    const result = {
      label: process.env.EDITOR_BENCH_LABEL,
      jobs,
      production: !!process.env.EDITOR_BENCH_PRODUCTION,
      sourceBytes: Buffer.byteLength(source),
      browser: await page.evaluate(() => navigator.userAgent),
      domNodes: await page.locator("*").count(),
      metrics: Object.fromEntries(
        metrics.metrics.map((m) => [m.name, m.value]),
      ),
      rows,
    };
    await appendFile(
      process.env.EDITOR_BENCH_OUTPUT ?? "/tmp/editor-interaction.jsonl",
      JSON.stringify(result) + "\n",
    );
    await page.reload();
    await page.getByRole("button", { name: "定義編集", exact: true }).click();
    await page.locator(".editor-unit-list button").first().click();
    await expect(
      page.getByRole("textbox", { name: "コマンド", exact: true }),
    ).toHaveValue("true # edited 2");
  });
}
