using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using JobRunner.Models;

namespace JobRunner.Tests;

/// <summary>実アプリを別OSプロセスで起動し、HTTPとフェイルオーバーを検証します。</summary>
internal sealed class AppProcess : IDisposable
{
    /// <summary>このテストが起動したアプリだけを終了するためのOSハンドルです。</summary>
    private readonly Process process;

    /// <summary>Initializes a new instance of the <see cref="AppProcess"/> class. 環境変数だけでテスト専用Storageへ接続します。</summary>
    /// <param name="options">同じシナリオだけが共有する名前空間と入力です。</param>
    public AppProcess(RunnerOptions options)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "JobRunner.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName ?? throw new InvalidOperationException("ソリューションが見つかりません。");
        }

        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(directory, "src/JobRunner/bin/Release/net10.0/JobRunner.dll"));
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        info.Environment["JobRunner__ConnectionString"] = options.ConnectionString;
        info.Environment["JobRunner__ManagementContainer"] = options.ManagementContainer;
        info.Environment["JobRunner__TableName"] = options.TableName;
        info.Environment["JobRunner__SchedulerId"] = options.SchedulerId;
        info.Environment["JobRunner__ScheduleTimeMode"] = "48";
        info.Environment["JobRunner__ConfirmedHorizonDays"] = "2";
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        for (int i = 0; i < options.DefinitionSources.Count; i++)
        {
            info.Environment[$"JobRunner__DefinitionSources__{i}__Kind"] = "Unit";
            info.Environment[$"JobRunner__DefinitionSources__{i}__TargetPath"] = options.DefinitionSources[i].TargetPath;
            info.Environment[$"JobRunner__DefinitionSources__{i}__BlobPath"] = options.DefinitionSources[i].BlobPath;
        }

        process = Process.Start(info) ?? throw new InvalidOperationException("アプリを起動できません。");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Gets. 起動したアプリの実HTTPエンドポイントへ接続するクライアントです。</summary>
    public HttpClient Client { get; }

    /// <summary>強制停止してメモリを失わせ、永続状態だけからの復旧を試験します。</summary>
    public void Kill()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    /// <summary>起動したテストプロセスとHTTP接続だけを確実に終了します。</summary>
    public void Dispose()
    {
        Kill();
        Client.Dispose();
        process.Dispose();
    }
}
