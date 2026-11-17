// Helpers shared by quick rooms (index.ts) and persistent rooms (persistent.ts).

/** No 0/O, 1/I/L: codes are read aloud and typed by hand. */
export const CODE_ALPHABET = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
export const CODE_LENGTH = 8;
/** Longest username, in characters. */
export const MAX_NAME_LENGTH = 24;

/**
 * A name others will see: whitespace (tabs and line breaks included) made single spaces, other control and
 * formatting characters removed, at most MAX_NAME_LENGTH characters. Empty becomes "Guest". CrystalCast cleans
 * names the same way (RoomUsername.Clean), so what someone types is what others see.
 */
export function cleanName(raw: string | null | undefined): string {
  const spaced = (raw ?? "").replace(/\s+/g, " ").replace(/\p{C}/gu, "").replace(/ {2,}/g, " ");
  const cleaned = [...spaced.trim()]
    .slice(0, MAX_NAME_LENGTH)
    .join("")
    .trim();
  return cleaned.length > 0 ? cleaned : "Guest";
}

export function normalizeCode(raw: string): string | null {
  const code = raw.toUpperCase().replace(/[^0-9A-Z]/g, "");
  if (code.length !== CODE_LENGTH) {
    return null;
  }

  for (const character of code) {
    if (!CODE_ALPHABET.includes(character)) {
      return null;
    }
  }

  return code;
}

export function randomCode(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(CODE_LENGTH));
  let code = "";
  for (const byte of bytes) {
    code += CODE_ALPHABET[byte % CODE_ALPHABET.length];
  }

  return code;
}

export function randomHex(byteCount: number): string {
  return [...crypto.getRandomValues(new Uint8Array(byteCount))]
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
}

export async function sha256(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}
