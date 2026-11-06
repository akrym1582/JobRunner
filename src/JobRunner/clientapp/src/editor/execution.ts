import type { EditorDraft } from "../api/@types";

import { groupPath } from "./group-paths";

export function executionTargets(draft: EditorDraft) {
  const names = new Map<string, string>();
  const networks = new Map(draft.networks.map((n) => [n.path, n]));
  function visit(id: string, name: string) {
    if (names.has(id)) return;
    names.set(id, name);
    for (const node of networks.get(id)?.nodes ?? [])
      visit(node.id, `${name}/${node.name}`);
  }
  for (const root of draft.roots ?? [])
    visit(
      root.node.id,
      `${root.parentPath.replace(/\/$/, "")}/${root.node.name}`,
    );
  const targets = [
    ...(draft.calendars ?? []).map((c) => ({
      id: c.id,
      name: c.id,
      type: "g",
    })),
    ...(draft.roots ?? []).map((r) => ({
      id: r.node.id,
      name: names.get(r.node.id) ?? r.node.name,
      type: r.node.type,
    })),
    ...draft.networks.flatMap((n) =>
      n.nodes.map((node) => ({
        id: node.id,
        name: names.get(node.id) ?? node.name,
        type: node.type,
      })),
    ),
  ];
  const known = new Set(targets.map((t) => t.id));
  return [
    ...targets,
    ...(draft.execution ?? [])
      .filter((e) => !known.has(e.id))
      .map((e) => ({ id: e.id, name: e.id, type: e.type ?? "unit" })),
  ]
    .filter((t) => groupPath(draft, t.name) !== null)
    .map((t) => ({ ...t, name: groupPath(draft, t.name)! }));
}
