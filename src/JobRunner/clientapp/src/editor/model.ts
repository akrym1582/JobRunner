import type { EditorDraft, EditorNetwork, EditorNode } from "../api/@types";

export const isJob = (type: string) => ["j", "pj", "rj", "rp"].includes(type);
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
  let number = 1;
  while (network.nodes.some((n) => n.name === `ジョブ${number}`)) number++;
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
    name: `ジョブ${number}`,
    type,
    attributes: { te: ["true"] },
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
