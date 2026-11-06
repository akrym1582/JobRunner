import type {
  EditorDocument,
  EditorDraft,
  EditorFileCreation,
  EditorGroupCreation,
} from "../api/@types";
const fullPath = (parent: string, name: string) =>
  `${parent.replace(/\/$/, "")}/${name}`;
function addGroupAttributes(draft: EditorDraft, path: string): EditorDraft {
  return {
    ...draft,
    calendars: [...draft.calendars!, { id: path, attributes: {} }],
    execution: [...draft.execution!, { id: path, attributes: {}, type: "g" }],
  };
}
export function createGroup(
  draft: EditorDraft,
  group: EditorGroupCreation,
): EditorDraft {
  return addGroupAttributes(
    { ...draft, newGroups: [...draft.newGroups!, group] },
    fullPath(group.parentPath, group.name),
  );
}
export function createFile(
  draft: EditorDraft,
  file: EditorFileCreation,
): EditorDraft {
  if (!draft.localSources)
    throw new Error("ファイル追加はローカル下書きだけに対応します。");
  return addGroupAttributes(
    { ...draft, newFiles: [...draft.newFiles!, file] },
    fullPath(file.targetPath, file.groupName),
  );
}
export function creationDocument(
  base: EditorDocument,
  draft: EditorDraft,
): EditorDocument {
  const sources = [
    ...base.sources,
    ...(draft.newFiles ?? []).map((f) => ({
      fileName: f.fileName,
      encoding: f.encoding,
      targetPath: f.targetPath,
      text: "",
      changed: true,
    })),
  ];
  const containers = [
    ...(base.containers ?? []),
    ...(draft.newFiles ?? []).flatMap((f, i) => [
      { sourceIndex: base.sources.length + i, parentPath: f.targetPath },
      {
        sourceIndex: base.sources.length + i,
        parentPath: fullPath(f.targetPath, f.groupName),
      },
    ]),
    ...(draft.newGroups ?? []).map((g) => ({
      sourceIndex: g.sourceIndex,
      parentPath: fullPath(g.parentPath, g.name),
    })),
  ];
  return { ...base, sources, containers };
}
