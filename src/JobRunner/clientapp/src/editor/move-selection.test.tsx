import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { EditorDraft } from "../api/@types";
import { MoveSelection } from "./MoveSelection";

const post = vi.hoisted(() => vi.fn());
vi.mock("../client", () => ({
  client: { ui: { definition_editor: { move_selection: { $post: post } } } },
}));
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});
const node = (id: string, name: string, type = "n") => ({
  id,
  name,
  type,
  attributes: {},
  x: null,
  y: null,
});
const draft: EditorDraft = {
  definitionHash: "base",
  editorVersion: 9,
  roots: [
    { node: node("/a", "renamed"), sourceIndex: 0, parentPath: "/" },
    { node: node("/b", "destination"), sourceIndex: 0, parentPath: "/" },
  ],
  networks: [
    {
      path: "/a",
      nodes: [node("/a/child", "child"), node("/a/j", "j", "j")],
      edges: [],
    },
    { path: "/a/child", nodes: [node("/a/child/deep", "deep")], edges: [] },
    { path: "/a/child/deep", nodes: [], edges: [] },
    { path: "/b", nodes: [], edges: [] },
  ],
};
it("自己と選択した子孫を除外し、現在の名前で移動先を表示する", async () => {
  const onMove = vi.fn(async (load: () => Promise<unknown>) => {
    await load();
  });
  render(
    <MoveSelection
      draft={draft}
      current="/a"
      selected={["/a/child", "/a/j"]}
      disabled={false}
      onMove={onMove}
    />,
  );
  expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual([
    "/destination",
  ]);
  fireEvent.click(
    screen.getByRole("button", { name: "選択の移動候補を確認（2件）" }),
  );
  expect(onMove).toHaveBeenCalledOnce();
  expect(post).toHaveBeenCalledWith({
    body: { draft, ids: ["/a/child", "/a/j"], targetNetworkId: "/b" },
  });
});
it("未選択と操作禁止中は移動を開始しない", () => {
  const onMove = vi.fn();
  const view = render(
    <MoveSelection
      draft={draft}
      current="/a"
      selected={[]}
      disabled={false}
      onMove={onMove}
    />,
  );
  fireEvent.click(screen.getByRole("button"));
  expect(onMove).not.toHaveBeenCalled();
  view.rerender(
    <MoveSelection
      draft={draft}
      current="/a"
      selected={["/a/j"]}
      disabled={true}
      onMove={onMove}
    />,
  );
  fireEvent.click(screen.getByRole("button"));
  expect(onMove).not.toHaveBeenCalled();
});

it("新規下書きのIDがパス形式でなくても子孫を移動先にしない", () => {
  const fresh: EditorDraft = {
    ...draft,
    networks: [
      { path: "/a", nodes: [node("new:child", "child")], edges: [] },
      { path: "new:child", nodes: [node("new:deep", "deep")], edges: [] },
      { path: "new:deep", nodes: [], edges: [] },
      { path: "/b", nodes: [], edges: [] },
    ],
  };
  render(
    <MoveSelection
      draft={fresh}
      current="/a"
      selected={["new:child"]}
      disabled={false}
      onMove={async () => {}}
    />,
  );
  expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual([
    "/destination",
  ]);
});
