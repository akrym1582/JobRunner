import type { EditorDraft } from "../api/@types";

export function groupPath(draft: EditorDraft, path: string): string | null {
  let prefix = "",
    result = "";
  for (const segment of path.split("/").filter(Boolean)) {
    prefix += "/" + segment;
    const change = draft.groupChanges?.find((c) => c.id === prefix);
    if (change?.delete) return null;
    result += "/" + (change?.name ?? segment);
  }
  return result || "/";
}
export function originalGroupPath(draft: EditorDraft, path: string): string {
  const groups = (draft.calendars ?? [])
    .map((c) => ({ id: c.id, name: groupPath(draft, c.id) }))
    .filter((c) => c.name !== null)
    .sort((a, b) => b.name!.length - a.name!.length);
  const group = groups.find(
    (g) => path === g.name || path.startsWith(g.name + "/"),
  );
  return group ? group.id + path.slice(group.name!.length) : path;
}
