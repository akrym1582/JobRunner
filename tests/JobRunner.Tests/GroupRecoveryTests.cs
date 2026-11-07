using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>グループ再開の分割書込みを実Storageで中断します。</summary>
public sealed class GroupRecoveryTests
{
    /// <summary>再開境界は復旧時刻へずれず、長期停止しても過去の予定を起動しません。</summary>
    /// <param name="stage">実際の書込後に応答を失う位置です。</param>
    /// <param name="count">件数方式を選ぶ場合trueです。</param>
    /// <returns>再開済み世代と停止状態の確認です。</returns>
    [Theory]
    [InlineData("journal", false)]
    [InlineData("batch", false)]
    [InlineData("control", false)]
    [InlineData("completed", false)]
    [InlineData("journal", true)]
    [InlineData("batch", true)]
    [InlineData("control", true)]
    [InlineData("completed", true)]
    public async Task ResumeSurvivesWriteLossAndLongStop(string stage, bool count)
    {
        var failure = new Interruption(stage);
        await using var scenario = new EngineScenario(failure);
        scenario.Scope.Options.ConfirmedFutureGenerations = count ? 1 : null;
        await scenario.StartAsync("unit=g;{ty=g;" + string.Concat(Enumerable.Range(0, 105).Select(i => $"unit=n{i};{{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;}}")) + "}");
        var storage = scenario.Scope.Repository;
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        var catalog = await new GroupCatalogService(storage).ReadAsync(active.GroupCatalogHash!, CancellationToken.None);
        string id = catalog.Groups["/g"];
        await scenario.EnqueueAsync(new Operation { Id = "pause", Kind = "PauseGroup", Target = id, ExpectedControlRevision = 0, ExpectedDefinitionRevision = active.Revision });
        await scenario.Engine.TickAsync(CancellationToken.None);
        var original = (await scenario.OccurrencesAsync()).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(20);
        var cutoff = scenario.Clock.Now;
        await scenario.EnqueueAsync(new Operation { Id = "resume", Kind = "ResumeGroup", Target = id, ExpectedControlRevision = 1, ExpectedDefinitionRevision = active.Revision });
        failure.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => scenario.Engine.TickAsync(CancellationToken.None));
        Assert.True(failure.Interrupted);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(20);
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        var control = StorageRepository.Read<RuntimeControl>((await storage.GetAsync("group-control:" + id, CancellationToken.None))!);
        Assert.False(control.Paused);
        Assert.Equal(2, control.Revision);

        // ジャーナル参照の保存前は操作未適用。参照確定後だけ当初の再開境界に固定する。
        Assert.Equal(stage == "journal" ? scenario.Clock.Now : cutoff, control.SkipBefore);
        Assert.All((await scenario.OccurrencesAsync()).Where(o => original.Contains(o.Id)), o => Assert.Equal("Skipped", o.State));
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        var operation = StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash("resume"), CancellationToken.None))!);
        Assert.Equal("Completed", operation.Status);
    }

    /// <summary>新規グループ停止の最初の99行を保存した直後でも、同じIDで反映を復旧します。</summary>
    /// <returns>新規グループの全件停止と採用の整合を検証します。</returns>
    [Fact]
    public async Task PublicationResumesAfterPartialNewGroupControls()
    {
        var failure = new Interruption("control");
        await using var scenario = new EngineScenario(failure);
        await scenario.StartAsync("unit=old;{ty=n;sd=0,ud;}");
        var storage = scenario.Scope.Repository;
        var service = new DefinitionService(storage, scenario.Scope.Options);
        string text = string.Concat(Enumerable.Range(0, 105).Select(i => $"unit=g{i};{{ty=g;unit=n;{{ty=n;sd=0,ud;}}}}"));
        var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/groups.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }]);
        await service.SaveAsync(next, CancellationToken.None);
        string? originalNamespace = scenario.Engine.Namespace;
        await scenario.EnqueueAsync(new Operation { Id = "publish-groups", Kind = "Activate", Target = next.Hash });
        failure.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => scenario.Engine.TickAsync(CancellationToken.None));
        Assert.True(failure.Interrupted);
        var partial = await storage.ListAsync("group-control:", CancellationToken.None);
        Assert.Equal(99, partial.Count);
        await scenario.RestartAsync();
        var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        Assert.Equal(originalNamespace, active.Id);
        Assert.Equal(next.Hash, active.DefinitionHash);
        var catalog = await new GroupCatalogService(storage).ReadAsync(active.GroupCatalogHash!, CancellationToken.None);
        var complete = await storage.ListAsync("group-control:", CancellationToken.None);
        Assert.Equal(105, complete.Count);
        Assert.Equal(105, catalog.Groups.Values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(complete, row => Assert.True(StorageRepository.Read<RuntimeControl>(row).Paused));
        Assert.All(partial, row => Assert.Contains(complete, current => current.RowKey == row.RowKey && current.GetString("Json") == row.GetString("Json")));
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
    }

    private sealed class Interruption(string stage) : HttpPipelinePolicy
    {
        public bool Armed { get; set; }

        public bool Interrupted { get; private set; }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            bool selected = Matches(message);
            ProcessNext(message, pipeline);
            Interrupt(message, selected);
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            bool selected = Matches(message);
            await ProcessNextAsync(message, pipeline);
            Interrupt(message, selected);
        }

        private bool Matches(HttpMessage message)
        {
            if (!Armed)
            {
                return false;
            }

            if (message.Request.Method == RequestMethod.Put)
            {
                return stage == "journal" && message.Request.Uri.ToUri().AbsolutePath.Contains("/mutations/", StringComparison.Ordinal);
            }

            if (message.Request.Method != RequestMethod.Post || !message.Request.Uri.ToUri().AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal))
            {
                return false;
            }

            using var stream = new MemoryStream();
            message.Request.Content!.WriteTo(stream, message.CancellationToken);
            foreach (string line in Encoding.UTF8.GetString(stream.ToArray()).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')))
            {
                using var entity = JsonDocument.Parse(line);
                if (!entity.RootElement.TryGetProperty("RowKey", out var key) || !entity.RootElement.TryGetProperty("Json", out var json))
                {
                    continue;
                }

                using var value = JsonDocument.Parse(json.GetString()!);
                if ((stage == "control" && key.GetString()!.StartsWith("group-control:", StringComparison.Ordinal))
                    || (stage == "batch" && value.RootElement.TryGetProperty("State", out var state) && state.GetString() == "Skipped")
                    || (stage == "completed" && value.RootElement.TryGetProperty("Status", out var status) && status.GetString() == "Completed"))
                {
                    return true;
                }
            }

            return false;
        }

        private void Interrupt(HttpMessage message, bool selected)
        {
            if (selected && message.Response.Status < 400)
            {
                Armed = false;
                Interrupted = true;
                throw new IOException("グループ再開の保存応答を失いました。");
            }
        }
    }
}
