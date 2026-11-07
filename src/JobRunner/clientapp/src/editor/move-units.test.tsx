import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { MoveUnits } from "./MoveUnits";
import type { EditorDraft } from "../api/@types";

afterEach(cleanup);

it("新規ファイルを含む現在の原文一覧を重複させず移動先に使う", () => {
  const draft: EditorDraft = {
    definitionHash: "base",
    editorVersion: 9,
    networks: [],
    calendars: [],
    newFiles: [
      {
        fileName: "new.ajs",
        targetPath: "/",
        encoding: "utf-8",
        bom: false,
        groupName: "new",
      },
    ],
  };
  render(
    <MoveUnits
      draft={draft}
      sources={[
        {
          fileName: "base.ajs",
          targetPath: "/",
          encoding: "utf-8",
          text: "",
          changed: false,
        },
        {
          fileName: "new.ajs",
          targetPath: "/",
          encoding: "utf-8",
          text: "",
          changed: true,
        },
      ]}
      disabled={false}
      onMove={async () => {}}
    />,
  );
  expect(
    within(screen.getByLabelText("移動先ファイル")).getAllByRole("option"),
  ).toHaveLength(2);
  expect(
    within(screen.getByLabelText("移動先グループ")).getByRole("option", {
      name: "/new",
    }),
  ).toBeTruthy();
});

it("既存のルート移動を適用した完全名を候補に表示する", () => {
  const draft: EditorDraft = {
    definitionHash: "base",
    editorVersion: 9,
    networks: [],
    roots: [
      {
        node: {
          id: "/a/n",
          name: "n",
          type: "n",
          attributes: {},
          x: null,
          y: null,
        },
        sourceIndex: 0,
        parentPath: "/a",
      },
    ],
    networkChanges: [{ id: "/a/n", parentPath: "/b" }],
  };
  const view = render(
    <MoveUnits
      draft={draft}
      sources={[]}
      disabled={false}
      onMove={async () => {}}
    />,
  );
  expect(
    within(view.getByLabelText("移動するユニット")).getByRole("option", {
      name: "/b/n",
    }),
  ).toBeTruthy();
  view.unmount();
});
