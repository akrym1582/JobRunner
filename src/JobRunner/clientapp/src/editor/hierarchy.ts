import type { EditorDraft, EditorNode } from "../api/@types";
import { groupPath } from "./group-paths";
import { isNetwork, removeNode, replaceNode } from "./model";

export function networkNode(draft: EditorDraft, id: string) {
  return (
    draft.roots?.find((r) => r.node.id === id)?.node ??
    draft.networks.flatMap((n) => n.nodes).find((n) => n.id === id)
  );
}

export function networkNames(draft: EditorDraft) {
  const names = new Map<string, string>();
  const networks = new Map(draft.networks.map((n) => [n.path, n]));
  function visit(id: string, path: string) {
    if (names.has(id)) return;
    names.set(id, path);
    for (const child of networks.get(id)?.nodes ?? [])
      if (isNetwork(child.type)) visit(child.id, path + "/" + child.name);
  }
  for (const root of draft.roots ?? [])
    visit(
      root.node.id,
      (groupPath(draft, root.parentPath) ?? root.parentPath).replace(
        /\/$/,
        "",
      ) +
        "/" +
        root.node.name,
    );
  return names;
}

export function updateNetworkNode(
  draft: EditorDraft,
  node: EditorNode,
): EditorDraft {
  return {
    ...draft,
    roots: draft.roots?.map((r) =>
      r.node.id === node.id ? { ...r, node } : r,
    ),
    networks: draft.networks.map((n) => replaceNode(n, node)),
  };
}

export function subtreeIds(draft: EditorDraft, id: string) {
  const ids = new Set<string>();
  function visit(current: string) {
    if (ids.has(current)) return;
    ids.add(current);
    for (const child of draft.networks.find((n) => n.path === current)?.nodes ??
      [])
      visit(child.id);
  }
  visit(id);
  return ids;
}

export function removeHierarchyNode(
  draft: EditorDraft,
  id: string,
): EditorDraft {
  const removed = subtreeIds(draft, id);
  return {
    ...draft,
    roots: draft.roots?.filter((r) => !removed.has(r.node.id)),
    schedules: draft.schedules?.filter((s) => !removed.has(s.id)),
    execution: draft.execution?.filter((s) => !removed.has(s.id)),
    networks: draft.networks
      .filter((n) => !removed.has(n.path))
      .map((n) => removeNode(n, id)),
  };
}

export function addRoot(
  draft: EditorDraft,
  sourceIndex: number,
  parentPath: string,
  name: string,
): EditorDraft {
  const id = `new:${crypto.randomUUID()}`;
  return {
    ...draft,
    roots: [
      ...(draft.roots ?? []),
      {
        node: { id, name, type: "n", attributes: {}, x: null, y: null },
        sourceIndex,
        parentPath,
      },
    ],
    networks: [...draft.networks, { path: id, nodes: [], edges: [] }],
    schedules:
      (draft.editorVersion ?? 1) >= 4
        ? [...(draft.schedules ?? []), { id, attributes: { sd: ["0,ud"] } }]
        : draft.schedules,
    execution:
      (draft.editorVersion ?? 1) >= 5
        ? [...(draft.execution ?? []), { id, attributes: { ha: ["y"] } }]
        : draft.execution,
  };
}
