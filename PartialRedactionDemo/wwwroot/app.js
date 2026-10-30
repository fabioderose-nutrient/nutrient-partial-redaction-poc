(() => {
  "use strict";

  const VARIANTS = {
    original: { title: "Original", sub: "Source document", badge: null },
    full: { title: "Full redaction", sub: "SmartRedaction as shipped today", badge: ["Today", "badge-today"] },
    partial: { title: "Partial redaction", sub: "Same detection, last group kept", badge: ["New", "badge-new"] },
  };
  const CARD_TYPES = ["creditCard"];
  // The UI is served by the GdPicture backend itself, so the API is same-origin.
  const API = "";

  const $ = (id) => document.getElementById(id);
  const state = {
    info: null,
    sample: "dispute-letter",
    file: null,
    types: new Set(),
    job: null,
    columns: new Set(["original", "full", "partial"]),
    zoom: 1,
  };

  const icons = {
    doc: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M8 13h8M8 17h5"/></svg>',
    scan: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 7V5a2 2 0 0 1 2-2h2M17 3h2a2 2 0 0 1 2 2v2M21 17v2a2 2 0 0 1-2 2h-2M7 21H5a2 2 0 0 1-2-2v-2"/><path d="M7 12h10"/></svg>',
    download: '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5"/><path d="M12 15V3"/></svg>',
    check: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6L9 17l-5-5"/></svg>',
  };

  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const alnum = (s) => String(s ?? "").replace(/[^0-9a-z]/gi, "").toLowerCase();

  function toast(message) {
    const t = $("toast");
    t.textContent = message;
    t.hidden = false;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => (t.hidden = true), 6000);
  }

  function presetOf(types) {
    if (types.size === state.info.types.length) return "all";
    if (types.size === CARD_TYPES.length && CARD_TYPES.every((t) => types.has(t))) return "cards";
    return null;
  }

  function pdfUrl(variant) {
    const job = state.job;
    return `${API}api/jobs/${job.jobId}/${variant}.pdf`;
  }

  function pngUrl(variant, page) {
    const job = state.job;
    return `${API}api/jobs/${job.jobId}/${variant}/${page}.png`;
  }

  // ------------------------------------------------------------ sidebar

  function renderSamples() {
    $("sample-list").innerHTML = state.info.samples.map((s) => `
      <button type="button" class="sample${state.sample === s.id && !state.file ? " active" : ""}" role="radio"
        aria-checked="${state.sample === s.id && !state.file}" data-sample="${s.id}">
        ${s.scanned ? icons.scan : icons.doc}
        <span class="sample-title">${esc(s.title)}${s.scanned ? ' <span class="tag">SCAN</span>' : ""}</span>
        <span class="sample-desc">${esc(s.description)}</span>
      </button>`).join("");
    $("upload-zone").classList.toggle("active", !!state.file);
    $("upload-label").innerHTML = state.file
      ? `<strong>${esc(state.file.name)}</strong><br /><small>Click to choose another file</small>`
      : "<strong>Upload your own file</strong><br /><small>PDF, scan, image or Office document</small>";
  }

  function renderTypes() {
    $("type-list").innerHTML = state.info.types.map((t) => `
      <label class="type">
        <input type="checkbox" value="${t.id}" ${state.types.has(t.id) ? "checked" : ""} />
        <span>${esc(t.label)}</span>
      </label>`).join("");
    const preset = presetOf(state.types);
    document.querySelectorAll("[data-preset]").forEach((b) => b.classList.toggle("active", b.dataset.preset === preset));
  }

  function bindSidebar() {
    $("sample-list").addEventListener("click", (e) => {
      const btn = e.target.closest("[data-sample]");
      if (!btn) return;
      state.sample = btn.dataset.sample;
      state.file = null;
      $("file-input").value = "";
      renderSamples();
      run();
    });

    $("file-input").addEventListener("change", (e) => {
      const file = e.target.files[0];
      if (!file) return;
      state.file = file;
      renderSamples();
      run();
    });

    const zone = $("upload-zone");
    ["dragenter", "dragover"].forEach((ev) => zone.addEventListener(ev, (e) => { e.preventDefault(); zone.classList.add("drag"); }));
    ["dragleave", "drop"].forEach((ev) => zone.addEventListener(ev, (e) => { e.preventDefault(); zone.classList.remove("drag"); }));
    zone.addEventListener("drop", (e) => {
      const file = e.dataTransfer.files[0];
      if (!file) return;
      state.file = file;
      renderSamples();
      run();
    });

    $("type-list").addEventListener("change", (e) => {
      if (e.target.checked) state.types.add(e.target.value);
      else state.types.delete(e.target.value);
      renderTypes();
    });

    document.querySelectorAll("[data-preset]").forEach((b) => b.addEventListener("click", () => {
      state.types = new Set(b.dataset.preset === "all" ? state.info.types.map((t) => t.id) : CARD_TYPES);
      renderTypes();
    }));

    $("run").addEventListener("click", run);
  }

  // ------------------------------------------------------------ processing

  async function run() {
    if (state.types.size === 0) { toast("Select at least one data type."); return; }
    const form = new FormData();
    form.append("types", [...state.types].join(","));
    if (state.file) form.append("file", state.file);
    else form.append("sample", state.sample);

    $("loading").hidden = false;
    $("run").disabled = true;
    try {
      const res = await fetch(`${API}api/redact`, { method: "POST", body: form });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
      state.job = data;
      renderResult();
    } catch (err) {
      toast(err.message);
    } finally {
      $("loading").hidden = true;
      $("run").disabled = false;
    }
  }

  function renderResult() {
    const job = state.job;
    const f = job.findings;
    const cards = f.filter((x) => CARD_TYPES.includes(x.type)).length;
    const verified = f.filter((x) => x.mode === "partial" && alnum(x.partialTextLeft) === alnum(x.kept)).length;
    const seconds = ((job.timings.detectMs + job.timings.fullMs + job.timings.partialMs) / 1000).toFixed(1);

    $("summary").hidden = false;
    $("summary-title").textContent = job.name;
    $("summary-sub").textContent = `${job.pages.length} page${job.pages.length > 1 ? "s" : ""} · ${job.engine}${job.ocrUsed ? " · OCR applied to scanned pages" : ""}`;
    $("stats").innerHTML = `
      <div class="stat"><b>${f.length}</b><span>values detected</span></div>
      <div class="stat"><b>${cards}</b><span>card numbers</span></div>
      <div class="stat good"><b>${verified}/${f.length}</b><span>verified in output</span></div>
      <div class="stat"><b>${seconds} s</b><span>processing time</span></div>`;
    $("values-count").textContent = f.length;

    renderCompare();
    renderValues();
  }

  // ------------------------------------------------------------ comparison

  function pageWidth() {
    const n = Math.max(state.columns.size, 1);
    const available = $("tab-compare").clientWidth - 40 - 18 * (n - 1);
    const fit = Math.max(260, Math.min(820, Math.floor(available / n)));
    return Math.round(fit * state.zoom);
  }

  function renderCompare() {
    const job = state.job;
    if (!job) return;
    const cols = ["original", "full", "partial"].filter((c) => state.columns.has(c));
    const el = $("compare");
    $("empty").hidden = true;
    el.hidden = false;
    el.style.setProperty("--page-width", pageWidth() + "px");
    el.style.gridTemplateColumns = `repeat(${cols.length}, var(--page-width))`;

    let html = cols.map((c) => {
      const v = VARIANTS[c];
      const badge = v.badge ? `<span class="badge ${v.badge[1]}">${v.badge[0]}</span>` : "";
      return `<div class="col-head">
        <div><h3>${v.title} ${badge}</h3><p>${v.sub}</p></div>
        <a class="dl" href="${pdfUrl(c)}" download>${icons.download} PDF</a>
      </div>`;
    }).join("");

    for (const page of job.pages) {
      for (const c of cols) {
        const boxes = job.findings.map((f, i) => f.page === page.number ? hlBox(f.area, page, i) : "").join("");
        html += `<div class="page" data-page="${page.number}">
          <img src="${pngUrl(c, page.number)}" alt="${VARIANTS[c].title}, page ${page.number}" loading="lazy" />
          ${boxes}
          <span class="page-no">${page.number} / ${job.pages.length}</span>
        </div>`;
      }
    }
    el.innerHTML = html;
    $("zoom-label").textContent = Math.round(state.zoom * 100) + "%";
  }

  function hlBox(area, page, index) {
    const pad = 3;
    const left = ((area.left - pad) / page.width) * 100;
    const top = ((area.top - pad) / page.height) * 100;
    const width = ((area.width + 2 * pad) / page.width) * 100;
    const height = ((area.height + 2 * pad) / page.height) * 100;
    return `<span class="hl" data-finding="${index}" style="left:${left}%;top:${top}%;width:${width}%;height:${height}%"></span>`;
  }

  function highlight(index, on) {
    document.querySelectorAll(`.hl[data-finding="${index}"]`).forEach((h) => h.classList.toggle("on", on));
  }

  function focusFinding(index) {
    showTab("compare");
    const hl = document.querySelector(`.hl[data-finding="${index}"]`);
    if (!hl) return;
    hl.scrollIntoView({ behavior: "smooth", block: "center" });
    document.querySelectorAll(`.hl[data-finding="${index}"]`).forEach((h) => {
      h.classList.remove("flash");
      void h.offsetWidth;
      h.classList.add("flash");
    });
  }

  function bindCompare() {
    document.querySelectorAll("[data-col]").forEach((chip) => chip.addEventListener("click", () => {
      const col = chip.dataset.col;
      if (state.columns.has(col) && state.columns.size === 1) return;
      state.columns.has(col) ? state.columns.delete(col) : state.columns.add(col);
      chip.classList.toggle("active", state.columns.has(col));
      renderCompare();
    }));
    $("zoom-in").addEventListener("click", () => { state.zoom = Math.min(2.5, +(state.zoom + 0.25).toFixed(2)); renderCompare(); });
    $("zoom-out").addEventListener("click", () => { state.zoom = Math.max(0.5, +(state.zoom - 0.25).toFixed(2)); renderCompare(); });
    let resizeTimer;
    window.addEventListener("resize", () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(renderCompare, 150); });
  }

  // ------------------------------------------------------------ values table

  function renderValues() {
    const labels = Object.fromEntries(state.info.types.map((t) => [t.id, t.label]));
    // Card numbers first, then document order.
    const rows = state.job.findings.map((f, i) => ({ f, i }))
      .sort((a, b) => (CARD_TYPES.includes(b.f.type) - CARD_TYPES.includes(a.f.type)) || a.i - b.i);
    $("values-body").innerHTML = rows.map(({ f, i }) => {
      const partial = f.mode === "partial"
        ? esc(f.partialDisplay).replace(/[^•\s]+/g, (m) => `<span class="kept">${m}</span>`)
        : "(fully redacted)";
      const left = f.partialTextLeft.trim();
      const ok = f.mode === "partial" && alnum(left) === alnum(f.kept);
      return `<tr data-finding="${i}" tabindex="0">
        <td><span class="type-badge${CARD_TYPES.includes(f.type) ? " card" : ""}">${esc(labels[f.type] || f.type)}</span></td>
        <td>${f.page}</td>
        <td class="mono">${esc(f.original)}</td>
        <td class="mono">${partial}</td>
        <td>${ok
          ? `<span class="verified">${icons.check}<span class="mono">${esc(left) || "nothing"}</span></span>`
          : `<span class="mono">${esc(left) || "nothing"}</span>`}</td>
      </tr>`;
    }).join("") || `<tr><td colspan="5">No sensitive value detected for the selected data types.</td></tr>`;
  }

  function bindValues() {
    const body = $("values-body");
    body.addEventListener("mouseover", (e) => { const r = e.target.closest("tr[data-finding]"); if (r) highlight(r.dataset.finding, true); });
    body.addEventListener("mouseout", (e) => { const r = e.target.closest("tr[data-finding]"); if (r) highlight(r.dataset.finding, false); });
    body.addEventListener("click", (e) => { const r = e.target.closest("tr[data-finding]"); if (r) focusFinding(r.dataset.finding); });
    body.addEventListener("keydown", (e) => {
      if (e.key !== "Enter") return;
      const r = e.target.closest("tr[data-finding]");
      if (r) focusFinding(r.dataset.finding);
    });
  }

  // ------------------------------------------------------------ tabs + code

  function showTab(name) {
    document.querySelectorAll(".tab").forEach((t) => {
      const active = t.dataset.tab === name;
      t.classList.toggle("active", active);
      t.setAttribute("aria-selected", active);
    });
    ["compare", "values", "code"].forEach((n) => ($("tab-" + n).hidden = n !== name));
    $("compare-tools").hidden = name !== "compare";
    if (name === "compare") renderCompare();
  }

  function renderCode() {
    $("rules").innerHTML = state.info.types.map((t) => `<tr><td>${esc(t.label)}</td><td>${esc(t.rule)}</td></tr>`).join("");
    $("engine-note").textContent = state.info.nativePartialRedaction
      ? `Running GdPicture.NET ${state.info.gdPictureVersion} with the native PartialRedaction option. Detection, masking and burn-in all happen locally in the SDK.`
      : `PartialRedaction was merged into GdPicture.NET on October 6, 2026 and ships with the next release. This demo runs on GdPicture.NET ${state.info.gdPictureVersion} and applies the same rule with the public API: SmartRedaction detects the values, character geometry keeps the last group, ApplyRedaction burns the result into the PDF. With the new release installed, the demo switches to the native option automatically.`;
  }

  // ------------------------------------------------------------ boot

  async function boot() {
    try {
      const res = await fetch(`${API}api/info`);
      state.info = await res.json();
    } catch {
      toast("The redaction service is not reachable.");
      return;
    }
    state.types = new Set(state.info.types.map((t) => t.id));
    $("engine-pill").textContent = `GdPicture.NET ${state.info.gdPictureVersion}`;
    renderSamples();
    renderTypes();
    renderCode();
    bindSidebar();
    bindCompare();
    bindValues();
    document.querySelectorAll(".tab").forEach((t) => t.addEventListener("click", () => showTab(t.dataset.tab)));
    run();
  }

  boot();
})();
