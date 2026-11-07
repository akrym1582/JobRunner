import type { EditorSource } from "../api/@types";

// Describeの比較キーと揃えます。旧下書きの省略値だけは従来のUnitとして扱います。
export function sourceKey(source: EditorSource): string {
  return JSON.stringify([
    source.kind ?? "Unit",
    source.targetPath,
    source.fileName,
    source.encoding,
  ]);
}
export function findOriginalSource(
  sources: EditorSource[],
  selected?: EditorSource,
): EditorSource | undefined {
  if (!selected) return undefined;
  const key = sourceKey(selected);
  return sources.find((source) => sourceKey(source) === key);
}
export function sourceLabel(source: EditorSource): string {
  return `${source.fileName} → ${source.targetPath} · ${source.kind ?? "Unit"} · ${source.encoding}`;
}
