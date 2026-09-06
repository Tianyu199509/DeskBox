# README and Release Images

This folder keeps public README and release images. Use lowercase ASCII names so links work reliably across GitHub, Windows, and scripts.

## Structure

```text
brand/                       Brand and product-level images
screenshots/zh-cn/           Chinese UI screenshots
screenshots/en-us/           English UI screenshots
```

## Naming Rules

- Put localized screenshots under `screenshots/zh-cn/` or `screenshots/en-us/`.
- Keep product-wide assets under `brand/`.
- Descriptive localized filenames are allowed when they match the corresponding settings section.
- Remove replaced and throwaway captures once the current README assets are verified.

## Current Assets (1.3.7 hero series)

```text
brand/readme-hero-1-3-7-dark-en.png
brand/readme-hero-1-3-7-dark-zh-cn.png
brand/logo-200.png
screenshots/zh-cn/… (one file per settings section)
screenshots/en-us/… (one file per settings section)
```

The `readme-hero-1-3-7-dark-*` banners are AI-generated brand illustrations and are not presented as literal UI screenshots. Files under `screenshots/` are captures from the running DeskBox build in both supported README languages. The five 1.3.4 hero candidates were removed on 2026-09-06 after the 1.3.7 banners were adopted; check git history if an old candidate is ever needed.
