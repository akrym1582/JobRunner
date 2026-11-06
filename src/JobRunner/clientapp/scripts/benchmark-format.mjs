import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { performance } from "node:perf_hooks";
import ts from "typescript";

const baseline = process.argv[2];
if (!baseline)
  throw new Error("Usage: npm run benchmark:format -- <baseline-revision>");
async function load(source) {
  const { outputText } = ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.ESNext,
      target: ts.ScriptTarget.ES2022,
    },
  });
  return import(
    "data:text/javascript;base64," + Buffer.from(outputText).toString("base64")
  );
}
const before = await load(
  execFileSync(
    "git",
    ["show", `${baseline}:src/JobRunner/clientapp/src/status.ts`],
    { encoding: "utf8" },
  ),
);
const after = await load(
  await readFile(new URL("../src/status.ts", import.meta.url), "utf8"),
);
const dates = Array.from({ length: 5000 }, (_, i) =>
  new Date(Date.UTC(2030, 5, 1) + i * 60000).toISOString(),
);
for (const date of dates.slice(0, 100)) {
  if (
    before.formatDate(date, "Asia/Tokyo") !==
    after.formatDate(date, "Asia/Tokyo")
  )
    throw new Error("Formatted dates differ");
}
function measure(format) {
  const start = performance.now();
  for (const date of dates) format(date, "Asia/Tokyo");
  return performance.now() - start;
}
const oldSamples = [],
  newSamples = [];
for (let i = 0; i < 5; i++) {
  oldSamples.push(measure(before.formatDate));
  newSamples.push(measure(after.formatDate));
}
const median = (samples) =>
  [...samples].sort((a, b) => a - b)[Math.floor(samples.length / 2)];
console.log(
  JSON.stringify(
    {
      baseline,
      node: process.version,
      dates: dates.length,
      samples: 5,
      beforeMedianMs: median(oldSamples),
      afterMedianMs: median(newSamples),
      oldSamples,
      newSamples,
    },
    null,
    2,
  ),
);
