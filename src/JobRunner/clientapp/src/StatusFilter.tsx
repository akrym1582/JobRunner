import { executionStatuses } from "./filter-model";
import { label } from "./status";
export function StatusFilter({
  selected,
  onChange,
  disabled = false,
}: {
  selected: string[];
  onChange: (value: string[]) => void;
  disabled?: boolean;
}) {
  return (
    <fieldset className="status-filter" disabled={disabled}>
      <legend>
        状態で絞り込み {disabled && <small>（実行回を選択してください）</small>}
      </legend>
      <button aria-pressed={!selected.length} onClick={() => onChange([])}>
        すべて
      </button>
      <details>
        <summary>
          {selected.length ? selected.map(label).join("・") : "状態を選択"}
        </summary>
        <div className="status-options">
          {executionStatuses.map((status) => (
            <label key={status}>
              <input
                type="checkbox"
                checked={selected.includes(status)}
                onChange={(event) =>
                  onChange(
                    event.target.checked
                      ? [...selected, status]
                      : selected.filter((s) => s !== status),
                  )
                }
              />
              {label(status)}
            </label>
          ))}
        </div>
      </details>
      {!!selected.length && !disabled && (
        <span>{selected.length}種類を表示</span>
      )}
    </fieldset>
  );
}
