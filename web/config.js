// Where the WclQuery.Api backend lives. The GitHub Pages workflow replaces __API_BASE_URL__
// with the repository variable API_BASE_URL at deploy time.
window.WCLQ_CONFIG = {
  apiBase: ["localhost", "127.0.0.1"].includes(location.hostname)
    ? "http://localhost:5022"
    : "__API_BASE_URL__",
};
