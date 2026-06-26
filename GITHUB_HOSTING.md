# Hosting Sophia's SPS Patcher On GitHub

This package can be hosted as a VPM/VCC package from GitHub.

## Recommended GitHub Setup

Use one repository:

```text
https://github.com/sophia1000/Sophias_sps_patches
```

This repo holds the package source, creates GitHub Releases with a `.zip` package, and publishes a VPM listing with GitHub Pages.

VRChat recommends GitHub because VCC reads package listings from a public JSON URL, and GitHub Pages can host that listing.

## Package Repo

The package repo is:

```text
https://github.com/sophia1000/Sophias_sps_patches
```

The package URL in `package.json` points to this release asset:

```text
https://github.com/sophia1000/Sophias_sps_patches/releases/download/1.0.0/com.sophia.sps-patcher-1.0.0.zip
```

## Making A Release

The repo includes a GitHub Action:

```text
.github/workflows/release.yml
```

To make the first release:

1. Push this package to GitHub.
2. Go to `Actions`.
3. Run `Build Release`.

It creates a release tagged from the `version` in `package.json`, and uploads:

```text
com.sophia.sps-patcher-1.0.0.zip
package.json
```

The zip should contain `package.json` at the root of the zip.

## VCC Listing

The repo includes a GitHub Pages listing action:

```text
.github/workflows/build-listing.yml
```

The listing action reads this file:

```text
source.json
```

The workflow generates `Website/index.json` directly from `source.json` and `package.json`, then publishes that folder with GitHub Pages.

In GitHub repo settings:

1. Open `Settings > Pages`.
2. Set `Build and deployment > Source` to `GitHub Actions`.
3. Run `Build Repo Listing`, or let it run after `Build Release`.

Users add this URL to VCC:

```text
https://sophia1000.github.io/Sophias_sps_patches/index.json
```

## Updating Later

For each update:

1. Change `version` in `package.json`.
2. Change the `url` in `package.json` to match the new version zip.
3. Update `CHANGELOG.md`.
4. Create a new GitHub Release with the new zip.

Never delete old releases after publishing them. Existing projects may still need those versions.
