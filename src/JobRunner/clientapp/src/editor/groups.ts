import type { EditorDraft, EditorGroupChange } from "../api/@types";
import { removeHierarchyNode } from "./hierarchy";
export function changeGroup(
  draft: EditorDraft,
  change: EditorGroupChange,
): EditorDraft {
  let next = {
    ...draft,
    groupChanges: [
      ...(draft.groupChanges ?? []).filter((c) => c.id !== change.id),
      change,
    ],
  };
  if (change.delete) {
    for (const root of draft.roots ?? []) {
      if (
        root.parentPath === change.id ||
        root.parentPath.startsWith(change.id + "/")
      )
        next = {
          ...removeHierarchyNode(next, root.node.id),
          groupChanges: next.groupChanges,
        };
    }
  }
  return next;
}
