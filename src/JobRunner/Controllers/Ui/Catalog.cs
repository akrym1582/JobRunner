using JobRunner.Models;

namespace JobRunner.Controllers.Ui;

/// <summary>採用中のネット一覧です。</summary>
/// <param name="Namespace">Namespaceです。</param>
/// <param name="DefinitionHash">DefinitionHashです。</param>
/// <param name="TimeZone">TimeZoneです。</param>
/// <param name="Phase">Phaseです。</param>
/// <param name="Networks">Networksです。</param>
public sealed record Catalog(string Namespace, string DefinitionHash, string TimeZone, string Phase, List<Network> Networks);
