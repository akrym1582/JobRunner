import type { EditorDraft, EditorNetwork, EditorNode } from "../api/@types";

export const isJob = (type: string) => ["j", "pj", "rj", "rp"].includes(type);
export const isJudgment = (type: string) => ["jdj", "rjdj"].includes(type);
export const isNetwork = (type: string) => ["n", "rn"].includes(type);
export const canEdit = (type: string, version: number) =>
  isJob(type) ||
  (version >= 2 && isJudgment(type)) ||
  (version >= 3 && version <= 4 && isNetwork(type));

export function upgradeDraft(
  draft: EditorDraft,
  baseline: EditorDraft,
): EditorDraft {
  if (
    ![1, 2, 3].includes(draft.editorVersion ?? 1) ||
    baseline.editorVersion !== 4 ||
    !baseline.roots ||
    !baseline.schedules ||
    !baseline.calendars ||
    (draft.editorVersion === 3 && !draft.roots) ||
    draft.definitionHash !== baseline.definitionHash
  )
    throw new Error("下書きの形式または元版が一致しません。");
  const originals = new Map(
    baseline.networks.flatMap((n) => n.nodes).map((n) => [n.id, n]),
  );
  return {
    ...draft,
    editorVersion: 4,
    roots: structuredClone(
      draft.editorVersion === 3 ? draft.roots : baseline.roots,
    ),
    calendars: structuredClone(baseline.calendars ?? []),
    schedules: draft.networks.map((network) => {
      const original = baseline.schedules?.find((s) => s.id === network.path);
      const root = draft.roots?.some((r) => r.node.id === network.path);
      if (!original && !network.path.startsWith("new:"))
        throw new Error("元版の予定属性を確認できません。");
      return original
        ? structuredClone(original)
        : { id: network.path, attributes: root ? { sd: ["0,ud"] } : {} };
    }),
    networks: draft.networks.map((n) => ({
      ...n,
      nodes: n.nodes.map((node) => {
        if (!isJudgment(node.type) || (draft.editorVersion ?? 1) >= 2)
          return node;
        const original = originals.get(node.id);
        if (!original || original.type !== node.type)
          throw new Error("元版の判定ジョブを確認できません。");
        return {
          ...node,
          attributes: { ...original.attributes, ...node.attributes },
        };
      }),
    })),
  };
}
export function snap(x: number, y: number) {
  return {
    x: 80 + Math.max(0, Math.min(99, Math.round((x - 80) / 160))) * 160,
    y: 48 + Math.max(0, Math.min(99, Math.round((y - 48) / 96))) * 96,
  };
}
export function replaceNetwork(
  draft: EditorDraft,
  network: EditorNetwork,
): EditorDraft {
  return {
    ...draft,
    networks: draft.networks.map((n) =>
      n.path === network.path ? network : n,
    ),
  };
}
export function replaceNode(
  network: EditorNetwork,
  node: EditorNode,
): EditorNetwork {
  return {
    ...network,
    nodes: network.nodes.map((n) => (n.id === node.id ? node : n)),
  };
}
export function removeNode(network: EditorNetwork, id: string): EditorNetwork {
  return {
    ...network,
    nodes: network.nodes.filter((n) => n.id !== id),
    edges: network.edges.filter((e) => e.from !== id && e.to !== id),
  };
}
export function newJob(network: EditorNetwork, type: string): EditorNode {
  if (!canEdit(type, 3)) throw new Error("追加できないジョブ種別です。");
  const prefix = isNetwork(type)
    ? "ネット"
    : isJudgment(type)
      ? "判定"
      : "ジョブ";
  let number = 1;
  while (network.nodes.some((n) => n.name === `${prefix}${number}`)) number++;
  // 空いているグリッドに新しいジョブだけを配置します。
  let cell = 0;
  while (
    cell < 10000 &&
    network.nodes.some(
      (n) =>
        n.x === 80 + (cell % 100) * 160 &&
        n.y === 48 + Math.floor(cell / 100) * 96,
    )
  )
    cell++;
  if (cell === 10000) throw new Error("配置できる空きグリッドがありません。");
  return {
    id: `new:${crypto.randomUUID()}`,
    name: `${prefix}${number}`,
    type,
    attributes: isNetwork(type)
      ? {}
      : isJudgment(type)
        ? { ej: ["gt"], ejc: ["0"] }
        : { te: ["true"] },
    x: 80 + (cell % 100) * 160,
    y: 48 + Math.floor(cell / 100) * 96,
  };
}
export type History = {
  past: EditorDraft[];
  present: EditorDraft;
  future: EditorDraft[];
};
export function change(history: History, draft: EditorDraft): History {
  if (JSON.stringify(history.present) === JSON.stringify(draft)) return history;
  return {
    past: [...history.past.slice(-99), history.present],
    present: draft,
    future: [],
  };
}
export function undo(history: History): History {
  return history.past.length
    ? {
        past: history.past.slice(0, -1),
        present: history.past.at(-1)!,
        future: [history.present, ...history.future],
      }
    : history;
}
export function redo(history: History): History {
  return history.future.length
    ? {
        past: [...history.past, history.present],
        present: history.future[0],
        future: history.future.slice(1),
      }
    : history;
}
