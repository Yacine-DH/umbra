// Repo data written by scripts/sync.mjs (widgets/index.json, themes/index.json, Directory.Build.props).
import site from "../data/site.json";

export interface Widget {
  id: string; name: string; description: string; author: string; version: string; category?: string;
  hosts?: string[]; commands?: { run: string }[]; previewSize: { w: number; h: number } | null;
}
export interface Theme { id: string; name: string; description: string; author: string; tokens: Record<string, string> }

export const version: string = site.version;
export const widgets = site.widgets as unknown as Widget[];
export const themes = site.themes as unknown as Theme[];
export const byId = (id: string) => widgets.find(w => w.id === id);

/** What a widget can reach: drives the catalog badges and filters. */
export const kindOf = (w: Widget) => (w.commands ?? []).length ? "commands" : (w.hosts ?? []).length ? "online" : "offline";

/** widget.json "category" ids in catalog order (WidgetManifest.Categories in the app), with their labels. */
export const categories: [string, string][] = [
  ["time", "Time & dates"], ["productivity", "Productivity"], ["info", "Info & news"], ["markets", "Markets"],
  ["system", "System"], ["media", "Media"], ["developer", "Developer"], ["social", "Social"], ["games", "Games"],
];
export const categoryOf = (w: Widget) => (categories.some(([id]) => id === w.category) ? w.category! : "other");
export const categoryLabel = (id: string) => categories.find(([c]) => c === id)?.[1] ?? "Other";
