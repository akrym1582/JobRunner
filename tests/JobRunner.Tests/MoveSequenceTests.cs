using System.Text;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;
using Xunit;
using Xunit.Abstractions;
using Operation = JobRunner.Models.Operation;

namespace JobRunner.Tests;

/// <summary>固定シードの有限操作列を、ルート同一性と予定枠の受入条件で検証します。</summary>
/// <param name="output">失敗時に再現用の操作履歴を出力します。</param>
public sealed class MoveSequenceTests(ITestOutputHelper output)
{
    /// <summary>削除・同名再利用・子移動・ルート移動・改名・解除・再起動を組み合わせます。</summary>
    /// <param name="seed">再現可能な操作選択です。</param>
    /// <param name="count">件数方式です。</param>
    /// <returns>旧保留枠の非混入とID・登録状態の維持です。</returns>
    [Theory]
    [InlineData(42, false)]
    [InlineData(42, true)]
    [InlineData(73, false)]
    [InlineData(73, true)]
    [InlineData(101, false)]
    [InlineData(101, true)]
    public async Task SeededMovesKeepIdentityAndProtectedSlots(int seed, bool count)
    {
        var random = new Random(seed);
        await using var scenario = new EngineScenario();
        scenario.Scope.Options.ConfirmedHorizonDays = 2;
        scenario.Scope.Options.ConfirmedFutureGenerations = count ? 2 : null;
        const string body = "ty=n;sd=1,en;st=11:00;cy=(1,d);sh=no;unit=left;{ty=n;unit=j;{ty=j;te=\"true\";}}unit=right;{ty=n;}";
        string Network() => "unit=n;{" + body + "}";
        await scenario.StartAsync("unit=a;{ty=g;" + Network() + "}unit=b;{ty=g;unit=n;{ty=n;sd=1,en;st=10:00;cy=(1,d);sh=no;}}unit=spare;{ty=g;}");
        var storage = scenario.Scope.Repository;
        var service = new DefinitionService(storage, scenario.Scope.Options);
        var initial = await scenario.OccurrencesAsync();
        var today = initial.Where(o => o.Id == o.RootId && o.RootPath == "/a/n").OrderBy(o => o.Due).First();
        var tomorrow = initial.Single(o => o.Id == o.RootId && o.RootPath == "/a/n" && o.Due == today.Due.AddDays(1));
        var retired = initial.Where(o => o.Id == o.RootId && o.RootPath == "/b/n").OrderBy(o => o.Due).First();
        var activation = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
        string identity = (await new GroupCatalogService(storage).ReadAsync(activation.GroupCatalogHash!, CancellationToken.None)).Roots["/a/n"];
        foreach (var root in new[] { retired, tomorrow })
        {
            await scenario.EnqueueAsync(new Operation { Id = "hold-" + root.Id, Kind = "Hold", Target = root.Id });
        }

        await scenario.Engine.TickAsync(CancellationToken.None);
        async Task Publish(string name, string text, Dictionary<string, string>? roots = null, Dictionary<string, string>? groups = null)
        {
            output.WriteLine($"seed={seed}, count={count}, operation={name}, roots={string.Join(',', (roots ?? []).Select(p => p.Key + "<-" + p.Value))}, groups={string.Join(',', (groups ?? []).Select(p => p.Key + "<-" + p.Value))}");
            var next = service.Validate([new SourceEvidence { Mapping = new() { BlobPath = "local/sequence" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }]);
            await service.SaveAsync(next, CancellationToken.None);
            await scenario.EnqueueAsync(new Operation { Id = name, Kind = "Activate", Target = next.Hash, RootOrigins = roots, GroupOrigins = groups });
            await scenario.Engine.TickAsync(CancellationToken.None);
            Assert.Equal("Completed", StorageRepository.Read<Operation>((await storage.GetAsync("command:" + StorageRepository.Hash(name), CancellationToken.None))!).Status);
        }

        await Publish("remove-retired", "unit=a;{ty=g;" + Network() + "}unit=b;{ty=g;}unit=spare;{ty=g;}");
        var groupNames = new List<string> { "a", "b", "spare" };
        string current = "b";
        string unitBody = Network();
        string Text() => string.Concat(groupNames.Select(g => "unit=" + g + ";{ty=g;" + (g == current ? unitBody : string.Empty) + "}"));
        await Publish("reuse-destination", Text(), new() { ["/b/n"] = "/a/n" });
        for (int step = 0; step < 6; step++)
        {
            string previous = current;
            bool rename = step == 0 || random.Next(2) == 0;
            var origins = groupNames.ToDictionary(g => "/" + g, g => "/" + g);
            if (rename)
            {
                current = "renamed" + seed + "x" + step;
                groupNames[groupNames.IndexOf(previous)] = current;
                origins.Remove("/" + previous);
                origins.Add("/" + current, "/" + previous);
            }
            else
            {
                current = groupNames.Where(g => g != previous).OrderBy(_ => random.Next()).First();
            }

            await Publish("step-" + step, Text(), new() { ["/" + current + "/n"] = "/" + previous + "/n" }, origins);
            if (step % 2 == 0)
            {
                var active = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
                var bundle = await service.ReadSourcesAsync(active.DefinitionHash, CancellationToken.None);
                string rootPath = "/" + current + "/n";
                string from = step % 4 == 0 ? "left" : "right";
                string to = from == "left" ? "right" : "left";
                var candidate = DefinitionEditor.MoveSelection(bundle, new(DefinitionEditor.Project(bundle, 9), [rootPath + "/" + from + "/j"], rootPath + "/" + to), service, scenario.Scope.Options);
                var compiled = service.Validate(DefinitionEditor.Render(bundle, candidate.Draft));
                unitBody = "unit=n;{" + body.Replace("unit=left;{ty=n;unit=j;{ty=j;te=\"true\";}}unit=right;{ty=n;}", to == "right" ? "unit=left;{ty=n;}unit=right;{ty=n;unit=j;{ty=j;te=\"true\";}}" : "unit=left;{ty=n;unit=j;{ty=j;te=\"true\";}}unit=right;{ty=n;}", StringComparison.Ordinal) + "}";
                await Publish("child-" + step, Encoding.UTF8.GetString(Convert.FromBase64String(compiled.Sources[0].Base64)), candidate.Draft.RootOrigins, candidate.Draft.GroupOrigins);
            }

            if (step % 2 == 1)
            {
                output.WriteLine("restart");
                await scenario.RestartAsync();
                await scenario.Engine.TickAsync(CancellationToken.None);
            }

            var nowActive = StorageRepository.Read<Activation>((await storage.GetAsync("definition-active", CancellationToken.None))!);
            var catalog = await new GroupCatalogService(storage).ReadAsync(nowActive.GroupCatalogHash!, CancellationToken.None);
            Assert.Equal(identity, catalog.Roots["/" + current + "/n"]);
            var values = await scenario.OccurrencesAsync();
            Assert.Equal("Held", values.Single(o => o.Id == retired.Id).State);
            Assert.Equal("Held", values.Single(o => o.Id == tomorrow.Id).State);
            Assert.Single(values, o => o.Id == o.RootId && o.RootPath == "/" + current + "/n" && o.State == "Waiting" && o.Due == today.Due);
            Assert.DoesNotContain(values, o => o.GenerationRevision == nowActive.Revision && o.Due == tomorrow.Due);
            Assert.All(values.Where(o => o.Id == o.RootId && o.State != "Superseded").GroupBy(o => new { o.GenerationRevision, o.RegistrationId, o.Rule, o.BusinessDate, o.OriginalDue }), g => Assert.Single(g));
            Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        }

        if (seed % 2 == 1)
        {
            output.WriteLine("unregister and rename");
            await scenario.EnqueueAsync(new Operation { Id = "unregister", Kind = "Unregister", Target = StorageRepository.Hash("/" + current + "/n") });
            await scenario.Engine.TickAsync(CancellationToken.None);
            string previous = current;
            current = "disabled" + seed;
            groupNames[groupNames.IndexOf(previous)] = current;
            await Publish("disabled-rename", Text(), new() { ["/" + current + "/n"] = "/" + previous + "/n" }, groupNames.ToDictionary(g => "/" + g, g => "/" + (g == current ? previous : g)));
            await scenario.RestartAsync();
            await scenario.Engine.TickAsync(CancellationToken.None);
            var registration = StorageRepository.Read<Registration>((await storage.GetAsync($"registration:{scenario.Engine.Namespace}:{StorageRepository.Hash("/" + current + "/n")}", CancellationToken.None))!);
            Assert.True(registration.Disabled);
            Assert.Empty(await storage.ListAsync("attempt:", CancellationToken.None));
        }
    }
}
