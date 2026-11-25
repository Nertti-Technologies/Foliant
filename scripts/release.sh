#!/usr/bin/env bash
# Cut a Foliant release — triggers the ONE-BUTTON release workflow (.github/workflows/release.yml).
# You never tag by hand: the workflow verifies the version equals the TOP CHANGELOG.md entry on main,
# builds, tests, tags main's HEAD itself (MinVer → the version), packs, and publishes to nuget.org.
# This is just a CLI shortcut for GitHub → Actions → "Release to nuget.org" → Run workflow.
#
#   ./scripts/release.sh 1.4.0            # release the main packages
#   ./scripts/release.sh 1.4.0 --dry-run  # build/test/pack only — no tag, no publish (safe rehearsal)
#   ./scripts/release.sh 0.2.0-preview.1  # release Foliant.DocumentExtraction (docext line; run it
#                                         # after the main release, from the same main commit)
#
# Prereqs: GitHub CLI (`gh auth login`), and the `## X.Y.Z` entry already merged to main's CHANGELOG.md.
set -euo pipefail
VER="${1:?usage: release.sh X.Y.Z|X.Y.Z-preview.N [--dry-run]}"
if [[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then PACKAGE=main
elif [[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+$ ]]; then PACKAGE=docext
else echo "FAIL: '$VER' is neither X.Y.Z (main) nor X.Y.Z-preview.N (docext)"; exit 1; fi
command -v gh >/dev/null || { echo "FAIL: GitHub CLI 'gh' not found — 'brew install gh' then 'gh auth login'."; exit 1; }

DRY=false
[ "${2:-}" = "--dry-run" ] && DRY=true

echo "Dispatching release workflow on main: package=$PACKAGE version=$VER dry_run=$DRY …"
gh workflow run release.yml --ref main -f package="$PACKAGE" -f version="$VER" -f dry_run="$DRY"
echo
echo "Dispatched. The workflow verifies version==CHANGELOG, tests, tags, and publishes."
echo "Watch:  gh run watch   (or GitHub → Actions → 'Release to nuget.org')."
