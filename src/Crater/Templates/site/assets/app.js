"use strict";

const state = {
  registry: null,
  packages: [],
  query: "",
  sort: "updated",
};

const view = document.getElementById("view");
const searchInput = document.getElementById("search");

function element(tag, properties, ...children) {
  const node = document.createElement(tag);

  for (const [key, value] of Object.entries(properties || {})) {
    if (value === null || value === undefined || value === false) {
      continue;
    }

    if (key === "className") {
      node.className = value;
    } else if (key === "text") {
      node.textContent = value;
    } else if (key.startsWith("on")) {
      node.addEventListener(key.slice(2), value);
    } else {
      node.setAttribute(key, value);
    }
  }

  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) {
      continue;
    }

    node.append(typeof child === "string" ? document.createTextNode(child) : child);
  }

  return node;
}

async function loadJson(path) {
  const response = await fetch(path, { cache: "no-cache" });

  if (!response.ok) {
    throw new Error(`${path}: HTTP ${response.status}`);
  }

  return response.json();
}

function parseVersion(text) {
  const [core, prerelease = ""] = String(text).split("-", 2);
  const numbers = core.split(".").map((part) => Number.parseInt(part, 10) || 0);

  return { numbers, prerelease };
}

function compareVersions(left, right) {
  const a = parseVersion(left);
  const b = parseVersion(right);

  for (let i = 0; i < 3; i++) {
    if (a.numbers[i] !== b.numbers[i]) {
      return a.numbers[i] - b.numbers[i];
    }
  }

  if (a.prerelease === b.prerelease) {
    return 0;
  }

  if (a.prerelease === "") {
    return 1;
  }

  if (b.prerelease === "") {
    return -1;
  }

  return a.prerelease < b.prerelease ? -1 : 1;
}

function formatDate(value) {
  if (!value) {
    return "";
  }

  const date = new Date(value);

  if (Number.isNaN(date.getTime()) || date.getFullYear() < 2000) {
    return "";
  }

  return date.toLocaleDateString("en-GB", { year: "numeric", month: "short", day: "numeric" });
}

function pad(number) {
  return String(number).padStart(2, "0");
}

function splitName(fullName) {
  const slash = fullName.indexOf("/");

  return { scope: fullName.slice(0, slash), name: fullName.slice(slash + 1) };
}

function score(entry, query) {
  const name = entry.name.toLowerCase();
  const shortName = splitName(name).name;
  const keywords = (entry.keywords || []).map((keyword) => keyword.toLowerCase());
  let total = 0;

  if (shortName === query || name === query) {
    total += 100;
  } else if (shortName.startsWith(query)) {
    total += 60;
  } else if (name.includes(query)) {
    total += 40;
  }

  if (keywords.includes(query)) {
    total += 30;
  }

  if ((entry.description || "").toLowerCase().includes(query)) {
    total += 10;
  }

  return total;
}

function registryConfigValue() {
  const registry = state.registry || {};

  if (registry.url) {
    return registry.url;
  }

  return registry.repository ? `https://raw.githubusercontent.com/${registry.repository}/main` : "";
}

function copyButton(text) {
  const button = element("button", { className: "copy", type: "button", text: "copy" });

  button.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = "copied";
    } catch {
      button.textContent = "failed";
    }

    setTimeout(() => {
      button.textContent = "copy";
    }, 1400);
  });

  return button;
}

function snippet(text) {
  return element("div", { className: "snippet" }, element("code", { text }), copyButton(text));
}

function safeUrl(value) {
  try {
    const url = new URL(value);

    return url.protocol === "https:" || url.protocol === "http:" ? url.href : null;
  } catch {
    return null;
  }
}

function eyebrow(text) {
  return element("p", { className: "eyebrow" }, element("span", { className: "dot" }), text);
}

function display(text, compact) {
  return element("h1", { className: compact ? "display compact" : "display" },
    text,
    element("span", { className: "display-dot", text: "." }),
  );
}

function tile(entry, index) {
  const { scope, name } = splitName(entry.name);
  const updated = formatDate(entry.updated_at);

  return element("a", { className: "tile", href: `#/pkg/${entry.name}` },
    element("div", { className: "tile-top" },
      element("span", { className: "tile-number", text: `${pad(index + 1)} · ${scope}` }),
      element("span", { text: entry.import_to || "shared" }),
    ),
    element("h3", { className: "tile-name", text: name }),
    element("p", { className: "tile-desc", text: entry.description || "No description." }),
    element("div", { className: "tile-meta" },
      element("span", { className: "version", text: entry.latest }),
      entry.license ? element("span", { text: entry.license }) : null,
      updated ? element("span", { text: updated }) : null,
    ),
  );
}

function sortedPackages() {
  const list = [...state.packages];

  if (state.sort === "name") {
    return list.sort((left, right) => left.name.localeCompare(right.name));
  }

  return list.sort((left, right) => String(right.updated_at).localeCompare(String(left.updated_at)));
}

function emptyState() {
  const repository = state.registry?.repository;
  const contributing = repository ? `https://github.com/${repository}/blob/main/CONTRIBUTING.md` : null;

  return element("div", { className: "empty" },
    element("h3", { text: "No packages yet" }),
    element("p", { text: "Publish the first one: name it after your GitHub login, then open a pull request with crater." }),
    snippet("crater publish --registry <your fork> --pr"),
    contributing ? element("a", { className: "button", href: contributing, rel: "noopener", text: "How to publish →" }) : null,
  );
}

function marquee() {
  const words = state.packages.length > 0
    ? state.packages.map((entry) => splitName(entry.name).name)
    : ["Luau", "Roblox", "crater", "open source", "packages"];

  const repeated = [];

  while (repeated.length < 12) {
    repeated.push(...words);
  }

  const items = [...repeated, ...repeated].map((word) => element("span", { className: "marquee-item", text: word }));

  return element("div", { className: "marquee", "aria-hidden": "true" },
    element("div", { className: "marquee-track" }, items),
  );
}

function principles() {
  const versions = state.packages.reduce((total, entry) => total + (entry.versions || 0), 0);
  const authors = new Set(state.packages.map((entry) => splitName(entry.name).scope)).size;

  const items = [
    [String(state.packages.length), "packages", "Every package lives in its author's folder, one copy per version."],
    [String(versions), "versions", "Published versions never change. Every archive is checked by sha256."],
    [String(authors), "authors", "Anyone can publish through a pull request, reviewed before merge."],
  ];

  return element("div", { className: "principles" },
    items.map(([value, label, text], index) => element("div", { className: "principle" },
      element("span", { className: "number", text: `${pad(index + 1)} · ${label}` }),
      element("h3", { text: value }),
      element("p", { text }),
    )),
  );
}

function sortControls() {
  const option = (key, label) => element("button", {
    className: state.sort === key ? "pill active" : "pill",
    type: "button",
    text: label,
    onclick: () => {
      state.sort = key;
      renderHome();
      document.getElementById("packages")?.scrollIntoView();
    },
  });

  return element("div", { className: "sorts" }, option("updated", "recent"), option("name", "a-z"));
}

function renderHome() {
  const query = state.query.trim().toLowerCase();

  if (query) {
    renderResults(query);
    return;
  }

  const registry = state.registry || {};
  const description = registry.description || "Packages for Luau and Roblox, installed with crater.";
  const configValue = registryConfigValue();

  view.replaceChildren(
    eyebrow("Package registry for Luau and Roblox"),
    display(registry.name ? registry.name.replace(/\s+registry$/i, "") || "crater" : "crater"),
    element("div", { className: "hero-row" },
      element("div", {},
        element("p", { className: "lead", text: description }),
      ),
      element("div", { className: "hero-actions" },
        element("a", { className: "button", href: "#packages", text: "Browse packages →" }),
      ),
    ),
    marquee(),
    principles(),
    element("section", { className: "section", id: "packages" },
      element("div", { className: "section-head" },
        element("div", {},
          element("h2", { className: "section-title", text: "Packages" }),
          configValue ? element("p", { className: "section-note", text: `"registry": "${configValue}"` }) : null,
        ),
        state.packages.length > 1 ? sortControls() : null,
      ),
      state.packages.length > 0
        ? element("div", { className: "tiles" }, sortedPackages().map(tile))
        : emptyState(),
    ),
  );
}

function renderResults(query) {
  const results = state.packages
    .map((entry) => [entry, score(entry, query)])
    .filter(([, value]) => value > 0)
    .sort((left, right) => right[1] - left[1] || left[0].name.localeCompare(right[0].name))
    .map(([entry]) => entry);

  const count = `${results.length} ${results.length === 1 ? "package" : "packages"}`;

  view.replaceChildren(
    eyebrow(`Search · ${count}`),
    display(state.query.trim(), true),
    element("section", { className: "section" },
      results.length > 0
        ? element("div", { className: "tiles" }, results.map(tile))
        : element("div", { className: "empty" },
          element("h3", { text: "Nothing found" }),
          element("p", { text: "Try another name, or a word from the description." }),
        ),
    ),
  );
}

function renderReadme(container, markdown) {
  if (!markdown) {
    container.append(element("p", { text: "This package has no README." }));
    return;
  }

  if (window.marked && window.DOMPurify) {
    container.innerHTML = window.DOMPurify.sanitize(window.marked.parse(markdown));
    return;
  }

  container.append(element("pre", {}, element("code", { text: markdown })));
}

function dependencyPanel(title, dependencies) {
  if (!dependencies || dependencies.length === 0) {
    return null;
  }

  const items = dependencies.map((raw) => {
    const match = /^(?:[A-Za-z][\w-]*:)?([a-z0-9_-]+\/[a-z0-9_-]+)(.*)$/i.exec(raw);
    const name = match ? match[1].toLowerCase() : raw;
    const requirement = match ? match[2] : "";

    return element("li", {},
      element("a", { href: `#/pkg/${name}`, text: name }),
      element("span", { className: "dim", text: requirement }),
    );
  });

  return element("div", { className: "panel" },
    element("h3", { className: "label", text: title }),
    element("ul", { className: "list" }, items),
  );
}

function spec(label, value) {
  if (!value) {
    return null;
  }

  return element("div", { className: "spec" }, element("dt", { text: label }), element("dd", {}, value));
}

async function renderPackage(fullName, requestedVersion) {
  view.replaceChildren(eyebrow("Loading"));

  let data;

  try {
    data = await loadJson(`data/packages/${fullName}.json`);
  } catch {
    view.replaceChildren(
      eyebrow("404"),
      display("Not found", true),
      element("div", { className: "hero-row" },
        element("p", { className: "lead", text: fullName }),
        element("div", { className: "hero-actions" }, element("a", { className: "button", href: "#/", text: "All packages →" })),
      ),
    );
    return;
  }

  const versions = [...data.versions].sort((left, right) => compareVersions(right.version, left.version));
  const release = versions.find((item) => item.version === requestedVersion)
    || versions.find((item) => item.version === data.latest)
    || versions[0];

  const manifest = release.manifest || {};
  const { scope, name } = splitName(fullName);
  const repository = safeUrl(manifest.repository);
  const configValue = registryConfigValue();
  const published = formatDate(release.published_at);

  const readme = element("article", { className: "panel readme" });
  renderReadme(readme, release.version === data.latest ? data.readme : null);

  const versionItems = versions.slice(0, 40).map((item) => element("li", {},
    element("a", {
      href: `#/pkg/${fullName}/${item.version}`,
      className: item.version === release.version ? "current" : null,
      text: item.version,
    }),
    element("span", { className: "dim", text: formatDate(item.published_at) }),
  ));

  view.replaceChildren(
    eyebrow(`${scope} / ${release.version}`),
    display(name, true),
    element("div", { className: "hero-row" },
      element("p", { className: "lead", text: manifest.description || "No description." }),
      element("div", { className: "hero-actions" },
        repository ? element("a", { className: "pill", href: repository, rel: "noopener", text: "source ↗" }) : null,
        element("a", { className: "pill", href: "#/", text: "all packages" }),
      ),
    ),
    element("div", { className: "package-layout" },
      readme,
      element("aside", { className: "side" },
        element("div", { className: "panel" },
          element("h3", { className: "label", text: "Install" }),
          snippet(`crater add ${fullName}^${release.version}`),
          snippet(`"${fullName}^${release.version}"`),
          configValue ? snippet(`"registry": "${configValue}"`) : null,
        ),
        element("div", { className: "panel" },
          element("h3", { className: "label", text: "About" }),
          element("dl", { className: "specs" },
            spec("realm", manifest.import_to || "shared"),
            spec("license", manifest.license),
            spec("authors", (manifest.authors || []).join(", ")),
            spec("published", published),
            spec("keywords", (manifest.keywords || []).join(", ")),
          ),
        ),
        dependencyPanel("Client dependencies", manifest.import_client),
        dependencyPanel("Server dependencies", manifest.import_server),
        element("div", { className: "panel" },
          element("h3", { className: "label", text: `Versions · ${versions.length}` }),
          element("ul", { className: "list" }, versionItems),
        ),
      ),
    ),
  );

  document.title = `${fullName} · ${state.registry?.name || "Registry"}`;
}

function route() {
  const hash = decodeURIComponent(location.hash.replace(/^#\/?/, ""));
  const parts = hash.split("/").filter(Boolean);

  if (parts[0] === "pkg" && parts.length >= 3) {
    renderPackage(`${parts[1]}/${parts[2]}`.toLowerCase(), parts[3]);
    window.scrollTo(0, 0);
    return;
  }

  document.title = state.registry?.name || "Registry";
  renderHome();

  if (parts[0] === "packages") {
    document.getElementById("packages")?.scrollIntoView();
  }
}

let searchTimer = 0;

searchInput.addEventListener("input", () => {
  clearTimeout(searchTimer);

  searchTimer = setTimeout(() => {
    state.query = searchInput.value;

    if (location.hash.startsWith("#/pkg/")) {
      location.hash = "#/";
      return;
    }

    renderHome();
  }, 120);
});

document.addEventListener("keydown", (event) => {
  if (event.key === "/" && document.activeElement !== searchInput) {
    event.preventDefault();
    searchInput.focus();
  }

  if (event.key === "Escape" && document.activeElement === searchInput) {
    searchInput.value = "";
    state.query = "";
    renderHome();
  }
});

window.addEventListener("hashchange", route);

async function start() {
  try {
    const [registry, catalog] = await Promise.all([
      loadJson("data/registry.json"),
      loadJson("data/packages.json"),
    ]);

    state.registry = registry;
    state.packages = catalog.packages || [];
  } catch (error) {
    view.replaceChildren(eyebrow("Error"), element("p", { className: "lead", text: `Could not load the registry: ${error.message}` }));
    return;
  }

  route();
}

start();
