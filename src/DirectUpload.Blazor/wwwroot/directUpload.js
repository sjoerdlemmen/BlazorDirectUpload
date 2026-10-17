// DirectUpload: browser -> S3-compatible storage via presigned multipart URLs.
//
// The File object never leaves JavaScript: .NET only sees file metadata,
// progress numbers and the final result. Every upload has its own state
// (keyed by id), so cancelling or failing one upload never touches another.

const uploads = new Map();

export function getFiles(input) {
  return Array.from(input?.files ?? [], (f) => ({ name: f.name, size: f.size, contentType: f.type || "" }));
}

export function start(id, input, fileIndex, config, dotnetRef) {
  const file = input?.files?.[fileIndex];
  if (!file) throw new Error(`No file selected at index ${fileIndex}.`);
  if (uploads.has(id)) throw new Error(`Upload ${id} is already running.`);

  const state = {
    id,
    file,
    config,
    dotnetRef,
    endpoint: new URL(config.endpoint.replace(/\/?$/, "/"), document.baseURI),
    controller: new AbortController(),
    xhrs: new Set(),
    cancelled: false,
    fatal: null,
    token: null,
  };
  uploads.set(id, state);

  run(state).finally(() => uploads.delete(id));
  return { name: file.name, size: file.size, contentType: file.type || "" };
}

export function cancel(id) {
  const state = uploads.get(id);
  if (!state) return false;
  state.cancelled = true;
  stopTransfers(state);
  return true;
}

async function run(state) {
  let result;
  try {
    result = await upload(state);
  } catch (err) {
    stopTransfers(state);
    const abortError = state.token ? await abortOnServer(state) : null;
    if (state.cancelled) {
      await notify(state, "OnCancelled", abortError);
    } else {
      const message = state.fatal?.message ?? err?.message ?? String(err);
      await notify(state, "OnFailed", message, abortError);
    }
    return;
  }
  await notify(state, "OnCompleted", result);
}

async function upload(state) {
  const { file, config } = state;
  const started = await api(state, "start", {
    fileName: file.name,
    contentType: file.type || "",
    size: file.size,
  });
  state.token = started.token;
  throwIfStopped(state);

  const { partSize, partCount } = started;
  const urls = new UrlCache(state, partCount);
  const loaded = new Map(); // partNumber -> bytes sent so far (in-flight parts)
  let doneBytes = 0;
  const progress = new ProgressReporter(state, file.size, () => {
    let inFlight = 0;
    for (const v of loaded.values()) inFlight += v;
    return doneBytes + inFlight;
  });

  const parts = [];
  let next = 1;
  const worker = async () => {
    while (next <= partCount) {
      throwIfStopped(state);
      const partNumber = next++;
      const begin = (partNumber - 1) * partSize;
      const blob = file.slice(begin, Math.min(begin + partSize, file.size));
      const eTag = await uploadPart(state, urls, partNumber, blob, (n) => {
        loaded.set(partNumber, n);
        progress.tick();
      });
      loaded.delete(partNumber);
      doneBytes += blob.size;
      parts.push({ partNumber, eTag });
      progress.tick();
    }
  };

  try {
    const workers = Array.from({ length: Math.max(1, Math.min(config.concurrency, partCount)) }, worker);
    // allSettled-then-throw: when one part fails fatally the siblings are
    // stopped (stopTransfers) and we wait for them to unwind before aborting.
    const outcomes = await Promise.allSettled(workers.map((w) => w.catch((e) => { fail(state, e); throw e; })));
    const rejected = outcomes.find((o) => o.status === "rejected");
    if (rejected) throw state.fatal ?? rejected.reason;
  } finally {
    progress.stop();
  }

  throwIfStopped(state);
  if (parts.length !== partCount) {
    // Belt and braces: never complete with a missing part.
    throw new Error(`Only ${parts.length} of ${partCount} parts were uploaded.`);
  }
  await progress.flush();

  parts.sort((a, b) => a.partNumber - b.partNumber);
  return await api(state, "complete", { token: state.token, parts });
}

async function uploadPart(state, urls, partNumber, blob, onProgress) {
  const maxAttempts = state.config.maxAttempts;
  let lastError = null;

  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    throwIfStopped(state);
    const url = await urls.get(partNumber);
    let response;
    try {
      response = await put(state, url, blob, onProgress);
    } catch (err) {
      if (err?.name === "AbortError") throw err;
      lastError = err; // network error: retry with backoff
    }

    if (response) {
      if (response.status >= 200 && response.status < 300) {
        const eTag = response.eTag;
        if (!eTag) {
          throw new Error(
            `Part ${partNumber} was stored but the ETag response header is not readable. ` +
              `The bucket CORS policy must expose the ETag header (ExposeHeaders: ["ETag"]).`
          );
        }
        return eTag.replaceAll('"', "");
      }
      lastError = new Error(`Part ${partNumber}: storage returned HTTP ${response.status}.`);
      if (response.status === 403) {
        // Most likely an expired presigned URL: get a fresh one and retry right away.
        urls.invalidate(partNumber);
        continue;
      }
      if (response.status !== 408 && response.status !== 429 && response.status < 500) {
        throw lastError; // permanent client error: retrying will not help
      }
    }
    onProgress(0);
    if (attempt < maxAttempts) await delay(state, Math.min(500 * 2 ** (attempt - 1), 8000));
  }

  // Never fall through silently: an exhausted part fails the whole upload.
  throw new Error(`Part ${partNumber} failed after ${maxAttempts} attempts: ${lastError?.message ?? "unknown error"}`);
}

function put(state, url, blob, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    state.xhrs.add(xhr);
    const done = () => state.xhrs.delete(xhr);
    xhr.open("PUT", url);
    xhr.upload.onprogress = (e) => onProgress(e.loaded);
    xhr.onload = () => { done(); resolve({ status: xhr.status, eTag: xhr.getResponseHeader("ETag") }); };
    xhr.onerror = () => { done(); reject(new Error("Network error while uploading a part (check bucket CORS and connectivity).")); };
    xhr.onabort = () => { done(); reject(new DOMException("Upload stopped.", "AbortError")); };
    xhr.send(blob);
  });
}

class UrlCache {
  constructor(state, partCount) {
    this.state = state;
    this.partCount = partCount;
    this.pending = new Map(); // partNumber -> Promise<url>
    this.batch = Math.max(state.config.urlBatchSize, state.config.concurrency);
  }

  get(partNumber) {
    if (!this.pending.has(partNumber)) {
      const numbers = [];
      for (let n = partNumber; n <= this.partCount && numbers.length < this.batch; n++) {
        if (!this.pending.has(n)) numbers.push(n);
      }
      const request = api(this.state, "part-urls", { token: this.state.token, partNumbers: numbers })
        .then((r) => new Map(r.urls.map((u) => [u.partNumber, u.url])));
      for (const n of numbers) {
        const p = request.then((m) => {
          const url = m.get(n);
          if (!url) throw new Error(`Server returned no URL for part ${n}.`);
          return url;
        });
        p.catch(() => this.pending.delete(n)); // allow a retry after a failed fetch
        this.pending.set(n, p);
      }
    }
    return this.pending.get(partNumber);
  }

  invalidate(partNumber) {
    this.pending.delete(partNumber);
  }
}

class ProgressReporter {
  constructor(state, total, read) {
    this.state = state;
    this.total = total;
    this.read = read;
    this.last = -1;
    this.timer = null;
    this.interval = state.config.progressIntervalMs;
  }

  tick() {
    // Throttled: at most one .NET callback per interval (matters for Blazor Server/SignalR).
    if (this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.flush();
    }, this.interval);
  }

  async flush() {
    const value = Math.min(this.read(), this.total);
    if (value === this.last) return;
    this.last = value;
    await notify(this.state, "OnProgress", value, this.total);
  }

  stop() {
    clearTimeout(this.timer);
    this.timer = null;
  }
}

async function api(state, action, body) {
  const attempts = action === "part-urls" ? state.config.maxAttempts : 1;
  for (let attempt = 1; ; attempt++) {
    let response;
    try {
      response = await fetch(new URL(action, state.endpoint), {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", ...(state.config.headers ?? {}) },
        body: JSON.stringify(body),
        signal: action === "abort" ? undefined : state.controller.signal,
      });
    } catch (err) {
      if (err?.name === "AbortError" || attempt >= attempts) throw err;
      await delay(state, 500 * 2 ** (attempt - 1));
      continue;
    }
    if (response.ok) return await response.json();
    if (response.status >= 500 && attempt < attempts) {
      await delay(state, 500 * 2 ** (attempt - 1));
      continue;
    }
    let detail = "";
    try {
      const problem = await response.json();
      detail = problem.detail ?? problem.title ?? "";
    } catch {
      /* not JSON */
    }
    throw new Error(`Upload ${action} failed: HTTP ${response.status}${detail ? ` - ${detail}` : ""}`);
  }
}

async function abortOnServer(state) {
  try {
    await api(state, "abort", { token: state.token });
    return null;
  } catch (err) {
    return err?.message ?? String(err);
  }
}

function fail(state, err) {
  if (!state.fatal && !state.cancelled && err?.name !== "AbortError") state.fatal = err;
  stopTransfers(state);
}

function stopTransfers(state) {
  state.controller.abort();
  for (const xhr of [...state.xhrs]) xhr.abort();
}

function throwIfStopped(state) {
  if (state.cancelled || state.fatal || state.controller.signal.aborted) {
    throw state.fatal ?? new DOMException("Upload stopped.", "AbortError");
  }
}

function delay(state, ms) {
  return new Promise((resolve, reject) => {
    const signal = state.controller.signal;
    if (signal.aborted) return reject(new DOMException("Upload stopped.", "AbortError"));
    const timer = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => { clearTimeout(timer); reject(new DOMException("Upload stopped.", "AbortError")); }, { once: true });
  });
}

async function notify(state, method, ...args) {
  try {
    await state.dotnetRef.invokeMethodAsync(method, ...args);
  } catch (err) {
    // The component or circuit may be gone; the upload itself is unaffected.
    console.warn(`DirectUpload: ${method} callback failed`, err);
  }
}
