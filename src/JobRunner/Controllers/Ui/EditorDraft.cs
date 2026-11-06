using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>保存済み版を基にした下書きです。</summary>
/// <param name="DefinitionHash">原文を固定する版です。</param>
/// <param name="Networks">全ネットの編集投影です。</param>
/// <param name="EditorVersion">投影の版です。未指定は判定属性を持たない旧下書きです。</param>
/// <param name="Roots">版3のルートネットと原文所属です。</param>
/// <param name="Schedules">版4のネット予定属性です。IDはネットの安定IDです。</param>
/// <param name="Calendars">版4の既存グループのカレンダー属性です。IDは完全名です。</param>
/// <param name="LocalSources">ローカル入力の原文です。省略時は保存済み版から読みます。</param>
/// <param name="Execution">版5の実行属性です。原文のあるグループと全ノードの安定IDを使用します。</param>
/// <param name="NewGroups">版6の追加グループです。</param>
/// <param name="NewFiles">版6の追加ローカルファイルです。</param>
/// <param name="GroupChanges">版7のグループ改名・削除です。</param>
/// <param name="NetworkChanges">版8のルート移動と子ネット種別切替です。</param>
public sealed record EditorDraft(string DefinitionHash, List<EditorNetwork> Networks, int? EditorVersion = null, List<EditorRoot>? Roots = null, List<EditorAttributes>? Schedules = null, List<EditorAttributes>? Calendars = null, List<SourceEvidence>? LocalSources = null, List<EditorAttributes>? Execution = null, List<EditorGroupCreation>? NewGroups = null, List<EditorFileCreation>? NewFiles = null, List<EditorGroupChange>? GroupChanges = null, List<EditorNetworkChange>? NetworkChanges = null);
