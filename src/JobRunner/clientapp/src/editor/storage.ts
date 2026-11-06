import type { EditorDocument, EditorDraft } from "../api/@types";
export type SavedDraft = {
  version: 1;
  revision?: string;
  base: EditorDocument;
  draft: EditorDraft;
};
export class DraftConflictError extends Error {
  constructor() {
    super(
      "別のタブで下書きが更新されました。このタブの編集は保存していません。必要な編集をダウンロードしてから、保存済み下書きを読み直してください。",
    );
  }
}
function checkSaved(saved: SavedDraft | undefined) {
  if (
    saved &&
    (saved.version !== 1 ||
      saved.base?.draft?.definitionHash !== saved.draft?.definitionHash ||
      !Array.isArray(saved.draft?.networks) ||
      (saved.revision !== undefined && typeof saved.revision !== "string"))
  )
    throw new Error("保存した下書きの形式が不正です。");
  return saved;
}
let connection: Promise<IDBDatabase> | undefined;
function database() {
  return (connection ??= new Promise<IDBDatabase>((resolve, reject) => {
    // 無条件保存する旧版タブの接続を閉じ、古いプロトコルでの再接続も防ぎます。
    const request = indexedDB.open("jobrunner-definition-editor", 2);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains("drafts"))
        request.result.createObjectStore("drafts");
    };
    request.onsuccess = () => {
      request.result.onversionchange = () => {
        request.result.close();
        connection = undefined;
      };
      resolve(request.result);
    };
    request.onerror = () => {
      connection = undefined;
      reject(request.error);
    };
    request.onblocked = () => {
      connection = undefined;
      reject(new Error("別のタブが下書き保存領域を使用しています。"));
    };
  }));
}
export async function readDraft(): Promise<SavedDraft | undefined> {
  const db = await database();
  return new Promise((resolve, reject) => {
    const transaction = db.transaction("drafts", "readonly");
    const request = transaction.objectStore("drafts").get("current");
    transaction.oncomplete = () => {
      try {
        resolve(checkSaved(request.result));
      } catch (error) {
        reject(error);
      }
    };
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}
export async function writeDraft(
  value: SavedDraft,
  expectedRevision: string | null,
) {
  const db = await database();
  return new Promise<string>((resolve, reject) => {
    const transaction = db.transaction("drafts", "readwrite");
    const store = transaction.objectStore("drafts");
    const request = store.get("current");
    const revision = crypto.randomUUID();
    let failure: unknown;
    // 比較と更新を同じトランザクションで行い、別タブの保存が間に入ることを防ぎます。
    request.onsuccess = () => {
      try {
        const saved = checkSaved(request.result);
        if ((saved?.revision ?? null) !== expectedRevision)
          throw new DraftConflictError();
        store.put({ ...value, revision }, "current");
      } catch (error) {
        failure = error;
        transaction.abort();
      }
    };
    transaction.oncomplete = () => resolve(revision);
    transaction.onerror = () => reject(failure ?? transaction.error);
    transaction.onabort = () => reject(failure ?? transaction.error);
  });
}
