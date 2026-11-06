import { describe, expect, it } from "vitest";
import type { EditorDraft } from "../api/@types";
import { upgradeDraft, change, undo, redo } from "./model";
import { addRoot, removeHierarchyNode } from "./hierarchy";

const baseline: EditorDraft = {
  definitionHash: "fixed",
  editorVersion: 5,
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
      edges: [],
      nodes: [
        {
          id: "/root/job",
          name: "job",
          type: "j",
          attributes: { te: ["original"] },
          x: 80,
          y: 48,
        },
      ],
    },
  ],
  schedules: [{ id: "/root", attributes: { sd: ["0,ud"] } }],
  calendars: [],
  execution: [
    { id: "/root", attributes: { ha: ["y"], sy: ["1,C30"] } },
    { id: "/root/job", attributes: { jd: ["ab"], etm: ["10"] } },
  ],
};
describe("版5への実行属性移行", () => {
  it.each([1, 2, 3, 4])(
    "版%sの編集を保持して欠落属性を元版から補う",
    (version) => {
      const old = structuredClone(baseline);
      old.editorVersion = version;
      delete old.execution;
      old.networks[0].nodes[0].attributes.te = ["edited"];
      if (version < 3) delete old.roots;
      if (version < 4) {
        delete old.schedules;
        delete old.calendars;
      } else old.schedules![0].attributes.sd = ["1,2026/11/6"];
      const upgraded = upgradeDraft(old, baseline);
      expect(upgraded.editorVersion).toBe(5);
      expect(upgraded.execution).toEqual(baseline.execution);
      expect(upgraded.networks[0].nodes[0].attributes.te).toEqual(["edited"]);
      expect(upgraded.schedules![0].attributes.sd).toEqual(
        version === 4 ? ["1,2026/11/6"] : ["0,ud"],
      );
      expect(old.execution).toBeUndefined();
    },
  );
  it("版4での改名・削除・新規作成を戻さず、新規ルートは保留ありで補う", () => {
    let old = structuredClone(baseline);
    old.editorVersion = 4;
    delete old.execution;
    old.roots![0].node.name = "renamed";
    old = removeHierarchyNode(old, "/root/job");
    old = addRoot(old, 0, "/", "newroot");
    const upgraded = upgradeDraft(old, baseline);
    expect(upgraded.roots).toEqual(old.roots);
    expect(upgraded.execution).toEqual([
      baseline.execution![0],
      { id: old.roots![1].node.id, attributes: { ha: ["y"] } },
    ]);
    const added = addRoot(upgraded, 0, "/", "another");
    expect(added.execution!.at(-1)!.attributes).toEqual({ ha: ["y"] });
    expect(
      removeHierarchyNode(added, "/root").execution?.some(
        (e) => e.id === "/root",
      ),
    ).toBe(false);
    const history = change({ past: [], present: upgraded, future: [] }, added);
    expect(undo(history).present).toEqual(upgraded);
    expect(redo(undo(history)).present).toEqual(added);
  });
  it("元版不一致・欠落投影・種別の食い違いを拒否する", () => {
    const old = { ...structuredClone(baseline), editorVersion: 4 };
    expect(() =>
      upgradeDraft(old, { ...baseline, definitionHash: "changed" }),
    ).toThrow();
    expect(() => upgradeDraft(old, { ...baseline, execution: null })).toThrow();
    expect(() => upgradeDraft(old, { ...baseline, execution: [] })).toThrow();
    old.networks[0].nodes[0].type = "pj";
    expect(() => upgradeDraft(old, baseline)).toThrow();
  });
});
