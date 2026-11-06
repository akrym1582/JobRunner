import type { LogChunk } from "./api/@types";
export const MAX_LOG_CHUNKS = 64;
export function mergeChunks(
  current: LogChunk[],
  incoming: LogChunk[],
  mode: "replace" | "append" | "prepend",
) {
  const bySequence = new Map(
    (mode === "replace" ? [] : current).map((chunk) => [chunk.sequence, chunk]),
  );
  for (const chunk of incoming) bySequence.set(chunk.sequence, chunk);
  const sorted = [...bySequence.values()].sort(
    (a, b) => a.sequence - b.sequence,
  );
  return mode === "prepend"
    ? sorted.slice(0, MAX_LOG_CHUNKS)
    : sorted.slice(-MAX_LOG_CHUNKS);
}
export const isLiveAttempt = (state?: string) =>
  state === "Running" || state === "Starting";
