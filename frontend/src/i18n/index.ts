import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { resources, type LanguageCode } from "./resources";

const storageKey = "homeharbor.language";

export const languageOptions: Array<{
  code: LanguageCode;
  labelKey: string;
}> = [
  { code: "zh-CN", labelKey: "languages.zhCN" },
  { code: "en-US", labelKey: "languages.enUS" },
];

const supportedLanguages = languageOptions.map((option) => option.code);

function normalizeLanguage(value: string | null | undefined): LanguageCode {
  if (!value) return "zh-CN";
  const normalized = value.toLowerCase();
  if (normalized === "zh" || normalized.startsWith("zh-")) return "zh-CN";
  if (normalized === "en" || normalized.startsWith("en-")) return "en-US";
  return "zh-CN";
}

function readStoredLanguage(): LanguageCode | null {
  try {
    const stored = window.localStorage.getItem(storageKey);
    return stored ? normalizeLanguage(stored) : null;
  } catch {
    return null;
  }
}

function initialLanguage(): LanguageCode {
  const stored = readStoredLanguage();
  if (stored) return stored;

  for (const language of navigator.languages ?? []) {
    const normalized = normalizeLanguage(language);
    if (supportedLanguages.includes(normalized)) return normalized;
  }

  return normalizeLanguage(navigator.language);
}

function syncDocumentLanguage(language: string): void {
  document.documentElement.lang = normalizeLanguage(language);
}

void i18n.use(initReactI18next).init({
  resources,
  lng: initialLanguage(),
  fallbackLng: "zh-CN",
  supportedLngs: supportedLanguages,
  interpolation: {
    escapeValue: false,
  },
  react: {
    useSuspense: false,
  },
  returnNull: false,
});

syncDocumentLanguage(i18n.language);

i18n.on("languageChanged", (language) => {
  const normalized = normalizeLanguage(language);
  syncDocumentLanguage(normalized);
  try {
    window.localStorage.setItem(storageKey, normalized);
  } catch {
    // Persisting the preference is best-effort when storage is unavailable.
  }
});

export { i18n };
export type { LanguageCode };

export function changeLanguage(language: LanguageCode): Promise<unknown> {
  return i18n.changeLanguage(language);
}

export function currentLanguage(): LanguageCode {
  return normalizeLanguage(i18n.resolvedLanguage ?? i18n.language);
}
