# MCP Tool Reference

Windows 365 for Agents ships **37 built-in tools**, invoked via `tools/call` over the [MCP endpoint](./api-reference.md#mcp-model-context-protocol). Coordinates use screen pixels with `(0, 0)` at top-left. Discover tools at runtime via `tools/list`.

---

## Desktop Tools (17)

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

### `analyze_screen`

> Coming Soon

OCR the screen. No parameters. **Returns:** JSON `{fullText, averageConfidence, boxes[{text, confidence, x, y, width, height}], width, height}`.

### `get_screen_size`

> Coming Soon

No parameters. **Returns:** JSON `{width, height}`.

### `list_windows`

No parameters. **Returns:** JSON array `[{title, processName, handle, x, y, width, height}]`.

### `activate_window`

Bring window to foreground by fuzzy title match.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `titlePattern` | string | Yes | — | Partial title (case-insensitive substring) |

### `focus_browser`

Focus a browser window (Edge, Chrome, Firefox).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string | No | null | URL or title substring (omit for any browser) |

### `close_window`

Graceful close. Protected system processes cannot be closed.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `titlePattern` | string | Yes | — | Partial title (80% match threshold) |

**Returns:** JSON `{matchedTitle, processName, closed}`.

### `execute_shell_command`

Run a whitelisted shell command.

**Allowed commands:** `git`, `npm`, `dotnet`, `python`, `cargo`, `node`, `pip`, `dir`, `mkdir`, `del`, `copy`, `move`, `robocopy`, `findstr`, `where`, `type`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `command` | string | Yes | — | Command to execute |
| `cwd` | string | No | null | Working directory |
| `timeoutMs` | int | No | 30000 | Timeout in ms (max 30000) |

**Returns:** JSON `{stdout, stderr, exitCode, success, timedOut, resourceLimitsApplied}`.

> stdout/stderr truncated at 32 KB.

**Blocked patterns:** Shell metacharacters (`` \`;&<> ``), `%VAR%` expansion, interpreter eval (`python -c`, `node -e`), `git config --global`, `npm -g`, path-prefixed executables, `rm -rf`, `sudo`, disk/system commands. Use `execute_python_code` for arbitrary computation.

### `execute_python_code`

Execute Python code in a sandboxed environment with resource limits.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `code` | string | Yes | — | Python code (max 262,144 chars) |
| `cwd` | string | No | null | Working directory |
| `timeoutMs` | int | No | 30000 | Timeout in ms (max 30000) |

**Returns:** Same schema as `execute_shell_command`.

### `wait_milliseconds`

One-shot pause. Do NOT loop — use `browser_wait_for` for polling. Clamped to [0, 5000].

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `ms` | int | Yes | — | Duration in ms (max 5000) |

---

## Browser Tools (18)

> The browser is **Microsoft Edge**. It launches automatically on the first browser tool call.

### `browser_navigate`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | — | Full URL including protocol |

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

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector (e.g. `#submit-btn`) |

More reliable than coordinate-based `click` for browser content. **Returns:** Text confirmation.

### `browser_type`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector of input element |
| `text` | string | Yes | — | Text to type |

**Returns:** Text confirmation.

### `browser_query_text`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector |

**Returns:** Text content of first matching element.

### `browser_wait_for`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector to wait for |
| `timeoutMs` | int | No | 5000 | Timeout in ms (max 30000) |

**Returns:** Text confirmation that element appeared, or error on timeout.

### `browser_eval_js`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `expression` | string | Yes | — | JavaScript expression returning a string |

**Returns:** String result of evaluated expression.

### `browser_list_tabs`

No parameters. **Returns:** JSON array `[{index, title, url}]`.

### `browser_switch_tab` / `browser_close_tab`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `tabIndex` | int | Yes | — | 0-based tab index |

**Returns:** Text confirmation.

### `browser_new_tab`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | No | null | URL to open (blank if omitted) |

**Returns:** JSON `{index, title, url}`.

### `browser_screenshot`

No parameters. Captures browser viewport only (not full screen). **Returns:** MCP image content block (base64 PNG).

---

## Accessibility Tools (2)

> Coming Soon

### `get_accessibility_tree`

Get UI element tree for the foreground window.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 3 | Max tree depth (1–10) |
| `maxElements` | int | No | 500 | Max elements (1–2000) |

**Returns:** JSON tree `{role, name, value, x, y, width, height, children[...]}`.

### `find_ui_element`

Find elements by text, role, or name (case-insensitive substring). At least one search parameter required.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | No | null | Text to search |
| `role` | string | No | null | UI role: `Button`, `TextBox`, `CheckBox`, `MenuItem`, etc. |
| `name` | string | No | null | Accessible name (takes precedence over `text`) |
| `windowHandle` | long | No | null | Window handle (`null` = foreground) |

**Returns:** JSON array of matching elements with `role`, `name`, `value`, `x`, `y`, `width`, `height`.

---

## Tool Families Summary

| Family | Count | Status |
|--------|-------|--------|
| Desktop Interaction | 17 | Available (`analyze_screen`, `get_screen_size` coming soon) |
| Browser Automation | 18 | Coming Soon |
| UI Accessibility | 2 | Coming Soon |

## Next Steps

- [API Reference](./api-reference.md) — endpoint details
- [Quick Start](./quickstart.md) — copy-paste Python example
- [Screen Sharing](./screen-sharing.md) — human observation
