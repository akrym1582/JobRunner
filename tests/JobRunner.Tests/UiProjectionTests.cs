using System.Diagnostics;
using System.Text.Json;
using JobRunner.Controllers.Jp1;
using JobRunner.Models;
using Xunit;
using Xunit.Abstractions;

namespace JobRunner.Tests;

/// <summary>表示の一括変換で世代を混ぜず、全件検索を繰り返さないことを検証します。</summary>
/// <param name="output">同条件での比較結果です。</param>
public sealed class UiProjectionTests(ITestOutputHelper output)
{
    /// <summary>同じパスでも世代ごとの条件接続・リカバリー・未実行を維持します。</summary>
    [Fact]
    public void BatchMappingKeepsGenerationAndDependencySemantics()
    {
        var net = new UnitDefinition { Path = "/net", Type = "n", Attributes = new() { ["ar"] = ["(f=first,t=next,con)", "(f=first,t=recovery,seq)"] } };
        var units = new Dictionary<string, UnitDefinition>(StringComparer.Ordinal)
        {
            [net.Path] = net,
            ["/net/first"] = new() { Path = "/net/first", Type = "j" },
            ["/net/next"] = new() { Path = "/net/next", Type = "j" },
            ["/net/recovery"] = new() { Path = "/net/recovery", Type = "rj" },
        };
        var values = new List<Occurrence>();
        foreach (string root in new[] { "false", "failed", "cancelled" })
        {
            values.Add(new() { RootId = root, UnitPath = "/net", State = root == "cancelled" ? "Cancelled" : "Running" });
            values.Add(new() { RootId = root, UnitPath = "/net/first", State = root == "false" ? "Succeeded" : "Failed", ConditionMatched = false });
            values.Add(new() { RootId = root, UnitPath = "/net/next", State = "Waiting" });
            values.Add(new() { RootId = root, UnitPath = "/net/recovery", State = "Waiting" });
        }

        var map = Jp1Status.CreateMapper(units, values, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "RUNNING", "NORMALFALSE", "TERMWAIT", "TERMWAIT", "ABCONT", "ABNORMAL", "TERMWAIT", "EXECWAIT", "BYPASS", "ABNORMAL", "UNEXEC", "UNEXEC" }, values.Select(map));
    }

    /// <summary>1000ジョブの同じ入力で状態を照合し、辞書・接続の再構築による割当増大を防ぎます。</summary>
    [Fact]
    public void BatchMappingReducesAllocationsForOneThousandJobs()
    {
        var now = DateTimeOffset.UtcNow;
        var net = new UnitDefinition { Path = "/net", Type = "n", Attributes = new() { ["ar"] = [] } };
        var units = new Dictionary<string, UnitDefinition>(StringComparer.Ordinal) { [net.Path] = net };
        var values = new List<Occurrence> { new() { RootId = "root", UnitPath = net.Path, State = "Running" } };
        for (int i = 0; i < 1000; i++)
        {
            string path = $"/net/job{i}";
            units.Add(path, new() { Path = path, Type = "j" });
            values.Add(new() { RootId = "root", UnitPath = path, State = "Waiting", Due = now.AddMinutes(1) });
            if (i > 0)
            {
                net.Attributes["ar"].Add($"(f=job{i - 1},t=job{i},seq)");
            }
        }

        // 両経路を先に通し、初回JITと正規表現の初期化を測定から除きます。
        _ = Jp1Status.Map(values[1], units, values, now);
        _ = Jp1Status.CreateMapper(units, values, now)(values[1]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var original = values.Select(v => Jp1Status.Map(v, units, values, now)).ToList();
        watch.Stop();
        long originalBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        double originalMs = watch.Elapsed.TotalMilliseconds;
        before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        var map = Jp1Status.CreateMapper(units, values, now);
        var batch = values.Select(map).ToList();
        watch.Stop();
        long batchBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(original, batch);
        Assert.Equal("TIMEWAIT", batch[1]);
        Assert.All(batch.Skip(2), status => Assert.Equal("TERMWAIT", status));
        Assert.True(batchBytes < originalBytes / 4, $"一括変換で割当が減っていません: {batchBytes} / {originalBytes}");
        string json = JsonSerializer.Serialize(new { Jobs = 1000, OriginalMs = originalMs, BatchMs = watch.Elapsed.TotalMilliseconds, OriginalBytes = originalBytes, BatchBytes = batchBytes }, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../TestResults"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ui-projection.json"), json);
    }
}
