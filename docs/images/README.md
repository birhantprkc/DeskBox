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

- Use lowercase kebab-case: `settings-general.png`, `onboarding-step-1.png`.
- Avoid spaces, dates, temporary capture names, and non-ASCII characters in file or folder names.
- Put localized screenshots under `screenshots/zh-cn/` or `screenshots/en-us/`.
- Keep product-wide assets under `brand/`.
- Move replaced but still useful images to `archive/`; remove throwaway captures before release.

## Current Examples

```text
brand/product-cover-1280x720.png
brand/logo-200.png
screenshots/zh-cn/widget-light.png
screenshots/zh-cn/settings-storage.png
screenshots/en-us/settings-general.png
screenshots/en-us/onboarding-step-1.png
```
