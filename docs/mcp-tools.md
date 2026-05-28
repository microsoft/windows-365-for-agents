# MCP Tool Reference

Windows 365 for Agents ships **62 built-in tools**, invoked via `tools/call` over the [MCP endpoint](./api-reference.md#mcp-model-context-protocol). Coordinates use screen pixels with `(0, 0)` at top-left. Discover tools at runtime via `tools/list`.

---

## Desktop Tools (26)

### `move_mouse`

Move cursor to screen position. Use `click` instead if you intend to click.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | — | X in screen pixels |
| `y` | int | Yes | — | Y in screen pixels |

**Returns:** Text confirmation.

### `click`

Click at coordinates, or current cursor position if omitted.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | No | null | X in screen pixels |
| `y` | int | No | null | Y in screen pixels |
| `button` | string | No | `"Left"` | `Left`, `Right`, `Middle`, `Forward`, `Backward` |
| `clickCount` | int | No | 1 | 1 = single, 2 = double |

**Returns:** Text confirmation.

### `get_cursor_position`

No parameters. **Returns:** JSON `{cursorX, cursorY}`.

### `drag_mouse`

Drag from start to end. Also useful for pixel-precise scrolling.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `startX` | int | Yes | — | Start X |
| `startY` | int | Yes | — | Start Y |
| `endX` | int | Yes | — | End X |
| `endY` | int | Yes | — | End Y |
| `button` | string | No | `"Left"` | `Left`, `Right`, `Middle` |

### `scroll`

Scroll in notches (NOT pixels). 3 notches ≈ one page. Positive `deltaY` = down, `deltaX` = right. Clamped to [-20, 20].

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | — | Scroll position X |
| `y` | int | Yes | — | Scroll position Y |
| `deltaX` | int | No | 0 | Horizontal notches |
| `deltaY` | int | No | 0 | Vertical notches |

### `type_text`

Type text via keyboard simulation. For shortcuts use `press_keys`. For browser form fields prefer `browser_type`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | — | Text to type |

### `press_keys`

Press key combination simultaneously. E.g. `["ctrl", "c"]`, `["alt", "tab"]`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `keys` | string[] | Yes | — | Key names to press together |

### `take_screenshot`

Capture full screen or cropped region as PNG.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | No | null | Crop left edge |
| `y` | int | No | null | Crop top edge |
| `width` | int | No | null | Crop width |
| `height` | int | No | null | Crop height |

> All four crop params must be provided together or all omitted.

**Returns:** MCP image content block (base64 PNG).

### `zoom_region`

Capture a screen region at native resolution as PNG. Use to inspect small text or dense UI. Max region: 1920x1080.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | — | Left edge X in screen pixels |
| `y` | int | Yes | — | Top edge Y in screen pixels |
| `width` | int | Yes | — | Width in pixels |
| `height` | int | Yes | — | Height in pixels |

**Returns:** MCP image content block (base64 PNG).

### `analyze_screen`

OCR the screen. No parameters. **Returns:** JSON `{fullText, averageConfidence, boxes[{text, confidence, x, y, width, height}], width, height}`.

### `list_windows`

No parameters. **Returns:** JSON array `[{title, processName, handle, x, y, width, height}]`.

### `activate_window`

Bring window to foreground by fuzzy title match.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | — | Partial title (case-insensitive substring) |

### `close_window`

Graceful close. Protected system processes cannot be closed.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | — | Partial title (80% match threshold) |

**Returns:** JSON `{matchedTitle, processName, closed}`.

### `resize_window`

Resize, move, maximize, minimize, or restore a window by fuzzy title match.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | — | Window title to match (case-insensitive fuzzy match) |
| `action` | string | Yes | — | Action: `Resize`, `Move`, `Maximize`, `Minimize`, `Restore` |
| `x` | int | No | null | Left edge X (for Resize/Move) |
| `y` | int | No | null | Top edge Y (for Resize/Move) |
| `width` | int | No | null | Width (for Resize) |
| `height` | int | No | null | Height (for Resize) |

**Returns:** Text confirmation.

### `get_screen_size`

No parameters. **Returns:** JSON `{width, height}`.

### `execute_shell_command`

Run a shell command with piping (`|`) and chaining (`&&`, `||`, `&`). Default timeout 30s, max 120s.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `command` | string | Yes | — | Command to execute |
| `cwd` | string | No | null | Working directory. Use forward slashes (`C:/Users/me/project`). |
| `timeoutMs` | int | No | 30000 | Timeout in ms (max 120000) |

**Returns:** JSON `{stdout, stderr, exitCode, success, timedOut}`.

> stdout/stderr truncated at 32 KB.

### `execute_python_code`

Execute Python code in a sandboxed process (512 MB memory, max 120s, 262,144 char limit).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `code` | string | Yes | — | Python code (max 262,144 chars) |
| `cwd` | string | No | null | Working directory. Use forward slashes. |
| `timeoutMs` | int | No | 30000 | Timeout in ms (max 120000) |

**Returns:** JSON `{stdout, stderr, exitCode}`.

### `wait_milliseconds`

One-shot pause. Do NOT loop — use `browser_wait_for` for polling. Clamped to [0, 5000].

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `ms` | int | Yes | — | Duration in ms (max 5000) |

### `clipboard_read`

Read system clipboard content. Returns format and payload: text string or base64-encoded image.

No parameters. **Returns:** JSON with clipboard content (format and payload).

### `clipboard_write`

Write text to the system clipboard, replacing current content.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | — | Text to write to clipboard |

**Returns:** Text confirmation with character count.

### `list_processes`

List running processes (current session only). Returns PID, name, memory, window title, and `startTimeTicks`. Use `startTimeTicks` with `kill_process` to prevent PID recycling.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxCount` | int | No | 200 | Maximum number of processes to return |

**Returns:** JSON array of process info objects.

### `kill_process`

Terminate a process by PID. Requires `startTime` from `list_processes` to prevent killing a recycled PID.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pid` | int | Yes | — | Process ID from `list_processes` |
| `startTime` | long | Yes | — | Process start time ticks from `list_processes` (prevents PID recycling) |
| `force` | bool | No | false | Force kill without graceful shutdown |

**Returns:** JSON result.

### `launch_application`

Launch a GUI application from allowed directories. Use `execute_shell_command` for CLI commands.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `path` | string | Yes | — | Absolute path to the executable. Use forward slashes (`C:/Program Files/app.exe`). |
| `args` | string[] | No | null | Command-line arguments |

**Returns:** JSON `{path, pid}`.

### `get_system_info`

Return OS version, CPU, RAM, disk space, and display resolution.

No parameters. **Returns:** JSON with system information.

### `find_ui_element`

Find elements by text, role, or name (case-insensitive substring). At least one search parameter required.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | No | null | Text to search |
| `role` | string | No | null | UI role: `Button`, `TextBox`, `CheckBox`, `MenuItem`, etc. |
| `name` | string | No | null | Accessible name (takes precedence over `text`) |
| `windowHandle` | long | No | null | Window handle (`null` = foreground) |

**Returns:** JSON array of matching elements with `role`, `name`, `value`, `x`, `y`, `width`, `height`.

### `get_accessibility_tree`

Get UI element tree for the foreground window.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 3 | Max tree depth (1–10) |
| `maxElements` | int | No | 500 | Max elements (1–2000) |

**Returns:** JSON tree `{role, name, value, x, y, width, height, children[...]}`.

---

## Browser Tools (36)

> The browser is **Microsoft Edge**. It launches automatically on the first browser tool call.

### `browser_navigate`

Navigate to a URL and wait for the page to load.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | — | Full URL including protocol |
| `waitUntil` | string | No | `"load"` | Wait condition: `load`, `networkidle0`, `networkidle2` |

**Returns:** Text confirmation.

### `browser_back` / `browser_forward` / `browser_reload`

No parameters. Navigate history or reload. **Returns:** Text confirmation.

### `browser_get_url`

No parameters. **Returns:** Current page URL as plain string.

### `browser_get_title`

No parameters. **Returns:** Current page title as plain string.

### `browser_get_text`

No parameters. **Returns:** Visible page text (truncated at 512 KB).

### `browser_get_html`

No parameters. **Returns:** Full page HTML source (truncated at 512 KB).

### `browser_click`

Click a DOM element by CSS selector. More reliable than coordinate-based `click` for browser content.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector (e.g. `#submit-btn`) |
| `button` | string | No | `"Left"` | `Left`, `Right`, `Middle`, `Backward`, `Forward` |
| `clickCount` | int | No | 1 | 1=single, 2=double, 3=triple |

**Returns:** Text confirmation.

### `browser_type`

Type text into a DOM element by CSS selector. Clears existing text by default.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector of input element |
| `text` | string | Yes | — | Text to type |
| `clear` | bool | No | true | Clear existing text before typing |

**Returns:** Text confirmation.

### `browser_query_text`

Return `innerText` of the first element matching a CSS selector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | — | CSS selector (e.g. `h1`, `#content`, `.message`) |

**Returns:** `innerText` of first matching element, empty if no match.

### `browser_wait_for`

Wait for a DOM element matching CSS selector to appear (default 5s, max 30s).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector to wait for |
| `timeoutMs` | int | No | 5000 | Timeout in ms (max 30000) |
| `visible` | bool | No | false | Wait for element to be visible, not just in DOM |

**Returns:** Text confirmation that element appeared, or error on timeout.

### `browser_eval_js`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `expression` | string | Yes | — | JavaScript expression returning a string |

**Returns:** String result of evaluated expression.

### `browser_list_tabs`

No parameters. **Returns:** JSON array `[{tabId, title, url}]`.

### `browser_switch_tab` / `browser_close_tab`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `tabId` | string | Yes | — | Tab ID from `browser_list_tabs` |

**Returns:** Text confirmation. `browser_close_tab` fails if it is the last remaining tab.

### `browser_new_tab`

Open a new tab, optionally navigating to a URL.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | No | null | URL to open (blank if omitted) |

**Returns:** Text confirmation.

### `browser_create_tabs`

Open multiple tabs at once.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `urls` | string[] | Yes | — | URLs to open, one per tab |
| `foregroundIndex` | int | No | null | Index of tab to bring to foreground after creation |

**Returns:** Text confirmation.

### `browser_screenshot`

No parameters. Captures browser viewport only (not full screen). **Returns:** MCP image content block (base64 PNG).

### `browser_select_option`

Select one or more options in a `<select>` element by their `value` attribute.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector for the `<select>` element |
| `values` | string[] | Yes | — | Option value(s) to select |

**Returns:** Text confirmation with count of selected options.

### `browser_fill_form`

Fill multiple form fields at once. Each entry is a `{selector, value}` pair. Stops on first failure and reports which fields succeeded.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `fields` | object[] | Yes | — | Array of `{selector, value}` pairs |

**Returns:** Text confirmation with count of filled fields.

### `browser_drag`

Drag a source element onto a target element, both identified by CSS selector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourceSelector` | string | Yes | — | CSS selector of the drag source |
| `targetSelector` | string | Yes | — | CSS selector of the drop target |

**Returns:** Text confirmation.

### `browser_pdf_save`

Save the current page as PDF under `%USERPROFILE%` or `%TEMP%` only.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filePath` | string | Yes | — | Destination file path under `%USERPROFILE%` or `%TEMP%`. Use forward slashes. |

**Returns:** Text confirmation with saved file path.

### `browser_handle_dialog`

Accept or dismiss a pending browser dialog (alert, confirm, prompt, beforeunload). Returns "No dialog pending" if none active.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `action` | string | Yes | — | `accept` or `dismiss` |
| `promptText` | string | No | null | Text for prompt dialogs (ignored for alert/confirm) |

**Returns:** Text confirmation indicating dialog action taken.

### `browser_snapshot`

Capture accessibility tree with ref IDs (e.g. `e5`) that map to DOM nodes. Use refs with `browser_click_ref`, `browser_type_ref`, `browser_hover_ref`, `browser_keypress_ref`, `browser_scroll_ref`, `browser_set_file_input_ref`. Refs expire on navigation.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 5 | Maximum tree depth 1–10 |
| `includeIframes` | bool | No | true | Include cross-origin iframes |
| `redactValues` | bool | No | true | Redact values in text inputs |

**Returns:** JSON with accessibility snapshot and ref IDs.

### `browser_click_ref`

Click element by ref ID from `browser_snapshot`. Verifies nothing overlays it (hit-test). Fails if snapshot expired — retake with `browser_snapshot`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) from snapshot nodes |
| `button` | string | No | `"Left"` | Left, Right, Middle |
| `clickCount` | int | No | 1 | 1=single, 2=double |

**Returns:** Text confirmation with coordinates.

### `browser_type_ref`

Type text into element by ref ID from `browser_snapshot`. Focuses element first, clears text by default. Fails if snapshot expired.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) from snapshot nodes |
| `text` | string | Yes | — | Text to type |
| `clear` | bool | No | true | Clear existing text first |

**Returns:** Text confirmation with character count.

### `browser_hover_ref`

Hover over element by ref ID from `browser_snapshot`. Returns immediately. Fails if snapshot expired — retake with `browser_snapshot`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) from snapshot nodes |

**Returns:** Text confirmation with coordinates.

### `browser_keypress_ref`

Press a single key on element by ref. Focuses element first. Supports modifiers.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) |
| `key` | string | Yes | — | Key name: `Enter`, `Escape`, `Tab`, `ArrowUp`/`Down`, `F1`–`F12`, etc. |
| `modifiers` | string[] | No | null | Modifier keys: `Ctrl`, `Shift`, `Alt`, `Meta` |

**Returns:** Text confirmation.

### `browser_scroll_ref`

Scroll element into view by ref, optionally scroll by delta within element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) |
| `deltaX` | int | No | 0 | Horizontal scroll delta in pixels |
| `deltaY` | int | No | 0 | Vertical scroll delta in pixels |

**Returns:** Text confirmation.

### `browser_set_file_input_ref`

Set files on a file input element by ref. Files must exist in Documents, Downloads, Desktop, or TEMP.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref for the file input |
| `filePaths` | string[] | Yes | — | File paths to upload |

**Returns:** Text confirmation.

### `browser_get_page_state`

Get multiple page state fields in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `fields` | string[] | Yes | — | Fields to return: `url`, `title`, `dom`, `screenshot`, `tabs` |

**Returns:** Object with requested fields.

### `browser_get_cookies`

Get cookies for current page or specified URLs. Values are always redacted for security.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `urls` | string[] | No | null | URLs to get cookies for (omit for current page) |

**Returns:** Array of cookie objects with redacted values.

### `browser_set_cookies`

Set cookies on the current page's domain. Adds/overwrites — does not clear existing cookies.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `cookies` | object[] | Yes | — | Array of cookie objects: `{name(req), value(req), domain, path, secure, httpOnly, sameSite}` |

**Returns:** Text confirmation.

### `browser_execute_batch`

Execute multiple browser actions sequentially. Stops on first failure.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `actions` | object[] | Yes | — | Array of `{action, params}`. Allowed: `navigate`, `snapshot`, `click_ref`, `type_ref`, `hover_ref`, `scroll_ref`, `keypress_ref`, `wait_for`, `eval_js`. |

**Returns:** Array of results per action.

### `focus_browser`

Focus a browser window (Edge, Chrome, Firefox). Optionally filter by URL or title substring.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string | No | null | URL or title substring (omit to focus any browser) |

**Returns:** Text confirmation.

---

## Tool Families Summary

| Family | Count | Description |
|--------|-------|-------------|
| Desktop Interaction | 26 | Mouse, keyboard, screenshots, windows, shell, clipboard, processes, accessibility |
| Browser Automation | 36 | Navigation, DOM interaction, tabs, forms, snapshots, ref-based actions, cookies, batch, PDF |

## Next Steps

- [API Reference](./api-reference.md) — endpoint details
- [Quick Start](./quickstart.md) — copy-paste Python example
- [Screen Sharing](./screen-sharing.md) — human observation
