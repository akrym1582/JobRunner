import { describe, expect, it } from "vitest";
import type { EditorDocument, EditorDraft } from "../api/@types";
import { createFile, createGroup, creationDocument } from "./creations";
import { upgradeDraft, change, undo, redo } from "./model";

const draft: EditorDraft = {
  definitionHash: "fixed",
  editorVersion: 6,
  networks: [],
  roots: [],
  schedules: [],
  calendars: [{ id: "/g", attributes: {} }],
  execution: [
    { id: "/g", type: "g", attributes: {} },
    { id: "/solo", type: "j", attributes: { ha: ["y"] } },
  ],
  newGroups: [],
  newFiles: [],
  localSources: [],
};
const base: EditorDocument = {
  draft,
  sources: [
    {
      fileName: "original.ajs",
      targetPath: "/",
      encoding: "utf-8",
      text: "original",
      changed: false,
    },
  ],
  containers: [{ sourceIndex: 0, parentPath: "/g" }],
};
describe("グループとファイルの追加", () => {
  it("原文・旧編集を保持し、追加分を履歴と保存へ含める", () => {
    let added = createGroup(draft, {
      sourceIndex: 0,
      parentPath: "/g",
      name: "child",
    });
    added = createFile(added, {
      fileName: "new.ajs",
      targetPath: "/g/child",
      encoding: "utf-16",
      bom: true,
      groupName: "new",
    });
    const document = creationDocument(base, added);
    expect(document.sources[0]).toEqual(base.sources[0]);
    expect(document.containers).toContainEqual({
      sourceIndex: 1,
      parentPath: "/g/child/new",
    });
    expect(added.execution!.find((e) => e.id === "/solo")).toEqual(
      draft.execution![1],
    );
    const history = change({ past: [], present: draft, future: [] }, added);
    expect(undo(history).present).toEqual(draft);
    expect(redo(undo(history)).present).toEqual(added);
    expect(JSON.parse(JSON.stringify(added))).toEqual(added);
    expect(draft.newFiles).toEqual([]);
    expect(() =>
      createFile({ ...draft, localSources: null }, added.newFiles![0]),
    ).toThrow();
  });
  it.each([1, 2, 3, 4, 5])(
    "版%sの省略・単独ジョブ・編集を移行する",
    (version) => {
      const old = structuredClone(draft);
      old.editorVersion = version;
      delete old.newGroups;
      delete old.newFiles;
      if (version < 5) delete old.execution;
      else old.execution![1].attributes = {};
      const upgraded = upgradeDraft(old, draft);
      expect(upgraded.editorVersion).toBe(6);
      expect(upgraded.newGroups).toEqual([]);
      expect(upgraded.newFiles).toEqual([]);
      expect(
        upgraded.execution!.find((e) => e.id === "/solo")!.attributes,
      ).toEqual(version === 5 ? {} : { ha: ["y"] });
    },
  );
});
