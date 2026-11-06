#!/usr/bin/env python3
"""Run each pure editor benchmark in an isolated, time-limited process."""
import argparse
import json
import os
import platform
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument("label")
parser.add_argument("output", type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
root = Path(__file__).resolve().parents[1]
dll = root / "tools/JobRunner.EditorBenchmarks/bin/Release/net10.0/JobRunner.EditorBenchmarks.dll"
if not dll.exists():
    raise SystemExit("Build tools/JobRunner.EditorBenchmarks in Release first")
cases = [("editor", 200, 1, 45), ("editor", 200, 10, 45),
         ("editor", 200, 45, 45), ("editor", 1000, 1, 45),
         ("schedule", 1, 31, 10), ("schedule", 144, 366, 10),
         ("old-schedule", 1, 31, 10), ("old-schedule", 8, 366, 10),
         ("old-schedule", 144, 366, 10), ("calendar-load", 1, 31, 30)]
results = []
for mode, size, count, limit in cases:
    name = f"{mode}-{size}-{count}"
    started = time.monotonic()
    child = subprocess.Popen(["dotnet", str(dll), args.label, mode, str(size), str(count)],
                             cwd=root, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    timed_out = False
    try:
        stdout, stderr = child.communicate(timeout=limit)
    except subprocess.TimeoutExpired:
        timed_out = True
        child.kill()
        stdout, stderr = child.communicate()
    (args.output / f"{args.label}-{name}.jsonl").write_text(stdout)
    (args.output / f"{args.label}-{name}.stderr").write_text(stderr)
    result = dict(case=name, seconds=time.monotonic() - started,
                  timedOut=timed_out, returnCode=child.returncode, limitSeconds=limit)
    results.append(result)
    print(json.dumps(result), flush=True)
(args.output / f"{args.label}-environment.json").write_text(json.dumps(dict(
    label=args.label, platform=platform.platform(), processors=os.cpu_count(),
    headAtMeasurement=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
    workingTreeChanged=bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True).strip()),
    cases=results), indent=2))

if any(result["returnCode"] != 0 and not result["timedOut"] for result in results):
    raise SystemExit("A benchmark failed; inspect the recorded stderr")
