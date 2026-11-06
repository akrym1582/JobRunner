import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwind from "@tailwindcss/vite";
export default defineConfig({
  plugins: [react(), tailwind()],
  server: {
    proxy: {
      "/api": process.env.JOBRUNNER_API_URL || "http://localhost:5080",
      "/swagger": process.env.JOBRUNNER_API_URL || "http://localhost:5080",
    },
  },
});
