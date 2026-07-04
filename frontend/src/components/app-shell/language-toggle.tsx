import { Languages } from "lucide-react";
import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  changeLanguage,
  currentLanguage,
  languageOptions,
  type LanguageCode,
} from "@/i18n";

export function LanguageToggle() {
  const { t } = useTranslation();
  const activeLanguage = currentLanguage();

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          aria-label={t("common.switchLanguage")}
        >
          <Languages className="size-4" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-44">
        <DropdownMenuRadioGroup
          value={activeLanguage}
          onValueChange={(value) => {
            void changeLanguage(value as LanguageCode);
          }}
        >
          {languageOptions.map((option) => (
            <DropdownMenuRadioItem key={option.code} value={option.code}>
              {t(option.labelKey)}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
