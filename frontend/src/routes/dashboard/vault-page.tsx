import { useEffect, useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { useTranslation } from "react-i18next";
import {
  Clipboard,
  Eye,
  EyeOff,
  KeyRound,
  Lock,
  Plus,
  ShieldCheck,
  Trash2,
  Unlock,
} from "lucide-react";
import { SectionHeader } from "@/components/glass/section-header";
import {
  GlassCard,
  GlassCardContent,
  GlassCardDescription,
  GlassCardHeader,
  GlassCardTitle,
} from "@/components/glass/glass-card";
import { EmptyState } from "@/components/glass/empty-state";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
} from "@/components/ui/form";
import {
  useCreateVaultItem,
  useDeleteVaultItem,
  useOverview,
  useVaultItem,
  useVaultItems,
} from "@/hooks/queries";
import { errorMessage, formatDateTime } from "@/lib/format";
import {
  decryptVault,
  hasRecoverableVaultKey,
  type DecryptedVaultPayload,
} from "@/lib/vault";
import { cn } from "@/lib/utils";
import type { VaultItemSummary } from "@/types";

type VaultValues = {
  name: string;
  username: string;
  password: string;
  notes?: string;
};

const defaults: VaultValues = {
  name: "Router login",
  username: "admin",
  password: "",
  notes: "LAN only",
};

export function VaultPage() {
  const overview = useOverview();
  const vaultItems = useVaultItems();
  const createVaultItem = useCreateVaultItem();
  const deleteVaultItem = useDeleteVaultItem();
  const { t } = useTranslation();

  const [vaultSecret, setVaultSecret] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [decryptingId, setDecryptingId] = useState<string | null>(null);
  const [decryptedItems, setDecryptedItems] = useState<
    Record<string, DecryptedVaultPayload>
  >({});
  const [visiblePasswords, setVisiblePasswords] = useState<
    Record<string, boolean>
  >({});
  const schema = useMemo(
    () =>
      z.object({
        name: z.string().trim().min(1, t("validation.nameRequired")),
        username: z.string().trim().min(1, t("validation.usernameRequired")),
        password: z.string().min(1, t("validation.passwordRequired")),
        notes: z.string().optional(),
      }),
    [t],
  );

  const form = useForm<VaultValues>({
    resolver: zodResolver(schema),
    defaultValues: defaults,
  });

  const items = vaultItems.data ?? [];
  const selectedItem = useMemo(
    () => items.find((item) => item.id === selectedId) ?? null,
    [items, selectedId],
  );
  const selectedVaultItem = useVaultItem(
    selectedId,
    vaultSecret.length > 0 && Boolean(selectedId),
  );
  const recoverableCount = items.filter((item) =>
    hasRecoverableVaultKey(item.keyHint),
  ).length;
  const vault = overview.data?.modules.vault;

  useEffect(() => {
    if (items.length === 0) {
      setSelectedId(null);
      return;
    }

    if (!selectedId || !items.some((item) => item.id === selectedId)) {
      setSelectedId(items[0].id);
    }
  }, [items, selectedId]);

  function onSubmit(values: VaultValues) {
    if (!vaultSecret) {
      toast.error(t("pages.vault.secretRequired"));
      return;
    }

    createVaultItem.mutate(
      {
        ...values,
        vaultSecret,
      },
      {
        onSuccess: () => {
          toast.success(t("toast.vaultSaved"));
          form.reset(defaults);
        },
        onError: (error) => toast.error(errorMessage(error)),
      },
    );
  }

  async function decryptSelectedItem() {
    if (!selectedItem || !vaultSecret) {
      toast.error(t("pages.vault.selectAndUnlock"));
      return;
    }

    setDecryptingId(selectedItem.id);
    try {
      const response =
        selectedVaultItem.data ?? (await selectedVaultItem.refetch()).data;
      if (!response) throw new Error(t("pages.vault.readItemFailed"));

      const decrypted = await decryptVault(response, vaultSecret);
      setDecryptedItems((current) => ({
        ...current,
        [response.id]: decrypted,
      }));
      toast.success(t("toast.vaultDecrypted"));
    } catch (error) {
      toast.error(errorMessage(error));
    } finally {
      setDecryptingId(null);
    }
  }

  function lockVault() {
    setVaultSecret("");
    setDecryptedItems({});
    setVisiblePasswords({});
    toast.message(t("toast.vaultLocked"));
  }

  function changeVaultSecret(value: string) {
    if (value !== vaultSecret) {
      setDecryptedItems({});
      setVisiblePasswords({});
    }

    setVaultSecret(value);
  }

  function removeItem(item: VaultItemSummary) {
    if (!window.confirm(t("pages.vault.confirmDelete", { name: item.name })))
      return;

    deleteVaultItem.mutate(item.id, {
      onSuccess: () => {
        setDecryptedItems(({ [item.id]: _removed, ...current }) => current);
        setVisiblePasswords(({ [item.id]: _removed, ...current }) => current);
        if (selectedId === item.id) setSelectedId(null);
        toast.success(t("toast.vaultDeleted"));
      },
      onError: (error) => toast.error(errorMessage(error)),
    });
  }

  async function copyValue(value: string, label: string) {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(t("toast.copied", { label }));
    } catch {
      toast.error(t("toast.copyFailed"));
    }
  }

  return (
    <div className="space-y-6">
      <SectionHeader
        eyebrow={t("pages.vault.eyebrow")}
        title={t("pages.vault.title")}
        description={t("pages.vault.description")}
      />

      <div className="grid gap-3 xl:grid-cols-[minmax(0,400px)_1fr]">
        <div className="space-y-3">
          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>{t("pages.vault.keyTitle")}</GlassCardTitle>
              <GlassCardDescription>
                {t("pages.vault.keyDescription")}
              </GlassCardDescription>
            </GlassCardHeader>
            <GlassCardContent>
              <form
                className="space-y-3"
                onSubmit={(event) => {
                  event.preventDefault();
                  if (!vaultSecret) {
                    toast.error(t("pages.vault.secretRequired"));
                    return;
                  }

                  toast.success(t("toast.vaultUnlocked"));
                }}
              >
                <div className="space-y-2">
                  <label
                    htmlFor="vault-secret"
                    className="text-sm font-medium"
                  >
                    {t("pages.vault.keyLabel")}
                  </label>
                  <Input
                    id="vault-secret"
                    type="password"
                    autoComplete="new-password"
                    value={vaultSecret}
                    onChange={(event) => changeVaultSecret(event.target.value)}
                    placeholder={t("pages.vault.keyPlaceholder")}
                  />
                </div>
                <div className="flex flex-wrap gap-2">
                  <Button type="submit" disabled={!vaultSecret}>
                    <Unlock className="size-4" />
                    {t("pages.vault.unlock")}
                  </Button>
                  <Button
                    type="button"
                    variant="outline"
                    onClick={lockVault}
                    disabled={!vaultSecret}
                  >
                    <Lock className="size-4" />
                    {t("pages.vault.lock")}
                  </Button>
                </div>
              </form>
            </GlassCardContent>
          </GlassCard>

          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>{t("pages.vault.addTitle")}</GlassCardTitle>
              <GlassCardDescription>
                {t("pages.vault.addDescription")}
              </GlassCardDescription>
            </GlassCardHeader>
            <GlassCardContent>
              <Form {...form}>
                <form
                  className="space-y-4"
                  onSubmit={form.handleSubmit(onSubmit)}
                >
                  <FormField
                    control={form.control}
                    name="name"
                      render={({ field }) => (
                        <FormItem>
                        <FormLabel>{t("fields.name")}</FormLabel>
                        <FormControl>
                          <Input placeholder="Router login" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="username"
                      render={({ field }) => (
                        <FormItem>
                        <FormLabel>{t("fields.username")}</FormLabel>
                        <FormControl>
                          <Input placeholder="admin" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="password"
                      render={({ field }) => (
                        <FormItem>
                        <FormLabel>{t("fields.password")}</FormLabel>
                        <FormControl>
                          <Input
                            type="password"
                            autoComplete="new-password"
                            {...field}
                          />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="notes"
                      render={({ field }) => (
                        <FormItem>
                        <FormLabel>{t("fields.notes")}</FormLabel>
                        <FormControl>
                          <Textarea placeholder="LAN only" {...field} />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <Button
                    type="submit"
                    className="w-full"
                    disabled={createVaultItem.isPending || !vaultSecret}
                  >
                    <Lock className="size-4" />
                    {createVaultItem.isPending
                      ? t("pages.vault.savePending")
                      : t("pages.vault.save")}
                  </Button>
                </form>
              </Form>
            </GlassCardContent>
          </GlassCard>
        </div>

        <div className="space-y-3">
          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>{t("pages.vault.statusTitle")}</GlassCardTitle>
            </GlassCardHeader>
            <GlassCardContent className="space-y-5">
              {overview.isPending ? (
                <Skeleton className="h-24 rounded-xl" />
              ) : (
                <>
                  <div className="flex items-end justify-between gap-3 rounded-xl border border-border/60 bg-background/30 px-4 py-4">
                    <div className="flex items-baseline gap-2">
                      <span className="text-4xl font-semibold tabular-nums tracking-tight">
                        {vault?.count ?? 0}
                      </span>
                      <span className="text-sm text-muted-foreground">
                        {t("pages.vault.entries")}
                      </span>
                    </div>
                    <span className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-primary/10 text-primary">
                      <KeyRound className="size-5" />
                    </span>
                  </div>

                  <div className="flex flex-wrap items-center gap-2">
                    <Badge
                      variant="outline"
                      className={cn(
                        "font-medium",
                        vault?.encrypted
                          ? "border-success/40 text-success"
                          : "border-warning/40 text-warning",
                      )}
                    >
                      <ShieldCheck className="size-3.5" />
                      {vault?.encrypted
                        ? t("pages.vault.encrypted")
                        : t("pages.vault.pendingEncryption")}
                    </Badge>
                    <Badge variant="secondary">
                      {t("pages.vault.recoverable", {
                        recoverable: recoverableCount,
                        total: items.length,
                      })}
                    </Badge>
                    <Badge variant={vaultSecret ? "default" : "outline"}>
                      {vaultSecret
                        ? t("pages.vault.unlocked")
                        : t("pages.vault.locked")}
                    </Badge>
                  </div>
                </>
              )}
            </GlassCardContent>
          </GlassCard>

          <GlassCard>
            <GlassCardHeader>
              <GlassCardTitle>{t("pages.vault.entriesTitle")}</GlassCardTitle>
              <GlassCardDescription>
                {t("pages.vault.entriesDescription")}
              </GlassCardDescription>
            </GlassCardHeader>
            <GlassCardContent>
              <div className="grid gap-3 lg:grid-cols-[minmax(0,320px)_1fr]">
                <div className="min-h-72">
                  {vaultItems.isPending ? (
                    <div className="space-y-2">
                      {Array.from({ length: 4 }).map((_, index) => (
                        <Skeleton key={index} className="h-16 rounded-xl" />
                      ))}
                    </div>
                  ) : vaultItems.isError ? (
                    <EmptyState
                      icon={Lock}
                      title={t("pages.vault.readErrorTitle")}
                      description={errorMessage(vaultItems.error)}
                    />
                  ) : items.length > 0 ? (
                    <ul className="space-y-2">
                      {items.map((item) => (
                        <li key={item.id}>
                          <div
                            className={cn(
                              "group flex items-center gap-2 rounded-xl border px-3 py-2.5 transition-colors",
                              selectedId === item.id
                                ? "border-primary/50 bg-primary/10"
                                : "border-border/60 bg-background/30 hover:bg-background/50",
                            )}
                          >
                            <button
                              type="button"
                              onClick={() => setSelectedId(item.id)}
                              className="min-w-0 flex-1 text-left"
                            >
                              <span className="block truncate text-sm font-medium">
                                {item.name}
                              </span>
                              <span className="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
                                <span className="truncate">
                                  {formatDateTime(item.updatedAt)}
                                </span>
                                <Badge
                                  variant="outline"
                                  className="shrink-0 px-1.5 py-0 text-[10px]"
                                >
                                  {hasRecoverableVaultKey(item.keyHint)
                                    ? t("pages.vault.recoverableBadge")
                                    : t("pages.vault.legacyBadge")}
                                </Badge>
                              </span>
                            </button>
                            <Button
                              type="button"
                              variant="ghost"
                              size="icon-sm"
                              aria-label={t("pages.vault.deleteAria")}
                              onClick={() => removeItem(item)}
                              disabled={deleteVaultItem.isPending}
                            >
                              <Trash2 className="size-4" />
                            </Button>
                          </div>
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <EmptyState
                      icon={Plus}
                      title={t("pages.vault.emptyTitle")}
                      description={t("pages.vault.emptyDescription")}
                    />
                  )}
                </div>

                <VaultItemDetail
                  item={selectedItem}
                  decrypted={selectedId ? decryptedItems[selectedId] : null}
                  isUnlocked={vaultSecret.length > 0}
                  isDecrypting={decryptingId === selectedId}
                  isLoading={selectedVaultItem.isFetching}
                  passwordVisible={
                    selectedId ? Boolean(visiblePasswords[selectedId]) : false
                  }
                  onDecrypt={decryptSelectedItem}
                  onCopy={copyValue}
                  onTogglePassword={() => {
                    if (!selectedId) return;
                    setVisiblePasswords((current) => ({
                      ...current,
                      [selectedId]: !current[selectedId],
                    }));
                  }}
                />
              </div>
            </GlassCardContent>
          </GlassCard>
        </div>
      </div>
    </div>
  );
}

type VaultItemDetailProps = {
  item: VaultItemSummary | null;
  decrypted: DecryptedVaultPayload | null;
  isUnlocked: boolean;
  isDecrypting: boolean;
  isLoading: boolean;
  passwordVisible: boolean;
  onDecrypt: () => void;
  onCopy: (value: string, label: string) => void;
  onTogglePassword: () => void;
};

function VaultItemDetail({
  item,
  decrypted,
  isUnlocked,
  isDecrypting,
  isLoading,
  passwordVisible,
  onDecrypt,
  onCopy,
  onTogglePassword,
}: VaultItemDetailProps) {
  const { t } = useTranslation();

  if (!item) {
    return (
      <div className="min-h-72 rounded-xl border border-border/60 bg-background/30">
        <EmptyState
          icon={KeyRound}
          title={t("pages.vault.selectTitle")}
          description={t("pages.vault.selectDescription")}
        />
      </div>
    );
  }

  const canRecover = hasRecoverableVaultKey(item.keyHint);

  return (
    <div className="min-h-72 rounded-xl border border-border/60 bg-background/30 p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate text-sm font-semibold">{item.name}</h3>
          <p className="text-xs text-muted-foreground">
            {t("pages.vault.updatedAt", {
              date: formatDateTime(item.updatedAt),
            })}
          </p>
        </div>
        <Badge variant={canRecover ? "secondary" : "outline"}>
          {canRecover
            ? t("pages.vault.recoverableKey")
            : t("pages.vault.legacyKey")}
        </Badge>
      </div>

      {decrypted ? (
        <div className="mt-5 space-y-4">
          <VaultValueRow
            label={t("fields.username")}
            value={decrypted.username}
            onCopy={() => onCopy(decrypted.username, t("fields.username"))}
          />
          <div className="space-y-2">
            <label className="text-sm font-medium">{t("fields.password")}</label>
            <div className="flex gap-2">
              <Input
                readOnly
                type={passwordVisible ? "text" : "password"}
                value={decrypted.password}
                className="font-mono"
              />
              <Button
                type="button"
                variant="outline"
                size="icon"
                aria-label={
                  passwordVisible
                    ? t("pages.vault.hidePassword")
                    : t("pages.vault.showPassword")
                }
                onClick={onTogglePassword}
              >
                {passwordVisible ? (
                  <EyeOff className="size-4" />
                ) : (
                  <Eye className="size-4" />
                )}
              </Button>
              <Button
                type="button"
                variant="outline"
                size="icon"
                aria-label={t("pages.vault.copyPassword")}
                onClick={() => onCopy(decrypted.password, t("fields.password"))}
              >
                <Clipboard className="size-4" />
              </Button>
            </div>
          </div>
          <div className="space-y-2">
            <label className="text-sm font-medium">{t("fields.notes")}</label>
            <Textarea
              readOnly
              value={decrypted.notes}
              className="min-h-24 resize-none"
            />
          </div>
          <p className="text-xs text-muted-foreground">
            {t("pages.vault.plaintextUpdatedAt", {
              date: formatDateTime(decrypted.updatedAt),
            })}
          </p>
        </div>
      ) : (
        <div className="mt-8 flex flex-col items-center gap-3 text-center">
          <span className="flex size-12 items-center justify-center rounded-xl bg-primary/10 text-primary">
            <Lock className="size-5" />
          </span>
          <div className="space-y-1">
            <p className="text-sm font-medium">
              {isUnlocked
                ? t("pages.vault.stillEncrypted")
                : t("pages.vault.lockedBody")}
            </p>
            <p className="max-w-md text-sm text-muted-foreground">
              {canRecover
                ? t("pages.vault.recoverableDescription")
                : t("pages.vault.legacyDescription")}
            </p>
          </div>
          <Button
            type="button"
            onClick={onDecrypt}
            disabled={!isUnlocked || !canRecover || isDecrypting || isLoading}
          >
            <Unlock className="size-4" />
            {isDecrypting || isLoading
              ? t("pages.vault.decryptPending")
              : t("pages.vault.decrypt")}
          </Button>
        </div>
      )}
    </div>
  );
}

type VaultValueRowProps = {
  label: string;
  value: string;
  onCopy: () => void;
};

function VaultValueRow({ label, value, onCopy }: VaultValueRowProps) {
  const { t } = useTranslation();

  return (
    <div className="space-y-2">
      <label className="text-sm font-medium">{label}</label>
      <div className="flex gap-2">
        <Input readOnly value={value} className="font-mono" />
        <Button
          type="button"
          variant="outline"
          size="icon"
          aria-label={t("pages.vault.copyAria", { label })}
          onClick={onCopy}
        >
          <Clipboard className="size-4" />
        </Button>
      </div>
    </div>
  );
}
