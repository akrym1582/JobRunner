using System.Diagnostics;

namespace JobRunner.Workers;

/// <summary>開発ホストと同じ寿命でViteを起動し、watch再起動時の孤児プロセスを防ぎます。</summary>
/// <param name="environment">clientappの基準ディレクトリです。</param>
/// <param name="lifetime">Viteの起動失敗をホストにも通知します。</param>
/// <param name="logger">Viteの起動ログです。</param>
public sealed class ClientAppDevServer(IHostEnvironment environment, IHostApplicationLifetime lifetime, ILogger<ClientAppDevServer> logger) : BackgroundService
{
    /// <summary>ブラウザからの最初の要求を待たずにnpm run devを開始します。</summary>
    /// <param name="stoppingToken">ホストの終了通知です。</param>
    /// <returns>Vite終了までのタスクです。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "npm")
        {
            WorkingDirectory = Path.Combine(environment.ContentRootPath, "clientapp"),
            UseShellExecute = false,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("npm");
        }

        start.ArgumentList.Add("run");
        start.ArgumentList.Add("dev");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Viteを起動できません。npm ciを実行してください。");
        try
        {
            logger.LogInformation("開発画面: http://localhost:5173 (APIは.NETへプロキシします)");
            await process.WaitForExitAsync(stoppingToken);
            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Viteが終了しました。終了コード: {ExitCode}", process.ExitCode);
                lifetime.StopApplication();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // npmだけでなく配下のViteも終了し、次のwatch起動へポートを引き渡します。
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
