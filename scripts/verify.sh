#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Azuriteはテスト専用ツールです。アプリ本体にNode.jsは不要です。
tools_dir="${JOBRUNNER_TEST_TOOLS:-${TMPDIR:-/tmp}/jobrunner-test-tools}"
data_dir="$(mktemp -d)"
npm install --prefix "$tools_dir" azurite@3.35.0 --no-audit --no-fund
"$tools_dir/node_modules/.bin/azurite" --silent --location "$data_dir" --blobHost 127.0.0.1 --queueHost 127.0.0.1 --tableHost 127.0.0.1 > "$data_dir/azurite.log" 2>&1 &
azurite_pid=$!
cleanup() {
    kill "$azurite_pid" 2>/dev/null || true
    wait "$azurite_pid" 2>/dev/null || true
    rm -rf "$data_dir"
}
trap cleanup EXIT
for attempt in {1..50}; do
    kill -0 "$azurite_pid" 2>/dev/null || { cat "$data_dir/azurite.log"; exit 1; }
    if curl --silent --output /dev/null http://127.0.0.1:10000/devstoreaccount1 && curl --silent --output /dev/null http://127.0.0.1:10002/devstoreaccount1; then
        break
    fi
    sleep 0.2
done
dotnet restore JobRunner.slnx
dotnet build JobRunner.slnx --no-restore --configuration Release
dotnet test JobRunner.slnx --no-build --configuration Release --logger 'trx;LogFileName=verification.trx'
# 成功終了だけでは検出0件を見逃すため、必須テスト数も検査します。
python3 - <<'PY'
from pathlib import Path
import xml.etree.ElementTree as ET
path = Path('tests/JobRunner.Tests/TestResults/verification.trx')
root = ET.parse(path).getroot()
counters = root.find('.//{*}Counters')
assert counters is not None and int(counters.attrib['executed']) >= 29, '必須テストの検出件数が不足しています'
assert int(counters.attrib['failed']) == 0
PY
