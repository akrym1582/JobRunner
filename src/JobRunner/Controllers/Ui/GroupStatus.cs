using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>グループ自身の停止と上位からの停止を区別します。</summary>
/// <param name="Id">固定グループIDです。</param>
/// <param name="Path">現在の完全名です。</param>
/// <param name="DefinitionRevision">確認した運用定義版です。</param>
/// <param name="Control">自身の停止状態です。</param>
/// <param name="PausedBy">停止元の完全名。全体は/です。</param>
/// <param name="Retired">旧世代の操作のためだけに残る所属です。</param>
public sealed record GroupStatus(string Id, string Path, long DefinitionRevision, RuntimeControl Control, List<string> PausedBy, bool Retired);
