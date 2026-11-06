import { describe, expect, it } from "vitest";
import type { EditorDocument, EditorDraft } from "../api/@types";
import { groupPath, originalGroupPath } from "./group-paths";
import { changeGroup } from "./groups";
import { executionTargets } from "./execution";
import { networkNames } from "./hierarchy";
import { creationDocument, createFile, createGroup } from "./creations";
import { upgradeDraft, change, undo, redo } from "./model";
const draft: EditorDraft = {
  definitionHash: "fixed",
  editorVersion: 7,
  groupChanges: [],
  newGroups: [],
  newFiles: [],
  localSources: [],
  calendars: [
    { id: "/g", attributes: { cl: ["su"] } },
    { id: "/g/c", attributes: {} },
    { id: "/keep", attributes: {} },
  ],
  execution: [
    { id: "/g", attributes: {} },
    { id: "/g/c", attributes: {} },
    { id: "/keep", attributes: {} },
    { id: "/g/c/solo", type: "j", attributes: {} },
    { id: "/g/c/net", type: "n", attributes: { ha: ["y"] } },
  ],
  schedules: [{ id: "/g/c/net", attributes: { jc: ["/g/c"] } }],
  networks: [{ path: "/g/c/net", nodes: [], edges: [] }],
  roots: [
    {
      node: {
        id: "/g/c/net",
        type: "n",
        name: "net",
        attributes: {},
        x: null,
        y: null,
      },
      sourceIndex: 0,
      parentPath: "/g/c",
    },
  ],
};
const base: EditorDocument = {
  draft,
  sources: [
    {
      fileName: "base.ajs",
      encoding: "utf-8",
      targetPath: "/",
      text: "original",
      changed: false,
    },
  ],
  containers: [
    { sourceIndex: 0, parentPath: "/g" },
    { sourceIndex: 0, parentPath: "/g/c" },
    { sourceIndex: 0, parentPath: "/keep" },
  ],
};
describe("グループの安定IDと表示名", () => {
  it("親子改名・参照・実行属性・ネット名を同じ対象へ対応付ける", () => {
    let next = changeGroup(draft, { id: "/g", name: "new" });
    next = changeGroup(next, { id: "/g/c", name: "calendar" });
    expect(groupPath(next, "/g/c/solo")).toBe("/new/calendar/solo");
    expect(originalGroupPath(next, "/new/calendar/future")).toBe("/g/c/future");
    expect(networkNames(next).get("/g/c/net")).toBe("/new/calendar/net");
    expect(executionTargets(next).find((t) => t.id === "/g/c/solo")!.name).toBe(
      "/new/calendar/solo",
    );
    expect(next.schedules![0].attributes.jc).toEqual(["/g/c"]);
    const history = change({ past: [], present: draft, future: [] }, next);
    expect(undo(history).present).toEqual(draft);
    expect(redo(undo(history)).present).toEqual(next);
  });
  it("子孫削除を履歴に含め、消した対象をフォームへ戻さない", () => {
    const next = changeGroup(draft, { id: "/g", delete: true });
    expect(next.networks).toEqual([]);
    expect(next.roots).toEqual([]);
    expect(next.schedules).toEqual([]);
    expect(executionTargets(next).map((t) => t.id)).toEqual(["/keep"]);
    expect(creationDocument(base, next).containers).toEqual([
      { sourceIndex: 0, parentPath: "/keep" },
    ]);
    expect(groupPath(next, "/g/c")).toBeNull();
    expect(draft.networks).toHaveLength(1);
  });
  it("版6の追加ファイル・グループ・省略を失わず移行する", () => {
    let old: EditorDraft = { ...structuredClone(draft), editorVersion: 6 };
    delete old.groupChanges;
    old = createGroup(old, { sourceIndex: 0, parentPath: "/g", name: "added" });
    old = createFile(old, {
      fileName: "extra.ajs",
      targetPath: "/newarea",
      encoding: "utf-16",
      bom: true,
      groupName: "extra",
    });
    old.execution!.find((e) => e.id === "/g/c/net")!.attributes = {};
    const next = upgradeDraft(old, draft);
    expect(next.groupChanges).toEqual([]);
    expect(next.newFiles).toEqual(old.newFiles);
    expect(next.newGroups).toEqual(old.newGroups);
    expect(
      next.execution!.find((e) => e.id === "/g/c/net")!.attributes,
    ).toEqual({});
    expect(next.calendars).toEqual(old.calendars);
  });
});
