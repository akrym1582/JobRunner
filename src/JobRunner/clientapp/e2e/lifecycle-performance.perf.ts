import { test, expect } from "@playwright/test";
import { appendFile, readFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { performance } from "node:perf_hooks";
import type {
  EditorDocument,
  Operation,
  RuntimeStatus,
  SharedEditorDraft,
} from "../src/api/@types";

const api = "http://127.0.0.1:5181/api/jobrunner";
const endpoint = api + "/ui/definition-editor";
function fixture(jobs: number) {
  let text =
    "/* isolated lifecycle benchmark; processes must never start */\nunit=g;{ty=g;cl=sa;cl=su;unit=net;{ty=n;ha=y;sd=2030/1/1;st=10:00;cy=(1,d);sh=no;sz=100*100;";
  for (let j = 0; j < jobs; j++)
    text += `el=j${j},j,+${80 + (j % 100) * 160} +${48 + Math.floor(j / 100) * 96};`;
  for (let j = 0; j < jobs; j++)
    text += `unit=j${j};{ty=j;te="true";cm="保持 ${j}";}`;
  return text + "}}";
}
async function backendMemory() {
  const processes = execFileSync("ps", ["-eo", "pid,args"], {
    encoding: "utf8",
  });
  const match = processes
    .split("\n")
    .find((line) => /dotnet .*\/JobRunner\.dll\s*$/.test(line));
  if (!match) return {};
  const status = await readFile(
    `/proc/${match.trim().split(/\s+/)[0]}/status`,
    "utf8",
  );
  return Object.fromEntries(
    [...status.matchAll(/^(VmRSS|VmHWM):\s+(\d+) kB$/gm)].map((m) => [
      m[1] + "KiB",
      Number(m[2]),
    ]),
  );
}
for (const jobs of [200, 1000, 9000]) {
  test(`storage lifecycle ${jobs} jobs`, async ({ page, request, context }) => {
    const rows: object[] = [];
    async function measured<T>(
      operation: string,
      work: () => Promise<T>,
    ): Promise<T> {
      const start = performance.now();
      const result = await work();
      const sample = { operation, milliseconds: performance.now() - start };
      rows.push(sample);
      console.log(JSON.stringify({ jobs, ...sample }));
      return result;
    }
    async function waitOperation(id: string) {
      let result: Operation;
      await expect
        .poll(
          async () => {
            result = await (
              await request.get(api + "/operations/" + id)
            ).json();
            return result.status;
          },
          { timeout: 300000, intervals: [100, 250, 1000] },
        )
        .not.toBe("Pending");
      expect(result!.status, result!.result ?? "operation").toBe("Completed");
    }
    const initial: RuntimeStatus = await (
      await request.get(api + "/ui/runtime")
    ).json();
    if (!initial.control.paused) {
      const id = crypto.randomUUID();
      await request.post(api + "/operations", {
        data: {
          id,
          kind: "PauseAll",
          expectedControlRevision: initial.control.revision,
        },
      });
      await waitOperation(id);
    }
    const source = fixture(jobs);
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
    const document: EditorDocument = await measured("http-local", async () => {
      const response = await request.post(endpoint + "/local", {
        data: input,
        timeout: 120000,
      });
      expect(response.ok(), await response.text()).toBe(true);
      return response.json();
    });
    for (const route of ["preview", "export", "schedules", "calendar"]) {
      await measured("http-" + route, async () => {
        const data =
          route === "schedules"
            ? {
                draft: document.draft,
                networkId: "/g/net",
                anchor: "2030-01-01",
                from: "2030-01-01",
                until: "2031-01-01",
              }
            : route === "calendar"
              ? { draft: document.draft, unitId: "/g", month: "2030-01-01" }
              : document.draft;
        const response = await request.post(endpoint + "/" + route, {
          data,
          timeout: 120000,
        });
        expect(
          response.ok(),
          (await response.body()).toString().slice(0, 200),
        ).toBe(true);
        if (route === "preview")
          expect((await response.json()).valid).toBe(true);
      });
    }
    const current = await request.get(endpoint + "/shared");
    const revision =
      current.status() === 204 ? 0 : (await current.json()).revision;
    const runtime: RuntimeStatus = await (
      await request.get(api + "/ui/runtime")
    ).json();
    const shared: SharedEditorDraft = {
      revision,
      baseRevision: runtime.active?.revision ?? 0,
      base: document,
      draft: document.draft,
    };
    await measured("http-shared-save", async () => {
      const response = await request.put(endpoint + "/shared", {
        data: shared,
        timeout: 120000,
      });
      expect(response.ok(), await response.text()).toBe(true);
    });
    await measured("http-shared-load", async () => {
      const response = await request.get(endpoint + "/shared");
      expect(response.ok()).toBe(true);
      expect((await response.json()).revision).toBe(revision + 1);
    });
    await page.goto("/");
    await page.getByRole("button", { name: "定義編集", exact: true }).click();
    page.on("dialog", (dialog) => void dialog.accept());
    const cdp = await context.newCDPSession(page);
    await cdp.send("Performance.enable");
    await measured("shared-load-and-render", async () => {
      await page
        .getByRole("button", { name: "Storageの下書きを読み込む", exact: true })
        .click();
      await expect(page.getByLabel("編集ネット")).toHaveValue("/g/net", {
        timeout: 120000,
      });
      await expect(page.locator(".editor-unit-list button")).toHaveCount(jobs, {
        timeout: 120000,
      });
      await page.evaluate(
        () =>
          new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          ),
      );
    });
    await expect(
      page.getByText("ブラウザーに保存済み", { exact: true }),
    ).toBeVisible({ timeout: 120000 });
    await page.locator(".editor-unit-list button").first().click();
    await measured("edit-and-browser-save", async () => {
      await page
        .getByRole("textbox", { name: "コマンド", exact: true })
        .fill("true # changed");
      await expect(
        page.getByText("ブラウザーに保存済み", { exact: true }),
      ).toBeVisible({ timeout: 120000 });
    });
    const browserMetrics = await cdp.send("Performance.getMetrics");
    const preview = await measured("http-publication-preview", async () => {
      const response = await request.post(endpoint + "/publication-preview", {
        data: document.draft,
        timeout: 120000,
      });
      expect(response.ok(), await response.text()).toBe(true);
      return response.json();
    });
    await measured("publication-applied", async () => {
      const id = crypto.randomUUID();
      const response = await request.post(endpoint + "/publish", {
        data: {
          id,
          target: preview.definitionHash,
          expectedDefinitionRevision: preview.expectedRevision,
          expectedImpactHash: preview.impactHash,
        },
      });
      expect(response.status()).toBe(202);
      await waitOperation(id);
    });
    const completed: RuntimeStatus = await (
      await request.get(api + "/ui/runtime")
    ).json();
    expect(completed.control.paused).toBe(true);
    expect(completed.active?.definitionHash).toBe(preview.definitionHash);
    const result = {
      jobs,
      sourceBytes: Buffer.byteLength(source),
      sharedRequestBytes: Buffer.byteLength(JSON.stringify(shared)),
      production: !!process.env.EDITOR_BENCH_PRODUCTION,
      browser: await page.evaluate(() => navigator.userAgent),
      browserMetrics: Object.fromEntries(
        browserMetrics.metrics
          .filter((m) =>
            [
              "JSHeapUsedSize",
              "JSHeapTotalSize",
              "Nodes",
              "TaskDuration",
            ].includes(m.name),
          )
          .map((m) => [m.name, m.value]),
      ),
      backendMemory: await backendMemory(),
      rows,
    };
    await appendFile(
      process.env.LIFECYCLE_BENCH_OUTPUT ?? "/tmp/lifecycle-performance.jsonl",
      JSON.stringify(result) + "\n",
    );
    console.log(JSON.stringify(result));
  });
}
