import { describe, expect, it } from "vitest";
import {
  applyNestedSimple,
  defaults,
  readSimple,
  upperRule,
  type Attributes,
} from "./simple-schedule";
import { scheduleParentId, effectiveScheduleRules } from "./schedule-hierarchy";
import type { EditorDraft } from "../api/@types";
const node = (id: string, name: string) => ({
  id,
  name,
  type: "n",
  attributes: {},
  x: 80,
  y: 48,
});
function draft(): EditorDraft {
  return {
    definitionHash: "base",
    editorVersion: 9,
    roots: [{ node: node("/n", "renamed"), parentPath: "/", sourceIndex: 0 }],
    networks: [
      { path: "/n", nodes: [node("/n/c", "child")], edges: [] },
      { path: "/n/c", nodes: [node("/n/c/h", "grand")], edges: [] },
      { path: "/n/c/h", nodes: [], edges: [] },
    ],
    schedules: [
      { id: "/n", attributes: { sd: ["7,en", "2,en"] } },
      { id: "/n/c", attributes: {} },
      { id: "/n/c/h", attributes: {} },
    ],
  };
}
describe("ネストの依存と標準ルールの局所編集", () => {
  it("上位依存からの独自設定は暗黙のルート第1ルールを作らない", () => {
    const input = { st: ["9,08:00"], jc: ["/暦"] };
    const value = { ...defaults(), calendar: "/暦" };
    const output = applyNestedSimple(
      input,
      1,
      value,
      undefined,
      "own",
      "parent",
      "7",
      "1",
    );
    expect(output.sd).toEqual(["1,25"]);
    expect(output.ln).toEqual(["1,7"]);
    expect(output.de).toEqual(["n"]);
    expect(output.st).toEqual(["9,08:00", "1,09:00"]);
    expect(input).toEqual({ st: ["9,08:00"], jc: ["/暦"] });
  });
  it("既存の独自設定では番号省略・ln省略と無変更属性を残す", () => {
    const input: Attributes = {
      de: ["n"],
      sd: ["25", "7,*b"],
      cy: ["(1,m)", "7,(1,m)"],
      ln: ["7,2"],
      jc: ["/暦"],
      st: ["+00:00"],
    };
    const original = readSimple(input, 1)!;
    expect(upperRule(input, 1)).toBe("1");
    expect(
      applyNestedSimple(input, 1, original, original, "own", "own", "1", "1"),
    ).toEqual(input);
    const output = applyNestedSimple(
      input,
      1,
      { ...original, time: "01:00" },
      original,
      "own",
      "own",
      "2",
      "1",
    );
    expect(output.ln).toEqual(["7,2", "1,2"]);
    expect(output.st).toEqual(["1,+01:00"]);
    expect(output.sd).toEqual(input.sd);
    expect(output.cy).toEqual(input.cy);
  });
  it("上位依存への切替でsdだけを外し、他ルールの属性と未知属性を消さない", () => {
    const input = {
      de: ["n"],
      sd: ["1,25", "7,*b"],
      cy: ["1,(1,m)", "7,(1,m)"],
      ln: ["1,2", "7,3"],
      jc: ["/暦"],
      custom: ["untouched"],
    };
    const output = applyNestedSimple(
      input,
      1,
      defaults(),
      undefined,
      "parent",
      "own",
      "1",
      "1",
    );
    const { sd: _sd, ...preserved } = input;
    expect(output).toEqual({ ...preserved, de: ["y"] });
    expect(
      applyNestedSimple(
        {},
        1,
        defaults(),
        undefined,
        "parent",
        "parent",
        "1",
        "1",
      ),
    ).toEqual({});
    expect(_sd).toHaveLength(2);
  });
  it("親が上位依存なら祖先のルールを案内し、独自になれば親のルールを使う", () => {
    const d = draft();
    expect(scheduleParentId(d, "/n/c/h")).toBe("/n/c");
    expect(effectiveScheduleRules(d, "/n/c")).toEqual({
      source: "/n",
      rules: [2, 7],
    });
    d.schedules![1].attributes = { de: ["n"], sd: ["5,en"] };
    expect(effectiveScheduleRules(d, "/n/c")).toEqual({
      source: "/n/c",
      rules: [5],
    });
  });
  it("ルートを別ネットへ移動した下書きでも親の安定IDを使う", () => {
    const d = draft();
    d.roots!.push({
      node: node("/other", "other"),
      parentPath: "/",
      sourceIndex: 0,
    });
    d.networks.push({ path: "/other", nodes: [], edges: [] });
    d.networkChanges = [{ id: "/other", parentPath: "/n/c", type: "n" }];
    expect(scheduleParentId(d, "/other")).toBe("/n/c");
  });
});
