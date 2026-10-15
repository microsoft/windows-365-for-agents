# MCP Tool Reference

The `mcp_W365ComputerUse` server exposes **72 tools**: 3 session-management tools plus 69 Computer-Use tools (28 desktop, 41 browser). All are invoked via `tools/call` through the [A365 tooling gateway (ATG)](./api-reference.md#how-you-talk-to-the-atg). Discover the live tool set at runtime via `tools/list`.

> **Authoritative reference:** [Windows 365 for Agents MCP server](https://learn.microsoft.com/en-us/microsoft-copilot-studio/mcp-windows-365-agents)
> on Microsoft Learn documents every tool and its parameters.

> **Browser automation runs on Microsoft Edge.** Edge launches automatically on the first browser tool call. `focus_browser` can also bring Chrome or Firefox to the foreground, but DOM-level browser tools only work on the Edge instance.

### Coordinates and paths

- **Desktop tools** use screen pixels, with `(0, 0)` at the top-left of the screenshot space.
- **Browser coordinate tools** (`browser_click_xy`, `browser_scroll_xy`) use the browser content viewport in CSS pixels, not the desktop or the address bar. Desktop and browser coordinates are not interchangeable.
- **File paths** refer to the Cloud PC, not the MCP client's local filesystem. In JSON, either use forward slashes or escape backslashes (for example `"C:\\Users\\Example\\Documents\\report.pdf"`).
- Most non-image tools return text. When that text contains JSON, parse it to get the payload; not every tool returns a native structured object.

### Choosing an interaction method

| Task | Recommended starting point |
|------|----------------------------|
| Inspect a Windows application | `get_accessibility_tree` or `find_ui_element`; use `analyze_screen` when OCR is needed |
| Inspect a web page | `browser_snapshot` for element refs; `browser_get_text` for readable page text |
| Interact with a known web element | CSS-selector tools such as `browser_click` and `browser_type`, or the ref-based tools |
| Interact from a browser image | `browser_set_viewport`, then `browser_screenshot`, then `browser_click_xy` and the other browser input tools |
| Interact with browser chrome or a non-browser app | Desktop tools such as `click`, `type_text`, and `press_keys` |
| Wait for dynamic web content | `browser_wait_for`, rather than repeated `wait_milliseconds` calls |
| Reduce browser round trips | `browser_get_page_state` or `browser_execute_batch` |

---

## Session Management (3)

Every Computer-Use session starts with `mcp_W365ComputerUse_StartSession` and should end with `mcp_W365ComputerUse_EndSession`. The `sessionId` returned at start is passed on subsequent calls. See [Session Lifecycle](./sessions.md) and [API Reference](./api-reference.md).

### `mcp_W365ComputerUse_StartSession`

Starts a Windows 365 Computer Use session, establishing a connection to a Cloud PC and allocating a Cloud PC resource. Returns the `sessionId` used to manage the session.

No required parameters.

**Returns:** JSON including `sessionId` (and, on start, a screen-share URL for [Computer-See](./screen-sharing.md)).

### `mcp_W365ComputerUse_GetSessionDetails`

Returns metadata for one Computer Use session identified by the `sessionId`. Does not list multiple sessions.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sessionId` | string | Yes | - | The `sessionId` returned by `mcp_W365ComputerUse_StartSession` |

### `mcp_W365ComputerUse_EndSession`

Ends an active Computer Use session and releases the associated Cloud PC resources back to the pool.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sessionId` | string | Yes | - | The `sessionId` returned by `mcp_W365ComputerUse_StartSession` |

> Always end sessions explicitly. Idle sessions are reclaimed automatically after about 30 minutes of inactivity.

---

## Desktop Tools (28)

Includes 26 desktop-control tools and 2 desktop accessibility tools (`find_ui_element`, `get_accessibility_tree`).

### `move_mouse`

Move cursor to screen position. Use `click` instead if you intend to click.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | - | X in screen pixels |
| `y` | int | Yes | - | Y in screen pixels |

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

No parameters. **Returns:** JSON `{x, y}`.

### `drag_mouse`

Drag from start to end. Also useful for pixel-precise scrolling.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `startX` | int | Yes | - | Start X |
| `startY` | int | Yes | - | Start Y |
| `endX` | int | Yes | - | End X |
| `endY` | int | Yes | - | End Y |
| `button` | string | No | `"Left"` | `Left`, `Right`, `Middle`, `Backward`, `Forward` |

**Returns:** Text confirmation.

### `scroll`

Scroll at a screen position in wheel notches (NOT pixels). Positive `scrollY` = down, positive `scrollX` = right. Each delta is clamped to [-20, 20]. For pixel-based scrolling inside the browser, use `browser_scroll_xy`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | - | Scroll position X |
| `y` | int | Yes | - | Scroll position Y |
| `scrollX` | int | No | 0 | Horizontal notches (positive = right) |
| `scrollY` | int | No | 0 | Vertical notches (positive = down) |

**Returns:** Text confirmation.

### `type_text`

Enter text into the focused desktop field. By default, text is pasted via the clipboard, and the previous text or image clipboard content is restored on a best-effort basis. For shortcuts use `press_keys`. For browser form fields prefer `browser_type` or `browser_type_text`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | - | Text to type |
| `usePaste` | bool | No | true | Paste via the clipboard. Set `false` to send characters individually and leave the clipboard untouched (can be unreliable over remote sessions). |

**Returns:** Text confirmation with character count.

### `press_keys`

Press key combination simultaneously. E.g. `["ctrl", "c"]`, `["alt", "tab"]`. For keys inside the browser page, use `browser_press_keys`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `keys` | string[] | Yes | - | Key names to press together |

**Returns:** Text confirmation.

### `take_screenshot`

Capture full screen or cropped region as PNG.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | No | null | Crop left edge |
| `y` | int | No | null | Crop top edge |
| `width` | int | No | null | Crop width (must be positive) |
| `height` | int | No | null | Crop height (must be positive) |

> All four crop params must be provided together or all omitted.

**Returns:** MCP image content block (`image/png`), not a file path.

### `zoom_region`

Capture a screen region at native resolution as PNG. Use to inspect small text or dense UI. The region is not enlarged.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | - | Left edge X in screen pixels |
| `y` | int | Yes | - | Top edge Y in screen pixels |
| `width` | int | Yes | - | Width in pixels (must be positive) |
| `height` | int | Yes | - | Height in pixels (must be positive) |

> Max region is an **area** limit of 2,073,600 pixels (`width × height`, equivalent to 1920x1080), not independent width and height caps.

**Returns:** MCP image content block (`image/png`).

### `analyze_screen`

OCR the full screen. No parameters. **Returns:** JSON `{text, confidence, boxes[{text, x, y, width, height, confidence}], imageWidth, imageHeight}`. No image block.

### `list_windows`

List visible windows. No parameters. **Returns:** JSON array `[{handle, title, processName, isBrowser, isVisible, x, y, width, height}]`. Bounds can be null when unavailable.

### `activate_window`

Bring window to foreground by fuzzy title match (case-insensitive, 80% match threshold).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | - | Window title to match |

**Returns:** Matched or not-found text status.

> Foreground activation is best-effort under Windows focus restrictions. A success message does not guarantee that keyboard focus moved.

### `focus_browser`

Bring a recognized browser window (Edge, Chrome, Firefox) to the foreground. Optionally select the window by fuzzy **window-title** match.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string | No | null | Window-title pattern (omit to focus any browser) |

**Returns:** Text status.

> `focus_browser` matches window titles; it does not search tab URLs. Focus is best-effort, the same as `activate_window`.

### `close_window`

Request graceful closure (`WM_CLOSE`) of a window by fuzzy title match. Protected system processes cannot be closed.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | - | Partial title (80% match threshold) |

**Returns:** JSON `{closed, matchedTitle, processName, reason}`.

### `resize_window`

Resize, move, maximize, minimize, or restore a window by fuzzy title match.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | - | Window title to match (case-insensitive fuzzy match) |
| `action` | string | Yes | - | Action: `Resize`, `Move`, `Maximize`, `Minimize`, `Restore` |
| `x` | int | No | null | Left edge X (for Resize/Move) |
| `y` | int | No | null | Top edge Y (for Resize/Move) |
| `width` | int | No | null | Width (for Resize) |
| `height` | int | No | null | Height (for Resize) |

Omitted bounds keep the current or restored values. `Maximize`, `Minimize`, and `Restore` do not need bounds. This resizes the OS window; to change the browser content size, use `browser_set_viewport`.

**Returns:** Text confirmation.

### `get_screen_size`

No parameters. **Returns:** JSON `{width, height}`.

### `execute_shell_command`

Run an allowed shell command through `cmd.exe` (not PowerShell), with validated piping (`|`) and chaining (`&&`, `||`, `&`). The default allowlist includes development tools such as `git`, `npm`, `dotnet`, `python`, `node`, `cargo`, and `pip`, plus common file, text, and diagnostic commands. Each command segment is validated; an allowlisted executable does not make every flag acceptable.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `command` | string | Yes | - | Command to execute |
| `cwd` | string | No | null | Working directory. Use forward slashes (`C:/Users/me/project`). |
| `timeoutMs` | int | No | 30000 | Timeout in ms. Capped at the configured ceiling (120000 by default); zero or negative uses the ceiling. |

**Returns:** JSON `{stdout, stderr, exitCode, success, timedOut, resourceLimitsApplied}`.

> stdout and stderr are each truncated at 32,768 characters by default. A rejected command can come back as a JSON result rather than an MCP error: check `success` and `exitCode`.

### `execute_python_code`

Execute Python code in a subprocess. Python must be installed and discoverable on the Cloud PC. The default contained-execution memory limit is 512 MiB per process; check `resourceLimitsApplied`, because execution can fall back to running without those limits.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `code` | string | Yes | - | Python code (max 262,144 chars) |
| `cwd` | string | No | null | Working directory. Use forward slashes. |
| `timeoutMs` | int | No | 30000 | Timeout in ms. Capped at the configured ceiling (120000 by default). |

**Returns:** JSON `{stdout, stderr, exitCode, success, timedOut, resourceLimitsApplied}`.

> stdout and stderr are each truncated at 32,768 characters by default. A missing Python installation is reported in the JSON result, not necessarily as an MCP error.

### `wait_milliseconds`

One-shot pause. Do NOT loop; use `browser_wait_for` for polling. Clamped to [0, 5000].

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `ms` | int | Yes | - | Duration in ms (max 5000) |

**Returns:** The actual (clamped) wait duration.

### `clipboard_read`

Read system clipboard content. No parameters.

**Returns:** JSON `{format, text, imageBase64}`; `text` or `imageBase64` is populated depending on the format.

### `clipboard_write`

Write text to the system clipboard, replacing current content.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | - | Text to write to clipboard |

**Returns:** Text confirmation with character count.

### `list_processes`

List running processes (current Windows session only). Use `startTimeTicks` with `kill_process` to prevent PID recycling.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxCount` | int | No | 200 | Maximum number of processes to return (clamped to 1 to 500) |

**Returns:** JSON array `[{pid, name, sessionId, memoryMb, windowTitle, startTimeTicks}]`.

### `kill_process`

Terminate a process by PID. Requires the start time from `list_processes` to prevent killing a recycled PID. Protected-process and session restrictions apply.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pid` | int | Yes | - | Process ID from `list_processes` |
| `startTime` | long | Yes | - | The `startTimeTicks` value from `list_processes`, passed as the exact integer |
| `force` | bool | No | false | Force kill without graceful shutdown |

**Returns:** JSON `{pid, name, killed, reason}`.

### `launch_application`

Launch a GUI application from allowed directories. Default allowed roots are `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%SystemRoot%`, and `%UserProfile%`; executable-name restrictions still apply. Use `execute_shell_command` for CLI commands.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `path` | string | Yes | - | Absolute path to the executable. Use forward slashes (`C:/Program Files/app.exe`). |
| `args` | string[] | No | null | Command-line arguments |

**Returns:** JSON `{pid, processName, path}`.

### `desktop_reset`

Return the desktop to a clean working state. Force-terminates eligible processes started during the session (after the MCP server started), excluding the agent stack and protected processes, and clears modern Notepad session-restore entries.

No parameters.

**Returns:** JSON `{succeededCount, failedCount, sweeps}`. Counts include both process cleanup and Notepad cleanup work items; check `failedCount`.

> Unsaved work, including unsaved Notepad tab text, is discarded. `desktop_reset` does not reboot or reimage the Cloud PC, and it does not erase saved files, cookies, or other on-disk state. It is process cleanup, not a privacy scrub.

### `get_system_info`

Return OS version, CPU count, RAM, free disk space, and display resolution.

No parameters. **Returns:** JSON `{os, cpuCount, ramGb, diskFreeGb, displayWidth, displayHeight}`.

### `find_ui_element`

Find Windows UI Automation elements in the foreground window or a specified window. Provide at least `name`/`text` or `role`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | No | null | Text to search (case-insensitive substring) |
| `role` | string | No | null | UI role, case-insensitive **exact** match: `Button`, `TextBox`, `CheckBox`, `MenuItem`, etc. |
| `name` | string | No | null | Accessible name, case-insensitive substring (takes precedence over `text`) |
| `windowHandle` | long | No | null | Window handle from `list_windows` (`null` = foreground) |

**Returns:** JSON array of up to 50 matching elements with `role`, `name`, `value`, `automationId`, `isInteractive`, `isPassword`, `processName`, `x`, `y`, `width`, `height`.

> `x`/`y` are the **top-left** of the bounding rectangle, not the clickable center. Compute the center as `x + width / 2`, `y + height / 2` before calling `click`. Password values are suppressed.

### `get_accessibility_tree`

Get the Windows UI Automation element tree for the foreground window.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 3 | Max tree depth (1 to 10) |
| `maxElements` | int | No | 500 | Max elements (1 to 2000) |

**Returns:** JSON tree `{role, name, value, automationId, isInteractive, isPassword, processName, x, y, width, height, truncated, children[...]}`.

---

## Browser Tools (41)

> The browser is **Microsoft Edge**. It launches automatically on the first browser tool call.

### `browser_navigate`

Navigate to a URL and wait for the page to load.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | - | Absolute `http` or `https` URL |
| `waitUntil` | string | No | `"load"` | Wait condition: `load`, `networkidle0`, `networkidle2`. The network-idle options help with client-rendered pages. |
| `timeoutMs` | int | No | 30000 | Navigation timeout in ms (clamped to 1000 to 120000) |

**Returns:** Text confirmation.

### `browser_back` / `browser_forward` / `browser_reload`

No parameters. Navigate history or reload. **Returns:** Text confirmation.

### `browser_set_viewport`

Set the browser content viewport size in CSS pixels, with a device scale factor of 1. Applies browser-wide, to existing tabs and tabs created afterward. Use before `browser_screenshot` when you plan to act on screenshot coordinates.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `width` | int | Yes | - | Viewport width in CSS pixels |
| `height` | int | Yes | - | Viewport height in CSS pixels |

**Returns:** Text confirmation.

> The viewport is applied per tab and an individual tab can fail. A success message does not verify every tab's dimensions; check the resulting screenshot before relying on its coordinates. This is different from resizing the OS window with `resize_window`.

### `browser_get_url`

No parameters. **Returns:** Current page URL (including query string and fragment) as plain string.

### `browser_get_title`

No parameters. **Returns:** Current page title as plain string.

### `browser_get_text`

No parameters. **Returns:** Readable page-body text (truncated at 524,288 characters).

### `browser_get_html`

No parameters. **Returns:** Full page HTML source (truncated at 524,288 characters).

### `browser_find_text`

Search the page text (case-insensitive) and return matching elements with a CSS selector for each.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | - | Text to search for |
| `maxResults` | int | No | 5 | Maximum matches to return (clamped to 1 to 25) |

**Returns:** JSON array `[{selector, text}]`, with `text` previews capped at 200 characters.

### `browser_click`

Click a DOM element by CSS selector. More reliable than coordinate-based `click` for browser content.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | - | CSS selector (e.g. `#submit-btn`) |
| `button` | string | No | `"Left"` | `Left`, `Right`, `Middle` |
| `clickCount` | int | No | 1 | 1=single, 2=double, 3=triple |

**Returns:** Text confirmation.

### `browser_type`

Type text into a DOM element by CSS selector. Clears existing text by default.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | - | CSS selector of input element |
| `text` | string | Yes | - | Text to type |
| `clear` | bool | No | true | Clear existing text before typing |

**Returns:** Text confirmation with character count and, when available, the resulting value length.

### `browser_query_text`

Return the content of the first element matching a CSS selector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | - | CSS selector (e.g. `h1`, `#content`, `.message`) |
| `format` | string | No | `"text"` | `text` returns `textContent`; `html` returns `outerHTML` |

**Returns:** Content of the first matching element, empty string if no match.

### `browser_wait_for`

Wait for a DOM element matching CSS selector to appear (default 5s, max 30s).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | - | CSS selector to wait for |
| `timeoutMs` | int | No | 5000 | Timeout in ms (clamped to 100 to 30000) |
| `visible` | bool | No | false | Wait for element to be visible, not just in DOM |

**Returns:** Text confirmation that element appeared, or error on timeout.

### `browser_eval_js`

Evaluate a JavaScript expression in the page.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `expression` | string | Yes | - | JavaScript expression to evaluate |

**Returns:** Strings are returned as text; other serializable values are returned as JSON text.

> A JavaScript failure can be returned as text beginning `JavaScript error:` rather than as an MCP error. Check the returned text.

### `browser_list_tabs`

No parameters. **Returns:** JSON array `[{tabId, title, url}]`.

### `browser_switch_tab` / `browser_close_tab`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `tabId` | string | Yes | - | Tab ID from `browser_list_tabs` |

**Returns:** Text confirmation. `browser_close_tab` fails if it is the last remaining tab.

### `browser_new_tab`

Open a new tab, optionally navigating to a URL.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | No | null | URL to open (blank if omitted) |

**Returns:** JSON `{tabId, title, url}`.

### `browser_create_tabs`

Open multiple tabs sequentially. All URLs are validated as absolute `http`/`https` URLs before any tab is created.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `urls` | string[] | Yes | - | URLs to open, one per tab |
| `foregroundIndex` | int | No | null | Zero-based index into `urls` of the tab to bring to the foreground after creation |

**Returns:** JSON `{created: [{tabId, title, url}], failed: {index, url, error}}`; `failed` is present only if creation stopped early.

> Tabs created before a failure are kept (no rollback). Check `browser_list_tabs` before retrying. Omitting `foregroundIndex` does not guarantee that new tabs stay in the background.

### `browser_screenshot`

No parameters. Captures browser page content only, excluding OS and browser chrome. **Returns:** MCP image content block (`image/png`).

### `browser_click_xy`

Click a position in the browser content viewport using browser mouse events. Use with coordinates read from `browser_screenshot`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | - | X in viewport CSS pixels |
| `y` | int | Yes | - | Y in viewport CSS pixels |
| `button` | string | No | `"Left"` | `Left`, `Right`, `Middle` |
| `clickCount` | int | No | 1 | 1=single, 2=double |

**Returns:** Text confirmation.

### `browser_scroll_xy`

Move to a viewport position and scroll by **pixel** deltas (unlike desktop `scroll`, which uses notches). Positive `deltaY` = down.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | - | X in viewport CSS pixels |
| `y` | int | Yes | - | Y in viewport CSS pixels |
| `deltaX` | int | Yes | - | Horizontal scroll in pixels (positive = right) |
| `deltaY` | int | Yes | - | Vertical scroll in pixels (positive = down) |

**Returns:** Text confirmation.

### `browser_press_keys`

Press keys inside the browser page. Keys are pressed down in order and released in reverse, so this supports single keys and chords.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `keys` | string[] | Yes | - | Browser key names, e.g. `["Enter"]`, `["Tab"]`, `["Control", "a"]` |

**Returns:** Text confirmation.

### `browser_type_text`

Type text into the browser's currently focused element, without a selector. Click or focus the target field first (for example with `browser_click_xy`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | - | Text to type |

**Returns:** Text confirmation with character count.

> There is no `clear` option. To replace existing text, select it first (for example `browser_press_keys` with `["Control", "a"]`).

### `browser_select_option`

Select one or more options in a `<select>` element by their `value` attribute.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | - | CSS selector for the `<select>` element |
| `values` | string[] | Yes | - | Option value(s) to select |

**Returns:** Text confirmation with count of selected options.

### `browser_fill_form`

Fill multiple form fields in order, then optionally submit and wait for a result. Each field is cleared before filling. Stops on first failure and reports which field failed and how many earlier fields completed; earlier edits are not rolled back.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `fields` | object[] | Yes | - | Array of `{selector, value}` pairs |
| `submitSelector` | string | No | null | CSS selector of a submit control to click after filling |
| `pressEnterToSubmit` | bool | No | false | Submit the first field's form via `requestSubmit()` (does not send an Enter key) |
| `waitForSelector` | string | No | null | CSS selector to wait up to 5s for after submitting |

> Use either `submitSelector` or `pressEnterToSubmit`, not both. If `waitForSelector` does not appear, the summary reports `result selector appeared: false` rather than raising an error.

**Returns:** Text summary with count of filled fields and, if requested, the result-selector status.

### `browser_drag`

Drag a source element onto a target element, both identified by CSS selector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourceSelector` | string | Yes | - | CSS selector of the drag source |
| `targetSelector` | string | Yes | - | CSS selector of the drop target |

**Returns:** Text confirmation.

### `browser_pdf_save`

Save the current page as PDF under `%USERPROFILE%` or `%TEMP%` only.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filePath` | string | Yes | - | Destination file path under `%USERPROFILE%` or `%TEMP%`. Use forward slashes. |

**Returns:** Text confirmation with saved file path on the Cloud PC (not the PDF bytes or a download URL).

### `browser_handle_dialog`

Accept or dismiss a pending browser dialog (alert, confirm, prompt, beforeunload). Returns "No dialog pending" if none active.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `action` | string | Yes | - | `accept` or `dismiss` |
| `promptText` | string | No | null | Text for prompt dialogs (ignored for alert/confirm) |

**Returns:** Text confirmation indicating dialog action taken.

> Pending dialogs auto-resolve after about 10 seconds: ordinary dialogs are dismissed and beforeunload dialogs are accepted. Respond promptly when the choice matters.

### `browser_snapshot`

Capture the page accessibility tree with ref IDs (e.g. `e5`). Use refs with `browser_click_ref`, `browser_type_ref`, `browser_hover_ref`, `browser_keypress_ref`, `browser_scroll_ref`, `browser_set_file_input_ref`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 5 | Maximum tree depth 1 to 10 |
| `includeIframes` | bool | No | true | Include child-frame content, including cross-origin frames where retrievable |
| `redactValues` | bool | No | true | Replace values of editable controls with length hints |

**Returns:** JSON `{snapshotId, nodeCount, nodes[{ref, role, name, value, focused, selected, checked}]}`. Nodes are a flat list (max 500) with no screen bounds; false state flags are omitted.

> Refs are snapshot-scoped, not CSS selectors or permanent IDs. They expire when the tab navigates, and only the **three most recent snapshots** are kept, so older snapshots can expire without navigation. Retake the snapshot after an expired or unknown-ref error.

### `browser_click_ref`

Click element by ref ID from `browser_snapshot`. Verifies nothing overlays it (hit-test). Fails if snapshot expired; retake with `browser_snapshot`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | - | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | - | Element ref (e.g. `e5`) from snapshot nodes |
| `button` | string | No | `"Left"` | Left, Right, Middle |
| `clickCount` | int | No | 1 | 1=single, 2=double |

**Returns:** Text confirmation.

### `browser_type_ref`

Type text into element by ref ID from `browser_snapshot`. Focuses element first, clears text by default. Fails if snapshot expired.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | - | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | - | Element ref (e.g. `e5`) from snapshot nodes |
| `text` | string | Yes | - | Text to type |
| `clear` | bool | No | true | Clear existing text first |

**Returns:** Text confirmation with character count and, when available, the resulting value length.

### `browser_hover_ref`

Hover over element by ref ID from `browser_snapshot`. Returns immediately. Fails if snapshot expired; retake with `browser_snapshot`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | - | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | - | Element ref (e.g. `e5`) from snapshot nodes |

**Returns:** Text confirmation.

### `browser_keypress_ref`

Press a single key on element by ref. Focuses element first. Supports modifiers. Combinations that open DevTools are blocked.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | - | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | - | Element ref (e.g. `e5`) |
| `key` | string | Yes | - | Key name: `Enter`, `Escape`, `Tab`, arrow keys, `Backspace`, `Delete`, `Home`, `End`, `PageUp`, `PageDown`, `Space`, or a printable character such as `a` |
| `modifiers` | string[] | No | null | Modifier keys: `Ctrl`, `Shift`, `Alt`, `Meta` |

**Returns:** Text confirmation.

### `browser_scroll_ref`

Scroll element into view by ref, optionally scroll by delta within element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | - | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | - | Element ref (e.g. `e5`) |
| `deltaX` | int | No | 0 | Horizontal scroll delta in pixels |
| `deltaY` | int | No | 0 | Vertical scroll delta in pixels |

**Returns:** Text confirmation.

### `browser_set_file_input_ref`

Set files on a file input element by ref. Files must already exist under `%USERPROFILE%\Documents`, `%USERPROFILE%\Downloads`, `%USERPROFILE%\Desktop`, or `%TEMP%`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | - | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | - | Element ref for the file input |
| `filePaths` | string[] | Yes | - | File paths to upload |

**Returns:** Text confirmation with file count.

> Setting the file input does not guarantee the site has submitted or completed the upload.

### `browser_get_page_state`

Get multiple page state fields in one call. Fields are collected one after another, not as an atomic snapshot.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `fields` | string[] | Yes | - | Fields to return (case-insensitive): `url`, `title`, `dom`, `screenshot`, `tabs` |

**Returns:** A JSON text block with the requested `url`, `title`, `dom`, and `tabs`, plus a separate PNG image block when `screenshot` is requested. A screenshot-only request returns only the image block.

> `dom` is an **accessibility snapshot** (depth 5, iframes included, values redacted), not HTML; its refs work with the ref-based tools. Use `browser_get_html` for HTML. Unrecognized fields are ignored.

### `browser_get_cookies`

Get cookies for current page or specified URLs. Values are always redacted for security.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `urls` | string[] | No | null | URLs to get cookies for (omit for current page) |

**Returns:** JSON array `[{name, value, domain, path, httpOnly, secure}]`, where `value` is a redacted length hint.

### `browser_set_cookies`

Set cookies on the current page's domain. Adds/overwrites; does not clear existing cookies. Navigate to an `http`/`https` page first.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `cookies` | object[] | Yes | - | Array of cookie objects: `{name(req), value(req), domain, path, secure, httpOnly, sameSite}` |

Cookie fields: `domain` defaults to the current page host and, if supplied, must match the host or a permitted parent domain (names without a dot, such as `localhost`, are rejected). `path` defaults to `/`. `secure` and `httpOnly` default to `false`. `sameSite` is passed to the browser (for example `"Lax"`).

**Returns:** Text confirmation with count of cookies set.

### `browser_execute_batch`

Execute multiple browser actions sequentially. Stops on first failure; earlier actions are not undone.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `actions` | object[] | Yes | - | Array of `{action, params}`. Allowed: `navigate`, `snapshot`, `click_ref`, `type_ref`, `hover_ref`, `scroll_ref`, `keypress_ref`, `wait_for`, `eval_js`. |

Supported actions and their `params` (action names are case-insensitive; use the exact `params` casing):

| Action | Required `params` | Optional `params` |
|--------|-------------------|-------------------|
| `navigate` | `url` | `waitUntil` (`"load"`), `timeoutMs` (30000) |
| `snapshot` | None | `maxDepth` (5); iframes and redaction always on |
| `click_ref` | `snapshotId`, `ref` | `button` (`"left"`, lowercase), `clickCount` (1, clamped to 1 to 3) |
| `type_ref` | `snapshotId`, `ref`, `text` | `clear` (true) |
| `hover_ref` | `snapshotId`, `ref` | None |
| `scroll_ref` | `snapshotId`, `ref` | `deltaX` (0), `deltaY` (0) |
| `keypress_ref` | `snapshotId`, `ref`, `key` | `modifiers` |
| `wait_for` | `selector` | `timeoutMs` (5000); waits for DOM presence only, no `visible` option |
| `eval_js` | `expression` | None |

**Returns:** JSON `{completed: [{index, action, result}], failed: {index, action, result}}`; `failed` is present only when an action fails. A snapshot action's `result` is itself serialized JSON text.

> Batches have no variable substitution: a later action cannot reference a `snapshotId` or ref produced earlier in the same batch. Take the snapshot in one call (or one batch), then use the returned values in the next call. JavaScript errors from `eval_js` can appear under `completed` as text rather than under `failed`.

---

## Tool Families Summary

| Family | Count | Description |
|--------|-------|-------------|
| Session Management | 3 | Start, inspect, and end a Computer Use session |
| Desktop Interaction | 28 | Mouse, keyboard, screenshots, OCR, windows, shell, Python, clipboard, processes, applications, session reset, and UI accessibility |
| Browser Automation | 41 | Navigation, viewport and coordinate input, page reading and search, DOM interaction, tabs, forms, snapshots, ref-based actions, cookies, batch, PDF |
| **Total** | **72** | |

## Next Steps

- [API Reference](./api-reference.md): endpoint details
- [Getting Started](./getting-started.md): onboarding flow and first call
- [Screen Sharing](./screen-sharing.md): human observation
