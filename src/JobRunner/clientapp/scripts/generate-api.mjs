import { readFileSync, rmSync } from "node:fs";
import { execFileSync } from "node:child_process";
const schema = JSON.parse(readFileSync("openapi.json", "utf8"));
if (!schema.paths["/api/jobrunner/ui/graph"])
  throw new Error("UI APIがスキーマにありません");
rmSync("src/api", { recursive: true, force: true });
execFileSync(
  process.execPath,
  [
    "node_modules/openapi2aspida/bin/index.js",
    "-i",
    "openapi.json",
    "-o",
    "src/api",
  ],
  { stdio: "inherit" },
);
