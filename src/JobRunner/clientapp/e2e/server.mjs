import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { BlobServiceClient } from "@azure/storage-blob";
const directory = await mkdtemp(join(tmpdir(), "jobrunner-ui-"));
const children = [];
const launch = (command, args, env = {}) => {
  const child = spawn(command, args, {
    stdio: "inherit",
    env: { ...process.env, ...env },
  });
  children.push(child);
  child.on("exit", (code) => {
    if (code && !stopping) {
      console.error(command, "exited", code);
      void stop(1);
    }
  });
  return child;
};
let stopping = false;
async function stop(code = 0) {
  if (stopping) return;
  stopping = true;
  for (const c of children.reverse()) c.kill("SIGTERM");
  await Promise.all(
    children.map(
      (c) =>
        new Promise((r) => (c.exitCode !== null ? r() : c.once("exit", r))),
    ),
  );
  await rm(directory, { recursive: true, force: true });
  process.exit(code);
}
process.on("uncaughtException", (e) => {
  console.error(e.message);
  void stop(1);
});
process.on("unhandledRejection", (e) => {
  console.error(String(e));
  void stop(1);
});
process.on("SIGINT", () => void stop());
process.on("SIGTERM", () => void stop());
launch(process.execPath, [
  "node_modules/azurite/dist/src/azurite.js",
  "--silent",
  "--location",
  directory,
  "--blobHost",
  "127.0.0.1",
  "--blobPort",
  "11300",
  "--queuePort",
  "11301",
  "--tablePort",
  "11302",
]);
const connection =
  "UseDevelopmentStorage=true;DevelopmentStorageProxyUri=http://127.0.0.1";
// Azurite標準の開発資格情報だけを使用します。
const defaultClient = BlobServiceClient.fromConnectionString(connection);
const credential = defaultClient.credential;
const blobs = new BlobServiceClient(
  "http://127.0.0.1:11300/devstoreaccount1",
  credential,
);
const source = blobs.getContainerClient("ui-definitions");
for (let i = 0; ; i++) {
  try {
    await source.create();
    break;
  } catch (e) {
    if (i > 40) throw e;
    await new Promise((r) => setTimeout(r, 250));
  }
}
const definition = `unit=受注処理;{ty=n;sz=4x2;el=受注データ集計,j,+80 +48;el=在庫チェック,j,+240 +48;el=受注伝票作成,n,+400 +48;el=結果通知,j,+560 +48;el=発注伝票作成,rj,+240 +144;sd=1,en;st=1,23:59;cy=1,(1,d);
ar=(f=受注データ集計,t=在庫チェック,seq);ar=(f=在庫チェック,t=受注伝票作成,seq);ar=(f=受注伝票作成,t=結果通知,seq);ar=(f=在庫チェック,t=発注伝票作成,seq);
unit=受注データ集計;{ty=j;te="printf collected";}
unit=在庫チェック;{ty=j;te="printf checked";}
unit=受注伝票作成;{ty=n;el=番号採番,j,+80 +48;el=帳票出力,j,+240 +48;ar=(f=番号採番,t=帳票出力,seq);unit=番号採番;{ty=j;te="printf numbered";}unit=帳票出力;{ty=j;te="printf printed";}}
unit=発注伝票作成;{ty=rj;te="printf recovered";}
unit=結果通知;{ty=j;te="printf notified";}}
unit=座標なし;{ty=n;sd=1,en;st=1,23:59;cy=1,(1,d);unit=ジョブ;{ty=j;te="true";}}
unit=ログ検証;{ty=n;sd=0,ud;el=出力ジョブ,j,+80 +48;el=後続ジョブ,j,+240 +48;ar=(f=出力ジョブ,t=後続ジョブ,seq);
unit=出力ジョブ;{ty=j;te="printf '%0200000d' 0; printf '日本語 <script>alert(1)</script> 出力完了 PID=%s' $$; printf '標準エラーの診断' >&2; exit 1";}
unit=後続ジョブ;{ty=j;te="true";}}
unit=判定検証;{ty=n;sd=0,ud;sz=4*1;el=先行,j,+80 +48;el=既存判定,jdj,+240 +48;el=従属,j,+400 +48;ar=(f=先行,t=既存判定);ar=(f=既存判定,t=従属,con);
unit=先行;{ty=j;te="true";wth=0;}
unit=既存判定,0000,owner,group;{ty=jdj;cm="残す判定";ej=ri;ejl=1;ejh=9;ejm=gt;eju=le;ha=n;}
unit=従属;{ty=j;te="true";}}
unit=運用暦;{ty=g;stt=06:00;cl=sa;cl=su;unit=継承暦;{ty=g;}}`;
await source
  .getBlockBlobClient("jobs")
  .upload(definition, Buffer.byteLength(definition));
const key = credential.accountKey.toString("base64");
const storageConnection = `DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=${key};BlobEndpoint=http://127.0.0.1:11300/devstoreaccount1;QueueEndpoint=http://127.0.0.1:11301/devstoreaccount1;TableEndpoint=http://127.0.0.1:11302/devstoreaccount1;`;
const project = resolve("..");
launch(
  process.env.DOTNET_HOST_PATH || "dotnet",
  [join(project, "bin/Release/net10.0/JobRunner.dll")],
  {
    ASPNETCORE_URLS: "http://127.0.0.1:5181",
    ASPNETCORE_ENVIRONMENT: "Production",
    ASPNETCORE_HOSTINGSTARTUPASSEMBLIES: "",
    JobRunner__ConnectionString: storageConnection,
    JobRunner__ManagementContainer: "ui-management",
    JobRunner__TableName: "UiTest",
    JobRunner__SchedulerId: "ui-test",
    JobRunner__ConfirmedFutureGenerations: "2",
    JobRunner__LocalAgents__0: "local",
    JobRunner__DefinitionSources__0__Kind: "Unit",
    JobRunner__DefinitionSources__0__TargetPath: "/",
    JobRunner__DefinitionSources__0__BlobPath: "ui-definitions/jobs",
    Logging__LogLevel__Default: "Warning",
  },
);
for (let i = 0; ; i++) {
  try {
    const r = await fetch("http://127.0.0.1:5181/api/jobrunner/ui/jobnets");
    if (r.ok) break;
  } catch {
    /* APIが起動するまで待機します。 */
  }
  if (i > 120) throw new Error("API initialization timed out");
  await new Promise((r) => setTimeout(r, 500));
}
launch(
  process.execPath,
  [
    "node_modules/vite/bin/vite.js",
    "--host",
    "127.0.0.1",
    "--port",
    "5174",
    "--strictPort",
  ],
  { JOBRUNNER_API_URL: "http://127.0.0.1:5181" },
);
