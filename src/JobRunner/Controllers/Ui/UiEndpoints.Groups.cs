using JobRunner.Models;
using JobRunner.Services;
using JobRunner.Storage;

namespace JobRunner.Controllers.Ui;

/// <summary>グループごとの実効停止を表示します。</summary>
public static partial class UiEndpoints
{
    private static void MapGroups(RouteGroupBuilder group)
    {
        group.MapGet("/groups", async (StorageRepository storage, CancellationToken token) =>
        {
            if (await storage.GetAsync("definition-active", token) is null)
            {
                return new List<GroupStatus>();
            }

            var active = await ActiveAsync(storage, token);
            var visible = await new GroupCatalogService(storage).VisibleAsync(active, token);
            var controls = (await storage.ListAsync("group-control:", token)).ToDictionary(r => r.RowKey["group-control:".Length..], StorageRepository.Read<RuntimeControl>, StringComparer.Ordinal);
            var runtime = StorageRepository.Read<RuntimeControl>((await storage.GetAsync("runtime-control", token)) ?? throw new InvalidDataException("停止状態がありません。"));
            var result = new List<GroupStatus>();
            foreach (var (id, value) in visible.OrderBy(p => p.Value.Path, StringComparer.Ordinal))
            {
                var pausedBy = runtime.Paused ? new List<string> { "/" } : [];
                string path = value.Path;
                while (path.Length > 0)
                {
                    if (value.Catalog.Groups.TryGetValue(path, out string? parentId) && controls[parentId].Paused)
                    {
                        pausedBy.Add(path);
                    }

                    if (path == "/")
                    {
                        break;
                    }

                    path = JobRunner.Scheduling.ScheduleCalculator.Parent(path);
                }

                result.Add(new(id, value.Path, active.Revision, controls[id], pausedBy, value.Retired));
            }

            return result;
        });
    }
}
