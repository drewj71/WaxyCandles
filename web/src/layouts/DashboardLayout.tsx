import type { ReactNode } from "react";
import {
    BarChart3,
    Bell,
    LayoutDashboard,
    LogOut,
    Settings,
    Star,
} from "lucide-react";
import { Link } from "react-router-dom";

import {
    Sidebar,
    SidebarContent,
    SidebarFooter,
    SidebarGroup,
    SidebarGroupContent,
    SidebarGroupLabel,
    SidebarHeader,
    SidebarMenu,
    SidebarMenuButton,
    SidebarMenuItem,
    SidebarProvider,
    SidebarTrigger,
} from "@/components/ui/sidebar";

import { Avatar, AvatarFallback } from "@/components/ui/avatar";

import {
    DropdownMenu,
    DropdownMenuContent,
    DropdownMenuItem,
    DropdownMenuSeparator,
    DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

interface DashboardLayoutProps {
    children: ReactNode;
    userEmail: string;
    onLogout: () => void;
}

const navigation = [
    {
        title: "Dashboard",
        icon: LayoutDashboard,
        href: "/dashboard",
    },
    {
        title: "Watchlist",
        icon: Star,
        href: "/watchlist",
    },
    {
        title: "Markets",
        icon: BarChart3,
        href: "/markets",
    },
    {
        title: "Alerts",
        icon: Bell,
        href: "/alerts",
    },
];

function DashboardLayout({
    children,
    userEmail,
    onLogout,
}: DashboardLayoutProps) {
    const initials = userEmail
        .slice(0, 2)
        .toUpperCase();

    return (
        <SidebarProvider>
            <div className="flex min-h-svh w-full">
                <Sidebar>
                    <SidebarHeader>
                        <div className="flex items-center gap-3 px-2 py-3">
                            <div className="flex size-9 items-center justify-center rounded-lg bg-primary text-primary-foreground">
                                <BarChart3 className="size-5" />
                            </div>

                            <div>
                                <h1 className="text-sm font-semibold">
                                    WaxyCandles
                                </h1>

                                <p className="text-xs text-muted-foreground">
                                    Market Intelligence
                                </p>
                            </div>
                        </div>
                    </SidebarHeader>

                    <SidebarContent>
                        <SidebarGroup>
                            <SidebarGroupLabel>
                                Platform
                            </SidebarGroupLabel>

                            <SidebarGroupContent>
                                <SidebarMenu>
                                    {navigation.map((item) => (
                                        <SidebarMenuItem key={item.title}>
                                            <SidebarMenuButton
                                                render={
                                                    <Link to={item.href} />
                                                }
                                                tooltip={item.title}
                                            >
                                                <item.icon />
                                                <span>{item.title}</span>
                                            </SidebarMenuButton>
                                        </SidebarMenuItem>
                                    ))}
                                </SidebarMenu>
                            </SidebarGroupContent>
                        </SidebarGroup>
                    </SidebarContent>

                    <SidebarFooter>
                        <SidebarMenu>
                            <SidebarMenuItem>
                                <SidebarMenuButton
                                    render={
                                        <Link to="/settings" />
                                    }
                                >
                                    <Settings />
                                    <span>Settings</span>
                                </SidebarMenuButton>
                            </SidebarMenuItem>

                            <SidebarMenuItem>
                                <DropdownMenu>
                                    <DropdownMenuTrigger
                                        render={
                                            <SidebarMenuButton />
                                        }
                                    >
                                        <Avatar className="size-7">
                                            <AvatarFallback>
                                                {initials}
                                            </AvatarFallback>
                                        </Avatar>

                                        <span className="truncate">
                                            {userEmail}
                                        </span>
                                    </DropdownMenuTrigger>

                                    <DropdownMenuContent
                                        side="top"
                                        align="start"
                                        className="w-56"
                                    >
                                        <DropdownMenuItem
                                            render={
                                                <Link to="/settings" />
                                            }
                                        >
                                            <Settings />
                                            Settings
                                        </DropdownMenuItem>

                                        <DropdownMenuSeparator />

                                        <DropdownMenuItem
                                            onClick={onLogout}
                                        >
                                            <LogOut />
                                            Log out
                                        </DropdownMenuItem>
                                    </DropdownMenuContent>
                                </DropdownMenu>
                            </SidebarMenuItem>
                        </SidebarMenu>
                    </SidebarFooter>
                </Sidebar>

                <div className="flex min-w-0 flex-1 flex-col">
                    <header className="flex h-14 items-center border-b px-4">
                        <SidebarTrigger />

                        <div className="ml-auto flex items-center">
                            <span className="text-sm text-muted-foreground">
                                Market Dashboard
                            </span>
                        </div>
                    </header>

                    <main className="flex-1 bg-muted/30 p-6">
                        {children}
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}

export default DashboardLayout;