import { LogOut } from "lucide-react";
import { useNavigate } from "react-router";
import { useTranslation } from "react-i18next";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { useAuth } from "@/hooks/use-auth";
import { useLogout } from "@/hooks/queries";

function initialsOf(name: string): string {
  return name.trim().slice(0, 2).toUpperCase() || "HH";
}

export function UserMenu() {
  const auth = useAuth();
  const navigate = useNavigate();
  const logout = useLogout();
  const { t } = useTranslation();

  if (!auth) return null;
  const roleLabel = t(`roles.${auth.member.role}`, {
    defaultValue: auth.member.role,
  });

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" className="h-9 gap-2 px-1.5">
          <Avatar className="size-7">
            <AvatarFallback className="bg-gradient-to-br from-chart-1 to-chart-2 text-xs text-white">
              {initialsOf(auth.member.displayName)}
            </AvatarFallback>
          </Avatar>
          <span className="hidden flex-col items-start leading-tight sm:flex">
            <span className="text-sm font-medium">
              {auth.member.displayName}
            </span>
            <span className="text-xs text-muted-foreground">
              {roleLabel}
            </span>
          </span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-52">
        <DropdownMenuLabel className="flex flex-col gap-0.5">
          <span>{auth.family.name}</span>
          <span className="text-xs font-normal text-muted-foreground">
            {auth.member.displayName}
            {t("common.separator")}
            {roleLabel}
          </span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem
          variant="destructive"
          onClick={() =>
            logout.mutate(undefined, { onSettled: () => navigate("/login") })
          }
        >
          <LogOut className="size-4" /> {t("common.logout")}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
