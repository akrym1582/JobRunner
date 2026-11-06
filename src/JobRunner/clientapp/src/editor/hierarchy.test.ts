import { describe, expect, it } from "vitest";
import type { EditorDraft } from "../api/@types";
import {
  addRoot,
  networkNames,
  removeHierarchyNode,
  updateNetworkNode,
} from "./hierarchy";
import { upgradeDraft } from "./model";
const baseline: EditorDraft = {
  definitionHash: "fixed",
  editorVersion: 4,
  schedules: [
    { id: "/root", attributes: { sd: ["0,ud"] } },
    { id: "/root/child", attributes: {} },
  ],
  calendars: [],
  roots: [
    {
      node: {
        id: "/root",
        name: "root",
        type: "n",
        attributes: {},
        x: null,
        y: null,
      },
      sourceIndex: 0,
      parentPath: "/",
    },
  ],
  networks: [
    {
      path: "/root",
      nodes: [
        {
          id: "/root/child",
          name: "child",
          type: "n",
          attributes: {},
          x: 80,
          y: 48,
        },
        {
          id: "/root/j",
          name: "j",
          type: "j",
          attributes: { te: ["true"] },
          x: 240,
          y: 48,
        },
      ],
      edges: [{ from: "/root/child", to: "/root/j", conditional: false }],
    },
    {
      path: "/root/child",
      nodes: [
        {
          id: "/root/child/judge",
          name: "judge",
          type: "jdj",
          attributes: { ej: ["eq"], ejc: ["0"], cm: ["old"] },
          x: 80,
          y: 48,
        },
      ],
      edges: [],
    },
  ],
};
describe("安定IDによるネット階層", () => {
  it("親の改名後も子のID・接続・編集を保ち削除で子孫だけを除く", () => {
    const renamed = updateNetworkNode(baseline, {
      ...baseline.roots![0].node,
      name: "changed",
    });
    expect(networkNames(renamed).get("/root/child")).toBe("/changed/child");
    expect(renamed.networks[0].edges).toEqual(baseline.networks[0].edges);
    expect(baseline.roots![0].node.name).toBe("root");
    const removed = removeHierarchyNode(renamed, "/root/child");
    expect(removed.networks.map((n) => n.path)).toEqual(["/root"]);
    expect(removed.networks[0].nodes.map((n) => n.name)).toEqual(["j"]);
    expect(removed.networks[0].edges).toEqual([]);
    expect(removeHierarchyNode(renamed, "/root").roots).toEqual([]);
  });
  it("版2の編集値と属性削除を維持して元版の所属情報だけを補う", () => {
    const old = structuredClone(baseline);
    old.editorVersion = 2;
    delete old.roots;
    old.networks[1].nodes[0].attributes.ejc = ["5"];
    delete old.networks[1].nodes[0].attributes.cm;
    const upgraded = upgradeDraft(old, baseline);
    expect(upgraded.roots).toEqual(baseline.roots);
    expect(upgraded.networks[1].nodes[0].attributes).toEqual({
      ej: ["eq"],
      ejc: ["5"],
    });
    expect(old.roots).toBeUndefined();
  });
  it("空の下書きにもルートを作り直せる", () => {
    const empty = removeHierarchyNode(baseline, "/root");
    const added = addRoot(empty, 1, "/team", "new");
    expect(added.roots![0].sourceIndex).toBe(1);
    expect(networkNames(added).get(added.networks[0].path)).toBe("/team/new");
    expect(empty.networks).toEqual([]);
  });
});

it("版3の新規・改名・削除を維持し予定とカレンダーだけ補う", () => {
  const old = structuredClone(baseline);
  old.editorVersion = 3;
  delete old.schedules;
  delete old.calendars;
  const renamed = updateNetworkNode(old, {
    ...old.roots![0].node,
    name: "renamed",
  });
  const deleted = removeHierarchyNode(renamed, "/root/child");
  const added = addRoot(deleted, 0, "/", "newroot");
  const migrated = upgradeDraft(added, baseline);
  expect(migrated.roots).toEqual(added.roots);
  expect(networkNames(migrated).get("/root")).toBe("/renamed");
  expect(migrated.schedules!.map((s) => s.id)).toEqual(
    migrated.networks.map((n) => n.path),
  );
  expect(migrated.schedules![1].attributes).toEqual({ sd: ["0,ud"] });
  expect(migrated.calendars).toEqual(baseline.calendars);
  expect(added.schedules).toBeUndefined();
  expect(() => upgradeDraft(added, { ...baseline, schedules: null })).toThrow();
});
