import { useState } from "react";
import { useForm } from "react-hook-form";
import {
    useMutation,
    useQuery,
    useQueryClient,
} from "@tanstack/react-query";
import {
    ChevronLeft,
    ChevronRight,
    Pencil,
    Plus,
    Star,
    Trash2,
} from "lucide-react";
import { cn } from "cn";

import {
    addWatchlistSymbol,
    createWatchlist,
    deleteWatchlist,
    getWatchlistQuotes,
    getWatchlists,
    maxListsPerUser,
    maxSymbolsPerList,
    removeWatchlistSymbol,
    renameWatchlist,
    reorderWatchlists,
    reorderWatchlistSymbols,
    watchlistQuotesQueryKey,
    watchlistsQueryKey,
    type WatchlistSummary,
} from "@/api/watchlists";
import { apiErrorMessages } from "@/api/errors";
import { useLivePrices } from "@/hooks/use-live-prices";

import LiveIndicator from "@/components/LiveIndicator";
import ListNameDialog from "@/components/watchlists/ListNameDialog";
import WatchlistQuoteTable from "@/components/watchlists/WatchlistQuoteTable";
import { Button } from "@/components/ui/button";
import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
} from "@/components/ui/card";
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogHeader,
    DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";

type DialogKind = "create" | "rename" | "delete" | null;

type AddSymbolValues = { symbol: string };

/** Moves the item at index by offset; the same array if it can't move. */
function moved<T>(items: T[], index: number, offset: -1 | 1): T[] {
    const target = index + offset;

    if (index < 0 || target < 0 || target >= items.length) {
        return items;
    }

    const next = [...items];
    [next[index], next[target]] = [next[target], next[index]];
    return next;
}

function Watchlist() {
    const queryClient = useQueryClient();
    const [selectedId, setSelectedId] = useState<string | null>(null);
    const [dialog, setDialog] = useState<DialogKind>(null);

    const listsQuery = useQuery({
        queryKey: watchlistsQueryKey,
        queryFn: getWatchlists,
    });

    const lists = listsQuery.data ?? [];
    // The chosen list, or the first one if none is chosen (or it was deleted).
    const active: WatchlistSummary | undefined =
        lists.find((list) => list.id === selectedId) ?? lists[0];

    const quotesQuery = useQuery({
        queryKey: watchlistQuotesQueryKey(active?.id ?? "none"),
        queryFn: () => getWatchlistQuotes(active!.id),
        enabled: !!active,
        // A newly added symbol has no price until the next poll; check again
        // until it does. Live updates cover symbols that have one.
        refetchInterval: (query) =>
            query.state.data?.some((row) => row.price === null)
                ? 30_000
                : false,
    });

    const rows = quotesQuery.data ?? [];

    const liveState = useLivePrices(active?.symbols ?? []);

    async function refresh() {
        await queryClient.invalidateQueries({ queryKey: watchlistsQueryKey });
    }

    const createMutation = useMutation({
        mutationFn: createWatchlist,
        onSuccess: async (created) => {
            setSelectedId(created.id);
            setDialog(null);
            await refresh();
        },
    });

    const renameMutation = useMutation({
        mutationFn: (name: string) => renameWatchlist(active!.id, name),
        onSuccess: async () => {
            setDialog(null);
            await refresh();
        },
    });

    const deleteMutation = useMutation({
        mutationFn: () => deleteWatchlist(active!.id),
        onSuccess: async () => {
            setSelectedId(null);
            setDialog(null);
            await refresh();
        },
    });

    const reorderListsMutation = useMutation({
        mutationFn: reorderWatchlists,
        onSettled: refresh,
    });

    const addMutation = useMutation({
        mutationFn: (symbol: string) => addWatchlistSymbol(active!.id, symbol),
        onSuccess: refresh,
    });

    const removeMutation = useMutation({
        mutationFn: (symbol: string) => removeWatchlistSymbol(active!.id, symbol),
        onSuccess: refresh,
    });

    const reorderSymbolsMutation = useMutation({
        mutationFn: (symbols: string[]) =>
            reorderWatchlistSymbols(active!.id, symbols),
        onSettled: refresh,
    });

    const {
        register,
        handleSubmit,
        reset,
    } = useForm<AddSymbolValues>({ defaultValues: { symbol: "" } });

    function handleAdd(data: AddSymbolValues) {
        const symbol = data.symbol.trim().toUpperCase();

        if (!symbol || !active) {
            return;
        }

        addMutation.mutate(symbol, { onSuccess: () => reset() });
    }

    function moveList(offset: -1 | 1) {
        if (!active) {
            return;
        }

        const ids = lists.map((list) => list.id);
        const next = moved(ids, ids.indexOf(active.id), offset);

        if (next !== ids) {
            // Show the new order straight away; the refetch confirms it.
            queryClient.setQueryData<WatchlistSummary[]>(
                watchlistsQueryKey,
                next.map((id) => lists.find((list) => list.id === id)!),
            );
            reorderListsMutation.mutate(next);
        }
    }

    function moveSymbol(symbol: string, offset: -1 | 1) {
        const symbols = rows.map((row) => row.symbol);
        const next = moved(symbols, symbols.indexOf(symbol), offset);

        if (next !== symbols && active) {
            queryClient.setQueryData(
                watchlistQuotesQueryKey(active.id),
                next.map((s) => rows.find((row) => row.symbol === s)!),
            );
            reorderSymbolsMutation.mutate(next);
        }
    }

    const errorSource =
        listsQuery.error ??
        quotesQuery.error ??
        addMutation.error ??
        removeMutation.error ??
        reorderListsMutation.error ??
        reorderSymbolsMutation.error ??
        deleteMutation.error;

    const errors = errorSource
        ? apiErrorMessages(errorSource, "Something went wrong with your watchlists.")
        : [];

    const busy =
        addMutation.isPending ||
        removeMutation.isPending ||
        reorderSymbolsMutation.isPending;

    const activeIndex = active ? lists.indexOf(active) : -1;

    return (
        <div className="mx-auto max-w-7xl space-y-6">
            <div className="flex flex-wrap items-end justify-between gap-4">
                <div>
                    <h1 className="text-3xl font-semibold tracking-tight">
                        Watchlists
                    </h1>

                    <p className="mt-1 text-muted-foreground">
                        Track the stocks you're watching, in as many lists as you like.
                    </p>
                </div>

                <LiveIndicator state={liveState} />
            </div>

            {/* List selector */}
            <div className="flex flex-wrap items-center gap-2">
                {lists.map((list) => (
                    <Button
                        key={list.id}
                        variant={list.id === active?.id ? "secondary" : "ghost"}
                        size="sm"
                        onClick={() => setSelectedId(list.id)}
                        aria-pressed={list.id === active?.id}
                    >
                        {list.name}
                        <span className="text-muted-foreground">
                            {list.symbols.length}
                        </span>
                    </Button>
                ))}

                <Button
                    variant="outline"
                    size="sm"
                    onClick={() => {
                        createMutation.reset();
                        setDialog("create");
                    }}
                    disabled={lists.length >= maxListsPerUser}
                    title={
                        lists.length >= maxListsPerUser
                            ? `At most ${maxListsPerUser} lists`
                            : undefined
                    }
                >
                    <Plus />
                    New list
                </Button>
            </div>

            {errors.length > 0 && (
                <div className="rounded-lg border border-destructive/50 bg-destructive/10 px-4 py-3 text-sm text-destructive">
                    {errors.map((message) => (
                        <p key={message}>{message}</p>
                    ))}
                </div>
            )}

            {listsQuery.isLoading ? (
                <p className="text-sm text-muted-foreground">
                    Loading watchlists...
                </p>
            ) : !active ? (
                <Card>
                    <CardContent className="flex h-48 flex-col items-center justify-center text-center">
                        <Star className="size-8 text-muted-foreground" />
                        <p className="mt-3 font-medium">You have no watchlists yet</p>
                        <p className="mt-1 text-sm text-muted-foreground">
                            Create one to start tracking stocks.
                        </p>
                    </CardContent>
                </Card>
            ) : (
                <Card>
                    <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-4">
                        <CardTitle className="flex items-center gap-2 text-base">
                            <Star className="size-4" />
                            {active.name}
                        </CardTitle>

                        <div className="flex flex-wrap items-center gap-1">
                            <Button
                                variant="ghost"
                                size="icon-sm"
                                disabled={activeIndex <= 0 || reorderListsMutation.isPending}
                                onClick={() => moveList(-1)}
                            >
                                <ChevronLeft />
                                <span className="sr-only">Move list left</span>
                            </Button>
                            <Button
                                variant="ghost"
                                size="icon-sm"
                                disabled={activeIndex >= lists.length - 1 || reorderListsMutation.isPending}
                                onClick={() => moveList(1)}
                            >
                                <ChevronRight />
                                <span className="sr-only">Move list right</span>
                            </Button>
                            <Button
                                variant="ghost"
                                size="sm"
                                onClick={() => {
                                    renameMutation.reset();
                                    setDialog("rename");
                                }}
                            >
                                <Pencil />
                                Rename
                            </Button>
                            <Button
                                variant="ghost"
                                size="sm"
                                onClick={() => {
                                    deleteMutation.reset();
                                    setDialog("delete");
                                }}
                            >
                                <Trash2 />
                                Delete
                            </Button>
                        </div>
                    </CardHeader>

                    <CardContent className="space-y-4 p-0">
                        <form
                            onSubmit={handleSubmit(handleAdd)}
                            className="flex max-w-md gap-2 px-4"
                        >
                            <Input
                                placeholder="Add symbol (e.g. AAPL)"
                                aria-label="Symbol to add"
                                {...register("symbol")}
                                disabled={addMutation.isPending}
                            />

                            <Button
                                type="submit"
                                disabled={
                                    addMutation.isPending ||
                                    active.symbols.length >= maxSymbolsPerList
                                }
                            >
                                <Plus />
                                {addMutation.isPending ? "Adding..." : "Add"}
                            </Button>
                        </form>

                        {quotesQuery.isLoading ? (
                            <div className="flex h-32 items-center justify-center">
                                <p className="text-sm text-muted-foreground">
                                    Loading quotes...
                                </p>
                            </div>
                        ) : rows.length === 0 ? (
                            <div className="flex h-40 flex-col items-center justify-center px-6 text-center">
                                <p className="font-medium">This list is empty</p>
                                <p className="mt-1 text-sm text-muted-foreground">
                                    Add a symbol above to start tracking it.
                                </p>
                            </div>
                        ) : (
                            <WatchlistQuoteTable
                                rows={rows}
                                busy={busy}
                                onRemove={(symbol) => removeMutation.mutate(symbol)}
                                onMove={moveSymbol}
                            />
                        )}
                    </CardContent>
                </Card>
            )}

            <ListNameDialog
                open={dialog === "create"}
                onOpenChange={(open) => setDialog(open ? "create" : null)}
                title="New watchlist"
                description="Give the list a name. You can rename it later."
                submitLabel="Create"
                pending={createMutation.isPending}
                errors={
                    createMutation.error
                        ? apiErrorMessages(createMutation.error, "Couldn't create the list.")
                        : []
                }
                onSubmit={(name) => createMutation.mutate(name)}
            />

            <ListNameDialog
                open={dialog === "rename"}
                onOpenChange={(open) => setDialog(open ? "rename" : null)}
                title="Rename watchlist"
                description="Names are unique among your lists."
                submitLabel="Rename"
                initialName={active?.name ?? ""}
                pending={renameMutation.isPending}
                errors={
                    renameMutation.error
                        ? apiErrorMessages(renameMutation.error, "Couldn't rename the list.")
                        : []
                }
                onSubmit={(name) => renameMutation.mutate(name)}
            />

            <Dialog
                open={dialog === "delete"}
                onOpenChange={(open) => setDialog(open ? "delete" : null)}
            >
                <DialogContent>
                    <DialogHeader>
                        <DialogTitle>Delete watchlist</DialogTitle>
                        <DialogDescription>
                            Delete “{active?.name}” and its {active?.symbols.length ?? 0} symbols?
                            Alerts on those symbols are not affected.
                        </DialogDescription>
                    </DialogHeader>

                    <DialogFooter>
                        <Button variant="ghost" onClick={() => setDialog(null)}>
                            Cancel
                        </Button>
                        <Button
                            variant="destructive"
                            disabled={deleteMutation.isPending}
                            onClick={() => deleteMutation.mutate()}
                            className={cn(deleteMutation.isPending && "opacity-50")}
                        >
                            {deleteMutation.isPending ? "Deleting..." : "Delete"}
                        </Button>
                    </DialogFooter>
                </DialogContent>
            </Dialog>
        </div>
    );
}

export default Watchlist;
