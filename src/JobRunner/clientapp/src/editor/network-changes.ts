import type {
  EditorDraft,
  EditorNetworkChange,
  EditorNode,
  EditorRoot,
} from "../api/@types";

export function changeNetwork(
  draft: EditorDraft,
  change: EditorNetworkChange,
): EditorDraft {
  return {
    ...draft,
    networkChanges: [
      ...(draft.networkChanges ?? []).filter((c) => c.id !== change.id),
      change,
    ],
  };
}
export function networkType(draft: EditorDraft, node: EditorNode) {
  return draft.networkChanges?.find((c) => c.id === node.id)?.type ?? node.type;
}
export function rootParent(draft: EditorDraft, root: EditorRoot) {
  return (
    draft.networkChanges?.find((c) => c.id === root.node.id)?.parentPath ??
    root.parentPath
  );
}
export function movedGroupConflict(draft: EditorDraft, group: string) {
  const within = (path: string) =>
    path === group || path.startsWith(group + "/");
  return (
    draft.roots?.some((root) => {
      const target = draft.networkChanges?.find(
        (c) => c.id === root.node.id,
      )?.parentPath;
      return (
        target != null &&
        target !== root.parentPath &&
        (within(root.parentPath) || within(target))
      );
    }) ?? false
  );
}
