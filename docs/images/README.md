# Image Naming

This folder keeps public README and release images. Use lowercase ASCII names so links work reliably across GitHub, Windows, and scripts.

## Structure

```text
brand/                       Brand and product-level images
screenshots/zh-cn/           Chinese UI screenshots
screenshots/en-us/           English UI screenshots
archive/                     Older screenshots kept for reference
```

## Naming Rules

- Use lowercase kebab-case: `settings-general-1-2.png`, `todo-widget.png`.
- Avoid spaces, dates, temporary capture names, and non-ASCII characters in file or folder names.
- Put localized screenshots under `screenshots/zh-cn/` or `screenshots/en-us/`.
- Keep product-wide assets under `brand/`.
- Move replaced but still useful images to `archive/`; remove throwaway captures before release.

## Current Examples

```text
brand/product-cover-zh-cn-1280x720.png
brand/product-cover-en-us-1280x720.png
brand/logo-200.png
screenshots/zh-cn/desktop-light.png
screenshots/zh-cn/todo-widget.png
screenshots/zh-cn/music-widget.png
screenshots/zh-cn/settings-general-1-2.png
screenshots/en-us/desktop-light.png
screenshots/en-us/file-widget.png
screenshots/en-us/settings-feature-widgets-1-2.png
```
