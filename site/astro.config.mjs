// Umbra website: landing page (src/pages/index.astro), widget catalog (src/pages/widgets.astro) and docs (Starlight,
// src/content/docs/docs/**). Built by .github/workflows/pages.yml; run `npm run dev` here to work on it.
import { defineConfig } from "astro/config";
import starlight from "@astrojs/starlight";
import { beacon } from "./src/lib/beacon.mjs";

const group = (label, directory, collapsed = false) => ({ label, collapsed, items: [{ autogenerate: { directory: `docs/${directory}` } }] });

export default defineConfig({
  site: "https://aminechr54.github.io",
  base: "/umbra",
  trailingSlash: "ignore",
  integrations: [
    starlight({
      title: "Umbra",
      description: "Desktop widgets that live on your wallpaper. Documentation for using Umbra, building widgets and themes, automating it and working on the app.",
      logo: { src: "./src/assets/umbra.png", alt: "Umbra" },
      favicon: "/favicon.png",
      social: [{ icon: "github", label: "GitHub", href: "https://github.com/AmineChr54/umbra" }],
      editLink: { baseUrl: "https://github.com/AmineChr54/umbra/edit/main/site/" },
      lastUpdated: false,
      customCss: ["./src/styles/docs.css"],
      components: { PageTitle: "./src/components/docs/PageTitle.astro" },
      expressiveCode: { themes: ["github-dark-default"], styleOverrides: { borderRadius: "10px" } },
      head: [{ tag: "meta", attrs: { name: "theme-color", content: "#000000" } }, { tag: "script", content: beacon }],
      sidebar: [
        group("Get started", "get-started"),
        group("Using Umbra", "using"),
        group("Build widgets", "widgets"),
        group("Themes", "themes"),
        group("Automate", "automate"),
        group("Internals", "internals", true),
        group("Reference", "reference"),
      ],
    }),
  ],
});
