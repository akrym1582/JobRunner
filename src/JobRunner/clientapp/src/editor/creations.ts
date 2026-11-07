import type {
  EditorDocument,
  EditorDraft,
  EditorFileCreation,
  EditorGroupCreation,
} from "../api/@types";
import { groupPath } from "./group-paths";

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
  const workingSources =
    draft.workingSources?.map((s) => ({
      fileName: s.mapping!.blobPath!,
      encoding: s.mapping!.encoding!,
      targetPath: s.mapping!.targetPath!,
      text: "",
      changed: true,
    })) ?? base.sources;
  const sources = [
    ...workingSources,
    ...(draft.newFiles ?? []).map((f) => ({
      fileName: f.fileName,
      encoding: f.encoding,
      targetPath: f.targetPath,
      text: "",
      changed: true,
    })),
  ];
  const containers = [
    ...(draft.workingContainers ?? base.containers ?? []),
    ...(draft.newFiles ?? []).flatMap((f, i) => [
      { sourceIndex: workingSources.length + i, parentPath: f.targetPath },
      {
        sourceIndex: workingSources.length + i,
        parentPath: fullPath(f.targetPath, f.groupName),
      },
    ]),
    ...(draft.newGroups ?? []).map((g) => ({
      sourceIndex: g.sourceIndex,
      parentPath: fullPath(g.parentPath, g.name),
    })),
  ];
  return {
    ...base,
    sources,
    containers: containers.filter(
      (c) => groupPath(draft, c.parentPath) !== null,
    ),
  };
}
