/**
 * Images a script returned with ImageFile(path). The plugin sends them next
 * to the JSON result; the result itself holds a numbered reference to each
 * ({"image": 1, "mimeType": "image/png", ...}).
 */
export interface ResultImage {
  mimeType: string;
  data: string;
}

const IMAGE_TYPES = new Set(["image/png", "image/jpeg"]);
const BASE64 = /^[A-Za-z0-9+/]+={0,2}$/;

/** Reads the plugin's image list; anything that is not a well-formed image is left out. */
export function readResultImages(response: Record<string, unknown>): ResultImage[] {
  const images = response.images;
  if (!Array.isArray(images)) return [];
  const result: ResultImage[] = [];
  for (const image of images) {
    if (typeof image !== "object" || image === null) continue;
    const { mimeType, data } = image as Record<string, unknown>;
    if (
      typeof mimeType === "string" &&
      IMAGE_TYPES.has(mimeType) &&
      typeof data === "string" &&
      data.length > 0 &&
      BASE64.test(data)
    ) {
      result.push({ mimeType, data });
    }
  }
  return result;
}

/** MCP image blocks in the order of the references in the result. */
export function imageContentBlocks(
  images: readonly ResultImage[]
): Array<{ type: "image"; data: string; mimeType: string }> {
  return images.map((image) => ({
    type: "image" as const,
    data: image.data,
    mimeType: image.mimeType,
  }));
}
