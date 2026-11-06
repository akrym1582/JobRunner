import { describe, it, expect } from "vitest";
import type { EditorDraft } from "../api/@types";
import {
  changeNetwork,
  networkType,
  movedGroupConflict,
} from "./network-changes";
import { networkNames, removeHierarchyNode } from "./hierarchy";
import { executionTargets } from "./execution";
import { change, undo, redo, upgradeDraft } from "./model";

const draft: EditorDraft = {
  editorVersion: 8,
  definitionHash: "base",
  networkChanges: [],
  groupChanges: [],
  newGroups: [],
  newFiles: [],
  localSources: [],
  roots: [
    {
      sourceIndex: 0,
      parentPath: "/a",
      node: {
        id: "/a/net",
        type: "n",
        name: "net",
        attributes: {},
        x: null,
        y: null,
      },
    },
  ],
  networks: [
    {
      path: "/a/net",
      nodes: [
        {
          id: "/a/net/child",
          type: "n",
          name: "child",
          attributes: { cm: ["keep"] },
          x: 80,
          y: 48,
        },
      ],
      edges: [],
    },
    { path: "/a/net/child", nodes: [], edges: [] },
  ],
  calendars: [
    { id: "/a", attributes: {} },
    { id: "/b", attributes: {} },
  ],
  schedules: [
    { id: "/a/net", attributes: {} },
    { id: "/a/net/child", attributes: {} },
  ],
  execution: ["/a", "/b", "/a/net", "/a/net/child"].map((id) => ({
    id,
    attributes: {},
  })),
};
describe("ネット操作の投影と履歴", () => {
  it("移動と種別を表示し、元ID・属性とundo/redoを保持する", () => {
    let next = changeNetwork(draft, { id: "/a/net", parentPath: "/b" });
    next = changeNetwork(next, { id: "/a/net/child", type: "rn" });
    next = { ...next, groupChanges: [{ id: "/b", name: "renamed" }] };
    expect(networkNames(next).get("/a/net/child")).toBe("/renamed/net/child");
    expect(networkType(next, next.networks[0].nodes[0])).toBe("rn");
    expect(next.networks).toEqual(draft.networks);
    expect(
      executionTargets(next).find((t) => t.id === "/a/net/child"),
    ).toMatchObject({ name: "/renamed/net/child", type: "rn" });
    const history = change({ past: [], present: draft, future: [] }, next);
    expect(undo(history).present).toEqual(draft);
    expect(redo(undo(history)).present).toEqual(next);
    expect(removeHierarchyNode(next, "/a/net").networkChanges).toEqual([]);
    expect(movedGroupConflict(next, "/a")).toBe(true);
    expect(movedGroupConflict(next, "/b")).toBe(true);
    expect(movedGroupConflict(next, "/other")).toBe(false);
  });
  it("版7の改名・作成・省略属性を版8移行で失わない", () => {
    const old: EditorDraft = {
      ...structuredClone(draft),
      editorVersion: 7,
      networkChanges: undefined,
      groupChanges: [{ id: "/b", name: "renamed" }],
    };
    old.newGroups = [{ sourceIndex: 0, parentPath: "/a", name: "added" }];
    old.calendars!.push({ id: "/a/added", attributes: {} });
    old.execution!.push({ id: "/a/added", attributes: {} });
    const upgraded = upgradeDraft(old, draft);
    expect(upgraded.editorVersion).toBe(8);
    expect(upgraded.groupChanges).toEqual(old.groupChanges);
    expect(upgraded.newGroups).toEqual(old.newGroups);
    expect(
      upgraded.execution!.find((e) => e.id === "/a/net")!.attributes,
    ).toEqual({});
    expect(upgraded.networkChanges).toEqual([]);
  });
});
