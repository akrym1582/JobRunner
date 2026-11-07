using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>実Storage書込みの応答を失わせ、部分状態の再構成なしで反映を復旧します。</summary>
public sealed class PublicationWriteBoundaryTests
{
    /// <summary>Gets. ジャーナルから完了までの11境界と、日数/件数の組合せです。</summary>
    public static TheoryData<int, int?> Boundaries
    {
        get
        {
            var result = new TheoryData<int, int?>();
            for (int stage = 1; stage <= 11; stage++)
            {
                result.Add(stage, null);
                result.Add(stage, 1);
            }

            return result;
        }
    }

    /// <summary>書込み直後の例外と子のコミット直前の例外から、保存済みの同一操作を再開します。</summary>
    /// <param name="stage">中断する実Storage要求の境界です。</param>
    /// <param name="count">件数方式または日数方式です。</param>
    /// <returns>重複と過去の再実行がなく、部分世代が同じIDで揃うことを確認します。</returns>
    [Theory]
    [MemberData(nameof(Boundaries))]
    public Task ResumesAfterActualStorageBoundary(int stage, int? count) => VerifyBoundaryAsync(stage, count, false);

    /// <summary>ルート移動の識別情報も実書込み中断から復旧します。</summary>
    /// <param name="stage">中断境界です。</param>
    /// <param name="count">件数方式です。</param>
    /// <returns>重複のない移動復旧です。</returns>
    [Theory]
    [InlineData(1, null)]
    [InlineData(3, null)]
    [InlineData(6, null)]
    [InlineData(9, null)]
    [InlineData(10, null)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(6, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 1)]
    public Task RootMoveResumesAfterActualStorageBoundary(int stage, int? count) => VerifyBoundaryAsync(stage, count, true);

    /// <summary>子移動を含む候補を実書込み境界で中断し、同じ世代IDで復旧します。</summary>
    /// <param name="stage">中断境界です。</param>
    /// <param name="count">件数方式です。</param>
    /// <returns>部分バッチと世代生成の復旧です。</returns>
    [Theory]
    [InlineData(1, null)]
    [InlineData(3, null)]
    [InlineData(6, null)]
    [InlineData(8, null)]
    [InlineData(9, null)]
    [InlineData(10, null)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(6, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 1)]
    public Task ChildMoveResumesAfterActualStorageBoundary(int stage, int? count) => VerifyBoundaryAsync(stage, count, false, true);

    private static async Task VerifyBoundaryAsync(int stage, int? count, bool move, bool childMove = false)
    {
        var failure = new WriteInterruption(stage);
        await using var scenario = new EngineScenario(failure);
        scenario.Scope.Options.ConfirmedFutureGenerations = count;
        string children = string.Concat(Enumerable.Range(0, childMove ? 103 : 105).Select(i => $"unit=j{i};{{ty=j;te=\"true\";}}"));
        string original = "unit=net;{ty=n;sd=2026/11/5;st=10:00;cy=(1,d);sh=no;" + children + "}";
        if (childMove)
        {
            original = original.Replace(children, "unit=left;{ty=n;" + children + "}unit=right;{ty=n;}", StringComparison.Ordinal);
        }

        await scenario.StartAsync(move ? "unit=a;{ty=g;" + original + "}unit=b;{ty=g;}" : original);
        var storage = scenario.Scope.Repository;
        string stateNamespace = scenario.Engine.Namespace!;
        var before = await scenario.OccurrencesAsync();
        var definitions = new DefinitionService(storage, scenario.Scope.Options);
        var next = definitions.Validate([new SourceEvidence { Mapping = new DefinitionSource { BlobPath = "local/new.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(move ? "unit=a;{ty=g;}unit=b;{ty=g;" + original.Replace("10:00", "11:00", StringComparison.Ordinal) + "}" : original.Replace("10:00", "11:00", StringComparison.Ordinal))) }]);
        if (childMove)
        {
            var candidate = DefinitionEditor.MoveSelection(next, new(DefinitionEditor.Project(next, 9), Enumerable.Range(0, 103).Select(i => "/net/left/j" + i).ToList(), "/net/right"), definitions, scenario.Scope.Options);
            next = definitions.Validate(DefinitionEditor.Render(next, candidate.Draft));
            Assert.Contains(next.Units.Flatten(), u => u.Path == "/net/right/j102");
        }

        await definitions.SaveAsync(next, CancellationToken.None);
        var operation = new Operation { Id = "write-boundary", Kind = "Activate", Target = next.Hash, RootOrigins = move ? new() { ["/b/net"] = "/a/net" } : null };
        await scenario.EnqueueAsync(operation);
        string key = "command:" + StorageRepository.Hash(operation.Id);

        failure.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => scenario.Engine.TickAsync(CancellationToken.None));
        Assert.True(failure.Interrupted);
        var stopped = await scenario.OccurrencesAsync();
        var newRoots = stopped.Where(o => o.Id == o.RootId && o.GenerationRevision == 1).ToList();
        var executionIds = newRoots.ToDictionary(o => o.Id, o => o.Jp1ExecutionId, StringComparer.Ordinal);
        var interruptedOperation = StorageRepository.Read<Operation>((await storage.GetAsync(key, CancellationToken.None))!);
        Assert.Equal(stage == 10 ? "Completed" : "Pending", interruptedOperation.Status);
        if (stage == 1)
        {
            Assert.Null(interruptedOperation.MutationPath);
            int journals = 0;
            await foreach (var unused in storage.Container.GetBlobsAsync(prefix: $"mutations/{storage.Partition}/{StorageRepository.Hash(operation.Id)}/"))
            {
                journals++;
            }

            Assert.Equal(1, journals);
        }
        else
        {
            Assert.NotNull(interruptedOperation.MutationPath);
        }

        if (stage <= 2)
        {
            Assert.DoesNotContain(stopped, o => o.State == "Superseded");
        }

        if (stage == 3)
        {
            Assert.Equal(99, stopped.Count(o => o.State == "Superseded"));
            Assert.True(before.Count > 99);
        }

        if (stage <= 4)
        {
            Assert.Empty(newRoots);
        }

        if (stage >= 4)
        {
            Assert.True(interruptedOperation.MutationApplied);
        }

        if (stage == 9)
        {
            var mirror = await storage.Container.GetBlobClient($"management/{storage.Partition}/active.json").DownloadContentAsync();
            Assert.Equal(next.Hash, mirror.Value.Content.ToObjectFromJson<Activation>()!.DefinitionHash);
        }

        if (stage is 5 or 6 or 7 or 11)
        {
            var root = Assert.Single(newRoots);
            Assert.False(root.Materialized);
            Assert.Equal(stage == 6 ? 99 : stage == 7 ? 105 : 0, stopped.Count(o => o.RootId == root.Id && o.Id != root.Id));
        }

        if (stage == 8)
        {
            Assert.True(Assert.Single(newRoots).Materialized);
        }

        if (stage >= 5)
        {
            // 実際に残った部分世代を、先読みより長い停止後に補います。
            scenario.Clock.Now = scenario.Clock.Now.AddDays(10);
        }

        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(stateNamespace, scenario.Engine.Namespace);
        Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync(key, CancellationToken.None))!).Status);
        Assert.Equal(next.Hash, StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!).DefinitionHash);
        var values = await scenario.OccurrencesAsync();
        Assert.All(before, old => Assert.Equal("Superseded", values.Single(o => o.Id == old.Id).State));
        Assert.All(executionIds, pair => Assert.Equal(pair.Value, values.Single(o => o.Id == pair.Key).Jp1ExecutionId));
        Assert.All(values.Where(o => o.Id == o.RootId), root =>
        {
            Assert.True(root.Materialized);
            Assert.Equal(106, values.Count(o => o.RootId == root.Id));
        });
        var currentRoots = values.Where(o => o.Id == o.RootId && o.GenerationRevision == 1).ToList();
        Assert.NotEmpty(currentRoots);
        Assert.All(currentRoots.GroupBy(o => new { o.RegistrationId, o.Rule, o.BusinessDate, o.OriginalDue }), group => Assert.Single(group));
        Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        Assert.DoesNotContain(values, o => o.Due < scenario.Clock.Now && o.Started);
        var ids = values.Select(o => o.Id).Order(StringComparer.Ordinal).ToList();
        await scenario.RestartAsync();
        await scenario.Engine.TickAsync(CancellationToken.None);
        Assert.Equal(ids, (await scenario.OccurrencesAsync()).Select(o => o.Id).Order(StringComparer.Ordinal).ToList());
    }

    private sealed class WriteInterruption(int stage) : HttpPipelinePolicy
    {
        public bool Armed { get; set; }

        public bool Interrupted { get; private set; }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            bool stop = Matches(message);
            InterruptBefore(stop);
            ProcessNext(message, pipeline);
            InterruptAfter(message, stop);
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            bool stop = Matches(message);
            InterruptBefore(stop);
            await ProcessNextAsync(message, pipeline);
            InterruptAfter(message, stop);
        }

        private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.True;

        private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;

        private bool Matches(HttpMessage message)
        {
            if (!Armed)
            {
                return false;
            }

            var uri = message.Request.Uri.ToUri();
            if (message.Request.Method == RequestMethod.Put)
            {
                return (stage == 1 && uri.AbsolutePath.Contains("/mutations/", StringComparison.Ordinal)) || (stage == 9 && uri.AbsolutePath.EndsWith("/active.json", StringComparison.Ordinal));
            }

            if (message.Request.Method != RequestMethod.Post || !uri.AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal))
            {
                return false;
            }

            using var body = new MemoryStream();
            message.Request.Content!.WriteTo(body, message.CancellationToken);
            var rows = new List<(string Key, JsonElement Value)>();
            foreach (string line in Encoding.UTF8.GetString(body.ToArray()).Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('{')))
            {
                using var entity = JsonDocument.Parse(line);
                if (entity.RootElement.TryGetProperty("RowKey", out var key) && entity.RootElement.TryGetProperty("Json", out var json))
                {
                    using var value = JsonDocument.Parse(json.GetString()!);
                    rows.Add((key.GetString()!, value.RootElement.Clone()));
                }
            }

            var commands = rows.Where(r => r.Key.StartsWith("command:", StringComparison.Ordinal)).Select(r => r.Value).ToList();
            var occurrences = rows.Where(r => r.Key.StartsWith("occurrence:", StringComparison.Ordinal)).Select(r => r.Value).ToList();
            var created = occurrences.Where(v => v.TryGetProperty("GenerationRevision", out var revision) && revision.GetInt64() == 1).ToList();
            bool root = created.Any(v => Text(v, "Id") == Text(v, "RootId"));
            return stage switch
            {
                2 => commands.Any(v => Text(v, "MutationPath") is not null && !Flag(v, "MutationApplied") && Text(v, "Status") == "Pending"),
                3 => occurrences.Count == 99 && occurrences.All(v => Text(v, "State") == "Superseded"),
                4 => commands.Any(v => Flag(v, "MutationApplied") && Text(v, "Status") == "Pending"),
                5 => root && created.Any(v => !Flag(v, "Materialized")),
                6 or 11 => !root && created.Count == 99,
                7 => !root && created.Count == 6,
                8 => root && created.Any(v => Flag(v, "Materialized")),
                10 => commands.Any(v => Text(v, "Status") == "Completed"),
                _ => false,
            };
        }

        private void InterruptBefore(bool stop)
        {
            if (stop && stage == 11)
            {
                Interrupt();
            }
        }

        private void InterruptAfter(HttpMessage message, bool stop)
        {
            if (stop && message.Response.Status < 400)
            {
                Interrupt();
            }
        }

        private void Interrupt()
        {
            Armed = false;
            Interrupted = true;
            throw new IOException("実Storage書込み境界で応答を失いました: " + stage);
        }
    }
}
