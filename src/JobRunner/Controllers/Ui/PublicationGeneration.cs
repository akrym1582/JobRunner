using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>世代単位の反映方針です。</summary>
/// <param name="Id">実行回のIDです。</param>
/// <param name="Path">ルートのパスです。</param>
/// <param name="Due">現在の実行予定です。</param>
/// <param name="Reason">nullなら通常予定として再計算します。</param>
public sealed record PublicationGeneration(string Id, string Path, DateTimeOffset Due, string? Reason);
