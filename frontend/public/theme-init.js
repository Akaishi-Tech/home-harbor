// Apply the stored theme before first paint without requiring inline script.
(function () {
  try {
    var stored = localStorage.getItem("homeharbor.theme");
    var prefersDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
    var theme =
      stored === "light" || stored === "dark"
        ? stored
        : prefersDark
          ? "dark"
          : "light";
    var root = document.documentElement;
    root.classList.toggle("dark", theme === "dark");
    root.style.colorScheme = theme;
  } catch (_error) {
    // Theme preference is best-effort when browser storage is unavailable.
  }
})();
