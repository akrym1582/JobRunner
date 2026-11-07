import { afterEach, expect, it } from "vitest";
import { cleanup, fireEvent, render } from "@testing-library/react";
import { NestedScheduleResults } from "./NestedScheduleResults";
import type { EditorNestedSchedulePreview } from "../api/@types";
afterEach(cleanup);
it("同日時の子を別ルート行として表示し、長い祖先経路は展開した行だけ描画する", () => {
  const point = {
    rule: 1,
    businessDate: "2026-11-05",
    due: "2026-11-05T11:00:00Z",
  };
  const chain = Array.from({ length: 64 }, (_, i) => ({
    path: "/nested/" + i,
    type: i === 63 ? "rn" : "n",
    eligible: true,
    reason: "INHERITED",
    point,
  }));
  const data: EditorNestedSchedulePreview = {
    rootNetworkId: "/n",
    rootPath: "/n",
    timeZone: "UTC",
    rows: [1, 2].map((rule) => ({
      businessDate: point.businessDate,
      root: {
        ...point,
        rule,
        due: `2026-11-05T${rule === 1 ? "09" : "10"}:00:00Z`,
      },
      selected: point,
      eligible: true,
      reason: "INHERITED",
      chain,
    })),
  };
  const view = render(<NestedScheduleResults data={data} />);
  expect(view.container.querySelectorAll("tbody tr")).toHaveLength(2);
  expect(view.container.querySelectorAll("li")).toHaveLength(0);
  const details = view.container.querySelector("details")!;
  // JSDOMのtoggleイベントでもブラウザーと同じopen値を渡します。
  details.open = true;
  fireEvent(details, new Event("toggle"));
  expect(view.container.querySelectorAll("li")).toHaveLength(64);
  expect(view.container.textContent).toContain(
    "実際の起動には異常時の条件が必要",
  );
  details.open = false;
  fireEvent(details, new Event("toggle"));
  expect(view.container.querySelectorAll("li")).toHaveLength(0);
});
