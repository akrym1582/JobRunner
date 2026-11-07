import { render, screen, within } from "@testing-library/react";
import { expect, it } from "vitest";
import { MoveUnits } from "./MoveUnits";
import type { EditorDraft } from "../api/@types";

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
