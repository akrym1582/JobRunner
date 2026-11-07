import type { EditorDraft } from "../api/@types";
import { networkNames } from "./hierarchy";
import { rootParent } from "./network-changes";
import { scheduleRules } from "./simple-schedule";

export function scheduleParentId(
  draft: EditorDraft,
  id: string,
): string | undefined {
  const container = draft.networks.find((n) =>
    n.nodes.some((node) => node.id === id && ["n", "rn"].includes(node.type)),
  );
  if (container) return container.path;
  const root = draft.roots?.find((r) => r.node.id === id);
  if (!root) return;
  const parent = rootParent(draft, root);
  if (draft.networks.some((n) => n.path === parent)) return parent;
  return Array.from(networkNames(draft)).find(
    ([, path]) => path === parent,
  )?.[0];
}
export function effectiveScheduleRules(
  draft: EditorDraft,
  id: string,
): { source: string; rules: number[] } {
  const visited = new Set<string>();
  while (!visited.has(id)) {
    visited.add(id);
    const parent = scheduleParentId(draft, id);
    const attributes =
      draft.schedules?.find((s) => s.id === id)?.attributes ?? {};
    if (!parent || attributes.de?.[0] === "n")
      return { source: id, rules: scheduleRules(attributes) };
    id = parent;
  }
  return { source: id, rules: [] };
}
