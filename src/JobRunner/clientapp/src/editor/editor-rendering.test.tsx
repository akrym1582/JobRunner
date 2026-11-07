import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render } from "@testing-library/react";
import type { EditorNetwork } from "../api/@types";
import { EditorGraph } from "./EditorGraph";
import { EditorLists } from "./EditorLists";

afterEach(cleanup);
const initial = (): EditorNetwork => ({
  path: "/net",
  nodes: [
    {
      id: "a",
      name: "開始",
      type: "j",
      x: 80,
      y: 48,
      attributes: { te: ["original"] },
    },
    { id: "b", name: "後続", type: "j", x: 240, y: 48, attributes: {} },
  ],
  edges: [{ from: "a", to: "b", conditional: false }],
});

describe("編集図と一覧の操作時の最新値", () => {
  it("表示が同じ属性変更後の移動でも新しい属性と操作関数を使う", () => {
    const network = initial(),
      oldMove = vi.fn(),
      onMove = vi.fn(),
      onSelect = vi.fn(),
      onOpen = vi.fn();
    const props = {
      network,
      selected: "a",
      connecting: false,
      onSelect,
      onOpen,
      onMove: oldMove,
    };
    const view = render(<EditorGraph {...props} />);
    const updated = {
      ...network,
      nodes: [
        { ...network.nodes[0], attributes: { te: ["latest"] } },
        network.nodes[1],
      ],
    };
    view.rerender(<EditorGraph {...props} network={updated} onMove={onMove} />);
    fireEvent.keyDown(view.getByRole("button", { name: "編集 開始" }), {
      key: "ArrowRight",
    });
    expect(oldMove).not.toHaveBeenCalled();
    expect(onMove).toHaveBeenCalledWith(updated.nodes[0], 240, 48);
    fireEvent.keyDown(view.getByRole("button", { name: "編集 後続" }), {
      key: "Enter",
      ctrlKey: true,
    });
    expect(onSelect).toHaveBeenCalledWith("b", true);
    fireEvent.doubleClick(view.getByText("後続", { selector: "text" }));
    expect(onOpen).toHaveBeenCalledWith("b");
  });
  it("改名・選択・座標・種別・接続変更と元に戻した値を表示する", () => {
    const network = initial();
    const props = {
      network,
      selected: "a",
      selection: ["a"],
      connecting: false,
      onSelect: vi.fn(),
      onMove: vi.fn(),
      onOpen: vi.fn(),
    };
    const view = render(<EditorGraph {...props} />);
    const oldPath = view.container
      .querySelector(".editor-edge path")!
      .getAttribute("d");
    const updated = {
      ...network,
      nodes: [
        network.nodes[0],
        { ...network.nodes[1], name: "変更後", x: 400 },
      ],
    };
    view.rerender(
      <EditorGraph
        {...props}
        network={updated}
        selection={["a", "b"]}
        typeOf={(n) => (n.id === "b" ? "rj" : n.type)}
      />,
    );
    const node = view.getByRole("button", { name: "編集 変更後" });
    expect(node.getAttribute("aria-pressed")).toBe("true");
    expect(node.textContent).toContain("RJ");
    expect(node.textContent).toContain("400, 48");
    expect(
      view.container.querySelector(".editor-edge")!.getAttribute("data-kind"),
    ).toBe("recovery");
    expect(
      view.container.querySelector(".editor-edge path")!.getAttribute("d"),
    ).not.toBe(oldPath);
    view.rerender(
      <EditorGraph
        {...props}
        network={{
          ...updated,
          edges: [{ ...updated.edges[0], conditional: true }],
        }}
      />,
    );
    expect(
      view.container.querySelector(".editor-edge")!.getAttribute("data-kind"),
    ).toBe("condition");
    view.rerender(<EditorGraph {...props} />);
    expect(
      view
        .getByRole("button", { name: "編集 後続" })
        .getAttribute("aria-pressed"),
    ).toBe("false");
    expect(
      view.container.querySelector(".editor-edge path")!.getAttribute("d"),
    ).toBe(oldPath);
  });
  it("図に座標のないユニットを表示せず、修正後に図を戻す", () => {
    const network = initial();
    const props = {
      network,
      selected: "",
      connecting: false,
      onSelect: vi.fn(),
      onMove: vi.fn(),
      onOpen: vi.fn(),
    };
    const view = render(
      <EditorGraph
        {...props}
        network={{
          ...network,
          nodes: [{ ...network.nodes[0], x: null }, network.nodes[1]],
        }}
      />,
    );
    expect(view.getByRole("alert").textContent).toContain("表示座標");
    view.rerender(<EditorGraph {...props} />);
    expect(view.queryByRole("alert")).toBeNull();
    expect(view.getByRole("button", { name: "編集 開始" })).toBeDefined();
  });
  it("一覧の改名・接続順変更後も最新の選択と削除操作を使う", () => {
    const network = initial(),
      oldSelect = vi.fn(),
      onSelect = vi.fn(),
      oldRemove = vi.fn(),
      onRemoveEdge = vi.fn();
    const props = {
      network,
      selected: "a",
      typeOf: () => "j",
      onSelect: oldSelect,
      onRemoveEdge: oldRemove,
    };
    const view = render(<EditorLists {...props} />);
    view.rerender(
      <EditorLists
        {...props}
        network={{
          ...network,
          nodes: [
            { ...network.nodes[0], name: "変更後", y: 144 },
            network.nodes[1],
          ],
          edges: [{ from: "b", to: "a", conditional: true }],
        }}
        selected="b"
        typeOf={() => "rj"}
        onSelect={onSelect}
        onRemoveEdge={onRemoveEdge}
      />,
    );
    fireEvent.click(
      view.container.querySelector("button[data-unit-id='a'] small")!,
    );
    expect(onSelect).toHaveBeenCalledWith("a");
    expect(oldSelect).not.toHaveBeenCalled();
    const row = view.container.querySelector(".editor-connection-list > div")!;
    expect(row.textContent).toContain("後続 → 変更後");
    expect(row.textContent).toContain("条件成立");
    fireEvent.click(row.querySelector("svg")!);
    expect(onRemoveEdge).toHaveBeenCalledWith(0);
    expect(oldRemove).not.toHaveBeenCalled();
    expect(
      view.container
        .querySelector("button[data-unit-id='b']")!
        .getAttribute("aria-pressed"),
    ).toBe("true");
  });
});
