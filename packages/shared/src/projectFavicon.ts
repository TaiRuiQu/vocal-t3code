import { WORKSPACE_IMAGE_PREVIEW_EXTENSIONS } from "./mediaPreview.ts";

export const PROJECT_FAVICON_FALLBACK_MARKER = "project-favicon-missing";

const PROJECT_FAVICON_EXTENSIONS = [...WORKSPACE_IMAGE_PREVIEW_EXTENSIONS, ".icns"] as const;

export function isProjectFaviconPath(path: string): boolean {
  const literalPath = path.toLowerCase();
  return PROJECT_FAVICON_EXTENSIONS.some((extension) => literalPath.endsWith(extension));
}
