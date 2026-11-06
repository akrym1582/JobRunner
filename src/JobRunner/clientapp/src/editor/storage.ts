import type { EditorDocument, EditorDraft } from "../api/@types";
export type SavedDraft = {
  version: 1;
  base: EditorDocument;
  draft: EditorDraft;
};
let connection: Promise<IDBDatabase> | undefined;
function database() {
  return (connection ??= new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open("jobrunner-definition-editor", 1);
    request.onupgradeneeded = () => request.result.createObjectStore("drafts");
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
      const saved: SavedDraft | undefined = request.result;
      if (
        saved &&
        (saved.version !== 1 ||
          saved.base?.draft?.definitionHash !== saved.draft?.definitionHash ||
          !Array.isArray(saved.draft?.networks))
      )
        reject(new Error("保存した下書きの形式が不正です。"));
      else resolve(saved);
    };
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}
export async function writeDraft(value: SavedDraft) {
  const db = await database();
  return new Promise<void>((resolve, reject) => {
    const transaction = db.transaction("drafts", "readwrite");
    transaction.objectStore("drafts").put(value, "current");
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}
