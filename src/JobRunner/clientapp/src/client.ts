import aspida from "@aspida/fetch";
import api from "./api/$api";
export const client = api(aspida(fetch, { baseURL: "", throwHttpErrors: true }))
  .api.jobrunner;
export async function errorMessage(error: unknown): Promise<string> {
  if (
    error instanceof Error &&
    "response" in error &&
    error.response instanceof Response
  ) {
    try {
      const data = await error.response.clone().json();
      return data.detail || data.error || error.message;
    } catch {
      return error.message;
    }
  }
  return error instanceof Error
    ? error.message
    : "通信に失敗しました。再取得してください。";
}
