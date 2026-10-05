import path from "path";
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";

export default defineConfig({
  plugins: [react(), tailwindcss(), tanstackRouter({
    routesDirectory: "./src/routes",
  })],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
});