import { describe, expect, it } from "vitest";
import { newJob, snap, upgradeDraft } from "./model";
import type { EditorDraft } from "../api/@types";

describe("JP1の100列・100行グリッド", () => {
  it("座標上限でも100番目のセルへスナップする", () => {
    expect(snap(16000, 10000)).toEqual({ x: 15920, y: 9552 });
    expect(snap(15920, 9552)).toEqual({ x: 15920, y: 9552 });
    expect(snap(-1, -1)).toEqual({ x: 80, y: 48 });
  });
});

describe("判定属性がない旧下書き", () => {
  const baseline: EditorDraft = {
    definitionHash: "old-version",
    editorVersion: 4,
    schedules: [{ id: "/net", attributes: { sd: ["0,ud"] } }],
    calendars: [],
    roots: [
      {
        node: {
          id: "/net",
          name: "net",
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
        path: "/net",
        edges: [],
        nodes: [
          {
            id: "/net/a",
            name: "a",
            type: "j",
            attributes: { te: ["original"] },
            x: 80,
            y: 48,
          },
          {
            id: "/net/judge",
            name: "judge",
            type: "jdj",
            attributes: {
              ej: ["ri"],
              ejl: ["1"],
              ejh: ["9"],
              ha: ["y"],
              cm: ["keep"],
            },
            x: 240,
            y: 48,
          },
        ],
      },
    ],
  };
  it("元版から判定属性だけを補い、既存の編集・接続・座標を維持する", () => {
    const old = structuredClone(baseline);
    old.editorVersion = null;
    old.networks[0].nodes[0].attributes.te = ["edited"];
    old.networks[0].nodes[1].attributes = { cm: ["keep"] };
    old.networks[0].nodes[1].x = 560;
    old.networks[0].edges = [
      { from: "/net/a", to: "/net/judge", conditional: false },
    ];
    const upgraded = upgradeDraft(old, baseline);
    expect(upgraded.editorVersion).toBe(4);
    expect(upgraded.networks[0].nodes[0].attributes.te).toEqual(["edited"]);
    expect(upgraded.networks[0].nodes[1].attributes).toEqual(
      baseline.networks[0].nodes[1].attributes,
    );
    expect(upgraded.networks[0].nodes[1].x).toBe(560);
    expect(upgraded.networks[0].edges).toEqual(old.networks[0].edges);
    expect(old.networks[0].nodes[1].attributes).toEqual({ cm: ["keep"] });
    expect(() =>
      upgradeDraft(old, { ...baseline, definitionHash: "active-new-version" }),
    ).toThrow();
    expect(() =>
      upgradeDraft({ ...old, editorVersion: 5 }, baseline),
    ).toThrow();
  });
  it("版8の移動・グループ変更と明示削除を版9へ保持する", () => {
    const old: EditorDraft = {
      ...structuredClone(baseline),
      editorVersion: 8,
      execution: [
        { id: "/net", type: "n", attributes: {} },
        { id: "/net/a", type: "j", attributes: {} },
        { id: "/net/judge", type: "jdj", attributes: {} },
      ],
      networkChanges: [{ id: "/net", parentPath: "/target" }],
      groupChanges: [{ id: "/unused", delete: true }],
      newGroups: [],
      newFiles: [],
    };
    old.networks[0].nodes[1].attributes = {};
    const current: EditorDraft = {
      ...structuredClone(baseline),
      editorVersion: 9,
      execution: old.execution!.map((entry) => ({
        ...entry,
        attributes: { ha: ["y"] },
      })),
      networkChanges: [],
      groupChanges: [],
      newGroups: [],
      newFiles: [],
    };
    expect(upgradeDraft(old, current)).toEqual({ ...old, editorVersion: 9 });
  });
  it("判定とリカバリー判定にコマンド属性を混入しない", () => {
    for (const kind of ["jdj", "rjdj"]) {
      const node = newJob(baseline.networks[0], kind);
      expect(node.attributes).toEqual({ ej: ["gt"], ejc: ["0"] });
      expect(node.type).toBe(kind);
    }
  });
});
