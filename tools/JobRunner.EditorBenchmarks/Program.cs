using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JobRunner.Controllers.Ui;
using JobRunner.Editing;
using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;

namespace JobRunner.EditorBenchmarks;

/// <summary>保存・実行を行わない編集処理の反復測定です。外側のプロセス制限と併用します。</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static void Main(string[] args)
    {
        string label = args[0];
        int size = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        int count = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var options = new RunnerOptions();
        var service = new DefinitionService(new StorageRepository(options), options);
        string source = args[1] == "groups" ? string.Concat(Enumerable.Range(0, size).Select(i => $"unit=g{i};{{ty=g;unit=n;{{ty=n;sd=0,ud;}}}}")) : args[1] == "editor" ? EditorText(size, count) : ScheduleText(size);
        if (args[1] == "calendar-load")
        {
            // 入力上限内でも大きいカレンダー属性により、結果集約前に負荷が発生します。
            source = source.Replace("ty=g;", "ty=g;" + string.Concat(Enumerable.Repeat("cl=1994/1/1;", 50000)), StringComparison.Ordinal);
        }

        var evidence = new SourceEvidence { Mapping = new() { BlobPath = "benchmark.ajs" }, Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source)) };
        var bundle = service.Validate([evidence]);
        var draft = DefinitionEditor.Project(bundle, 8) with { LocalSources = bundle.Sources };
        Console.WriteLine(JsonSerializer.Serialize(new { label, mode = args[1], size, count, sourceChars = source.Length, units = bundle.Units.Flatten().Count(), runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount }, Json));
        if (args[1] == "groups")
        {
            var grouped = DefinitionEditor.Project(bundle, 9);
            var activation = new Activation { Id = "benchmark", Revision = 1 };
            var catalog = GroupCatalogService.Create(bundle, activation, null);
            Measure("catalog-create", () => GroupCatalogService.Create(bundle, activation, catalog));
            Measure("group-export", () => DefinitionEditor.ExportGroup(bundle, new(grouped, "/g0"), service));
            string zip = Convert.ToBase64String(DefinitionEditor.ExportGroup(bundle, new(grouped, "/g0"), service));
            Measure("group-import", () => DefinitionEditor.ImportGroup(bundle, new(grouped, string.Empty, zip, "/", "copied"), service, options));
        }
        else if (args[1] == "editor")
        {
            Measure("import-project", () => DefinitionEditor.Project(service.Validate([evidence]), 8));
            draft.Networks[0].Nodes[0].Attributes["te"] = ["echo edited"];
            Measure("render-validate", () => service.Validate(DefinitionEditor.Render(bundle, draft)));
            Measure("export-bytes", () =>
            {
                var sources = DefinitionEditor.Render(bundle, draft);
                service.Validate(sources);
                return Convert.FromBase64String(sources[0].Base64);
            });
        }
        else
        {
            DateOnly anchor = args[1] is "old-schedule" or "calendar-load" ? new(1994, 1, 1) : new(2026, 1, 1);
            var request = new EditorScheduleRequest(draft, "/g/net", anchor, new(2026, 1, 1), new DateOnly(2026, 1, 1).AddDays(count));
            Measure("schedule-preview", () => DefinitionEditor.PreviewSchedule(bundle, request, options));
        }
    }

    private static void Measure(string name, Func<object> action)
    {
        object Run()
        {
            try
            {
                return action();
            }
            catch (ArgumentException error)
            {
                return error.Message;
            }
        }

        Run(); // JITと初回キャッシュは時間の比較から除きます。
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            object result = Run();
            timer.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            string hash = StorageRepository.Hash(JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(new { name, sample = i, milliseconds = timer.Elapsed.TotalMilliseconds, allocatedBytes = bytes, managedBytes = GC.GetTotalMemory(false), workingSetBytes = Environment.WorkingSet, resultHash = hash, result = result is string message ? message : null }, Json));
            GC.KeepAlive(result);
        }
    }

    private static string EditorText(int jobs, int networks)
    {
        var text = new StringBuilder("/* performance fixture */\nunit=g;{ty=g;");
        for (int n = 0; n < networks; n++)
        {
            text.Append($"unit=n{n};{{ty=n;sd=0,ud;sz=100*100;");
            for (int j = 0; j < jobs; j++)
            {
                text.Append($"el=j{j},j,+{80 + ((j % 100) * 160)} +{48 + ((j / 100) * 96)};");
                if (j % 2 == 1)
                {
                    text.Append($"ar=(f=j{j - 1},t=j{j});");
                }
            }

            for (int j = 0; j < jobs; j++)
            {
                text.Append($"unit=j{j};{{ty=j;te=\"echo preserved {j}\";cm=\"原文\";}}");
            }

            text.Append('}');
        }

        return text.Append('}').ToString();
    }

    private static string ScheduleText(int rules)
    {
        var text = new StringBuilder("unit=g;{ty=g;cl=sa;cl=su;stt=06:00;unit=net;{ty=n;");
        for (int i = 1; i <= rules; i++)
        {
            text.Append($"sd={i},*1;cy={i},(1,d);st={i},{6 + ((i - 1) / 60):D2}:{(i - 1) % 60:D2};");
        }

        return text.Append("}}").ToString();
    }
}
