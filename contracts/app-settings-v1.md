# app-settings-v1

`app-settings-v1` is the application scope of §26.3: the user's own preferences for InterCat itself, as opposed to
a workspace's view (`.icat-workspace`, not yet defined) or a capture's configuration, which is evidence (I9). It is
documented, versioned and meant to be edited by hand.

## 1. Location

`%APPDATA%\InterCat\settings.json`, the per-user roaming application-data folder, so a preference follows the user's
profile. Sessions stay in the local application-data folder (ADR-027); nothing large is kept here. A test or tool
names its own file; only the Desktop application reads and writes this one.

## 2. JSON contract

A UTF-8 JSON object of at most 1 MiB:

```text
{
  "contract": "app-settings-v1",
  "themeMode": "system" | "dark" | "light" | "high-contrast-dark" | "high-contrast-light",
  "window": {
    "x": <integer>, "y": <integer>, "width": <number above 0>, "height": <number above 0>, "maximized": <boolean>
  }
}
```

| Key | Meaning | When absent |
|---|---|---|
| `contract` | This contract's name. Another value is another version: its settings are not applied, and the file is never changed (§3). | Read as this version. |
| `themeMode` | The theme the Desktop draws. `system` follows the operating system's light or dark and high-contrast settings (§26.2); the others fix a verified token set (§6.1). | `system` |
| `window` | Where InterCat's window stood when it last closed: `x` and `y`, its left and top edges in the screen's pixels; `width` and `height`, its size in device-independent pixels while neither maximized nor minimized; and `maximized`, whether it filled its screen. The window opens there, maximized if it was, wholly inside the screen that shows the part of its title bar a person takes hold of and no larger than that screen, never smaller than the least it takes. A value with any other member, or one this version does not read, is reported. | The window opens where the system places it, at its first size, as it does when no screen shows its title bar. |

## 3. Reading and writing

- **Nothing is dropped.** A key this version does not know is kept as written and reported. A known key with a value
  it cannot read is reported, and that key's default applies; the value stays in the file until the user changes the
  setting.
- **Nothing is overwritten unread.** The file is written only when the user changes a setting. The change replaces
  that one key and keeps every other, and the file is written through a staged copy and a replacing move.
- **The window's place** is the one key written without a choice in the settings menu: as InterCat's window closes,
  when it closed elsewhere than the file places it. It replaces only `window`, as a choice replaces its key, and it is
  never written over a file that cannot be read or another version wrote, which stay as the user left them.
- **A file that cannot be read** (not JSON, not an object, or over the bound) is reported and its defaults apply. If
  the user then changes a setting, the unreadable file is first kept beside the new one as
  `settings.json.unreadable-<UTC time>`, so a hand edit gone wrong is never lost.
- **A file another version wrote** is not applied and never written over, so an older InterCat cannot erase what a
  newer one saved. Changing a setting then lasts only until InterCat closes, and the settings menu says so.
- **Reports** appear in the Desktop's settings menu (the header's *Theme* button), under the choices, together with
  the file's location.

## 4. Not yet defined

Units, the default profile, the update policy and enrichment opt-ins (§26.3) join this contract as keys when the
product has them. Adding a key is not a new version; changing what an existing key means is.
