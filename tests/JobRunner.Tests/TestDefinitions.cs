using System.Text.Json;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;

namespace JobRunner.Tests;

/// <summary>実行試験の前提を明示的に取り込みます。通常起動の自動取込には依存しません。</summary>
internal static class TestDefinitions
{
    /// <summary>初期化ジャーナルを配置し、初回復旧の前提を作ります。</summary>
    /// <param name="storage">試験専用Storageです。</param>
    /// <param name="options">実行器と一致する設定です。</param>
    /// <param name="clock">試験の固定時刻です。</param>
    /// <returns>明示的な取込と準備の保存です。</returns>
    public static async Task SeedAsync(StorageRepository storage, RunnerOptions options, TimeProvider clock)
    {
        await storage.InitializeAsync(CancellationToken.None);
        var service = new DefinitionService(storage, options);
        var bundle = await service.LoadSourcesAsync(CancellationToken.None);
        await service.SaveAsync(bundle, CancellationToken.None);
        await storage.Table.AddEntityAsync(storage.Entity("runtime-control", new RuntimeControl { Paused = false }));
        var activation = new Activation { Id = Guid.NewGuid().ToString("N"), DefinitionHash = bundle.Hash, SettingsHash = service.SettingsHash(), Baseline = clock.GetUtcNow() };
        var catalog = GroupCatalogService.Create(bundle, activation, null);
        activation.GroupCatalogHash = await new GroupCatalogService(storage).SaveAsync(catalog, CancellationToken.None);
        foreach (string id in catalog.Groups.Values)
        {
            await storage.Table.AddEntityAsync(storage.Entity("group-control:" + id, new RuntimeControl { Paused = false }));
        }

        await storage.Table.AddEntityAsync(storage.Entity("definition-active", activation));
    }

    /// <summary>HTTP試験プロセスが使う時間制と先読み設定で初回状態を作ります。</summary>
    /// <param name="storage">試験専用Storageです。</param>
    /// <param name="options">入力マッピングなどの設定です。</param>
    /// <returns>取込の完了です。</returns>
    public static Task SeedHttpAsync(StorageRepository storage, RunnerOptions options)
    {
        var effective = JsonSerializer.Deserialize<RunnerOptions>(JsonSerializer.Serialize(options))!;
        effective.ScheduleTimeMode = 48;
        effective.ConfirmedHorizonDays = 2;
        return SeedAsync(storage, effective, TimeProvider.System);
    }
}
