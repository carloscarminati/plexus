// Outward-facing app metadata in one place. The version is injected from
// package.json at build time (vite.config.ts → __APP_VERSION__), so it stays in
// sync with the Tauri bundle version. Update REPO_URL once if the slug changes.
export const REPO_URL = "https://github.com/carloscarminati/plexus";
export const APP_VERSION: string = __APP_VERSION__;
