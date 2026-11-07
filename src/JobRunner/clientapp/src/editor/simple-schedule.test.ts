import { describe, expect, it } from "vitest";
import {
  applySimple,
  formatSimpleAttributes,
  defaults,
  nextRule,
  readSimple,
  scheduleRules,
  simpleAttributes,
  simpleErrors,
  type Attributes,
} from "./simple-schedule";

describe("JP1標準属性と簡単設定の往復", () => {
  it.each([
    ["daily", "en", "(1,d)", "no"],
    ["working", "en", "(1,d)", "ca"],
    ["weekly", "mo", "(1,w)", "ca"],
    ["monthly", "25", "(1,m)", "ca"],
    ["nth", "*1", "(1,m)", "ca"],
    ["last", "*b", "(1,m)", "ca"],
    ["offset", "25", "(1,m)", "no"],
  ] as const)("%sを標準sd/cy/shへ変換して読み戻す", (pattern, sd, cy, sh) => {
    const value = defaults(pattern);
    const output = applySimple({ sd: ["0,ud"] }, 7, value);
    expect(output.sd).toEqual([`7,${sd}`]);
    expect(output.cy).toEqual([`7,${cy}`]);
    expect(output.sh).toEqual([`7,${sh}`]);
    expect(readSimple(output, 7)).toEqual(value);
    expect(Object.keys(output).sort()).toEqual([
      "cftd",
      "cy",
      "sd",
      "sh",
      "shd",
      "st",
    ]);
  });
  it("生成属性は選択ルールだけを表示し、改名後のカレンダー名をJP1形式で引用する", () => {
    const text = formatSimpleAttributes(
      { sd: ["1,25", "2,en"], jc: ['/暦/#"別'], st: ["1,09:00"], ha: ["y"] },
      1,
      (path) => path.replace("/暦/", "/改名/"),
    );
    expect(text).toBe('sd=1,25;\njc="/改名/###"別";\nst=1,09:00;');
  });
  it("25日起算の後2/前5運用日をcftdへ保存する", () => {
    const value = defaults();
    expect(simpleAttributes(value).cftd).toBe("af,2,10");
    expect(
      simpleAttributes({ ...value, day: "05", count: "02", grace: "03" }),
    ).toMatchObject({ sd: "5", cftd: "af,2,3" });
    expect(
      simpleAttributes({ ...value, direction: "be", count: "5" }).cftd,
    ).toBe("be,5,10");
  });
  it("時刻だけ変更したとき省略と番号なし表記、無効付加値、他ルールと監視を残す", () => {
    const input: Attributes = {
      sd: ["25", "9,*b"],
      cy: ["(1,m)", "9,(1,m)"],
      cftd: ["no,3,12"],
      sy: ["3,09:00"],
      ey: ["9,10:00"],
      ed: ["2030/1/1"],
      ln: ["1,5"],
    };
    const value = readSimple(input, 1)!;
    expect(value.relative).toBe(true);
    expect(value.time).toBe("00:00");
    expect(applySimple(input, 1, value, value)).toEqual(input);
    expect(applySimple(input, 1, { ...value, time: "01:00" }, value)).toEqual({
      ...input,
      st: ["1,+01:00"],
    });
    expect(input.st).toBeUndefined();
  });
  it("ルールごとの変更位置と他属性を保持し、周期を外すのは一回実行への明示変更だけ", () => {
    const input: Attributes = {
      sd: ["2,25", "7,25"],
      st: ["7,09:00", "2,11:00"],
      cy: ["2,(1,m)", "7,(1,m)"],
      sh: ["7,no"],
    };
    const original = readSimple(input, 7)!;
    const output = applySimple(
      input,
      7,
      { ...original, pattern: "once", date: "2028-02-29" },
      original,
    );
    expect(output.sd).toEqual(["2,25", "7,2028/2/29"]);
    expect(output.cy).toEqual(["2,(1,m)"]);
    expect(output.st).toEqual(input.st);
    expect(readSimple(output, 7)?.date).toBe("2028-02-29");
  });
  it("開始年月と参照カレンダーを標準の属性で表す", () => {
    const value = { ...defaults(), month: "2026-11", calendar: "/他/暦" };
    const attrs = applySimple({}, 1, value);
    expect(attrs.sd).toEqual(["1,2026/11/25"]);
    expect(attrs.jc).toEqual(["/他/暦"]);
    expect(readSimple(attrs, 1)).toEqual(value);
    expect(
      applySimple(attrs, 1, { ...value, calendar: "" }, value).jc,
    ).toBeUndefined();
  });
  it.each<Attributes>([
    { sd: ["+3"], cy: ["(1,m)"] },
    { sd: ["25"], cy: ["(1,y)"] },
    { sd: ["25"], cy: ["(1,m)"], cftd: ["da,2"] },
    { sd: ["mo:2"], cy: ["(1,m)"] },
    { sd: ["11/25"], cy: ["(1,m)"] },
    { sd: ["en"] },
    { sd: ["25", "1,26"], cy: ["(1,m)"] },
    { sd: ["0,ud"] },
  ])("対象外の指定を近似変換しない: %j", (attributes) => {
    expect(readSimple(attributes, 1)).toBeUndefined();
  });
  it("監視や上位対応の使用済番号と暗黙の第1ルールも避けて新規番号を選ぶ", () => {
    expect(
      nextRule({
        sd: ["0,ud"],
        st: ["1,09:00"],
        sy: ["2,09:00"],
        ey: ["3,12:00"],
        ln: ["4,2"],
      }),
    ).toBe(5);
    expect(
      nextRule({ sd: ["0,ud"] }, { sy: ["1,12:00"], ey: ["2,13:00"] }),
    ).toBe(3);
    expect(nextRule({})).toBe(2);
    expect(applySimple({}, 2, defaults()).sd).toEqual(["1,en", "2,25"]);
    expect(
      nextRule({ sd: Array.from({ length: 144 }, (_, i) => `${i + 1},en`) }),
    ).toBeUndefined();
    expect(scheduleRules({ sd: ["7,en", "2,*b"] })).toEqual([2, 7]);
  });
  it("予定なし解除時も既存カレンダーと全ての監視を維持する", () => {
    const attrs = { sd: ["0,ud"], jc: ["/暦"], sy: ["1,09:00"] };
    const output = applySimple(attrs, 2, { ...defaults(), calendar: "/暦" });
    expect(output.sd).toEqual(["2,25"]);
    expect(output.jc).toEqual(attrs.jc);
    expect(output.sy).toEqual(attrs.sy);
  });
  it("日付、番号、猶予、24/48時刻の境界を検証する", () => {
    expect(simpleErrors({ ...defaults(), time: "47:59" }, 48)).toEqual([]);
    expect(simpleErrors({ ...defaults(), time: "24:00" }, 24)).not.toEqual([]);
    expect(simpleErrors({ ...defaults(), count: "0" }, 48)).not.toEqual([]);
    expect(simpleErrors({ ...defaults(), grace: "32" }, 48)).not.toEqual([]);
    expect(simpleErrors({ ...defaults("nth"), index: "35" }, 48)).toEqual([]);
    expect(simpleErrors({ ...defaults("nth"), index: "36" }, 48)).not.toEqual(
      [],
    );
    expect(
      simpleErrors({ ...defaults("once"), date: "2027-02-29" }, 48),
    ).not.toEqual([]);
    expect(
      simpleErrors({ ...defaults("once"), date: "2028-02-29" }, 48),
    ).toEqual([]);
    expect(simpleErrors({ ...defaults(), month: "2037-01" }, 48)).not.toEqual(
      [],
    );
  });
});
