using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>反映確認時の定義差分と世代の扱いです。</summary>
/// <param name="DefinitionHash">検証済みの反映先です。</param>
/// <param name="ExpectedRevision">確認した運用版です。</param>
/// <param name="ImpactHash">確認した登録・世代の状態です。</param>
/// <param name="AddedRoots">自動登録するルートです。</param>
/// <param name="RemovedRoots">補充を停止するルートです。</param>
/// <param name="Generations">置換・保護の内訳です。</param>
/// <param name="Sources">ファイル差分です。</param>
/// <param name="PreviousSources">削除ファイルも含む反映前の一式です。</param>
/// <param name="RootOrigins">固定IDを引き継ぐルート対応です。</param>
/// <param name="GroupOrigins">反映時に引き継ぐグループの改名元です。</param>
/// <param name="GroupChanges">新規停止と所属変更の確認事項です。</param>
public sealed record PublicationPreview(string DefinitionHash, long ExpectedRevision, string ImpactHash, List<string> AddedRoots, List<string> RemovedRoots, List<PublicationGeneration> Generations, List<EditorSource> Sources, List<EditorSource> PreviousSources, Dictionary<string, string>? GroupOrigins = null, List<string>? GroupChanges = null, Dictionary<string, string>? RootOrigins = null);
