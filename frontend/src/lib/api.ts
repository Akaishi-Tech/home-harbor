import { i18n } from "@/i18n";
import type { AuthState } from "@/types";

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? "";

type ApiOptions = {
  auth?: AuthState | null;
  method?: string;
  body?: unknown;
  headers?: HeadersInit;
  signal?: AbortSignal;
};

let unauthorizedHandler: (() => void) | null = null;
let tokenProvider: (() => string | null) | null = null;

/** Thrown for any non-2xx response, carrying the HTTP status for retry logic. */
export class ApiError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

export function endpoint(path: string): string {
  return `${API_BASE}${path}`;
}

export function configureUnauthorizedHandler(
  handler: (() => void) | null,
): void {
  unauthorizedHandler = handler;
}

/** Lets the auth provider supply the current bearer token to every request. */
export function configureAuthTokenProvider(
  provider: (() => string | null) | null,
): void {
  tokenProvider = provider;
}

export async function api<T>(
  path: string,
  options: ApiOptions = {},
): Promise<T> {
  const headers = new Headers(options.headers);
  headers.set("accept", "application/json");

  const token = options.auth?.accessToken ?? tokenProvider?.() ?? null;
  if (token) {
    headers.set("authorization", `Bearer ${token}`);
  }

  const init: RequestInit = {
    method: options.method ?? (options.body === undefined ? "GET" : "POST"),
    headers,
    signal: options.signal,
  };

  if (options.body !== undefined) {
    headers.set("content-type", "application/json");
    init.body = JSON.stringify(options.body);
  }

  const response = await fetch(endpoint(path), init);
  const text = await response.text();
  const body = text ? parseJson(text) : null;

  if (response.status === 401) {
    unauthorizedHandler?.();
    throw new ApiError(i18n.t("api.expired"), 401);
  }

  if (!response.ok) {
    throw new ApiError(readError(body, response.statusText), response.status);
  }

  return body as T;
}

export function post<T>(
  path: string,
  body: unknown,
  auth?: AuthState | null,
): Promise<T> {
  return api<T>(path, { method: "POST", body, auth });
}

function parseJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function readError(body: unknown, fallback: string): string {
  if (typeof body === "string") {
    return body.trim() || fallback;
  }

  if (!isRecord(body)) return fallback;

  const directMessage =
    readErrorValue(body.error) ??
    readErrorValue(body.message) ??
    readErrorValue(body.detail);
  if (directMessage) return directMessage;

  const validationErrors = readErrorValue(body.errors);
  const title = readErrorValue(body.title);
  if (title && validationErrors) return `${title}: ${validationErrors}`;
  return title ?? validationErrors ?? fallback;
}

function readErrorValue(value: unknown): string | null {
  if (typeof value === "string") {
    return value.trim() || null;
  }

  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }

  if (Array.isArray(value)) {
    const items = value.map(readErrorValue).filter(isPresent);
    return items.length ? items.join("; ") : null;
  }

  if (isRecord(value)) {
    const items = Object.entries(value).flatMap(([key, item]) => {
      const message = readErrorValue(item);
      return message ? [`${key}: ${message}`] : [];
    });
    return items.length ? items.join("; ") : null;
  }

  return null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function isPresent<T>(value: T | null | undefined): value is T {
  return value !== null && value !== undefined;
}
