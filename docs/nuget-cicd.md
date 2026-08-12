# NuGet CI/CD

This repository includes a dedicated package release workflow:

- `.github/workflows/nuget-release.yml`

## What The Workflow Does

The workflow is manual and runs from the GitHub Actions UI with `workflow_dispatch`.

It has three jobs in order:

1. Pack NuGet packages
2. Publish to NuGet.org (runs if `publish_nuget = true`)
3. Publish to GitHub Packages (runs if `publish_github = true`)

Both publish jobs depend only on the pack job, so they run independently based on the input toggles.

## Package Project

The workflow packs this project:

- `src/MAUI/Lib/DrawnUi.Maui.Camera.csproj`

Package version is taken from project metadata:

- `<Version>` in `src/MAUI/Lib/DrawnUi.Maui.Camera.csproj`

## Produced Artifacts

The pack job collects from release outputs under `src/**/bin/Release`:

- `*.nupkg`
- `*.snupkg`

The files are uploaded as one artifact:

- `nuget-packages`

## Required Repository Secrets

Create this secret before enabling NuGet.org publish:

- `NUGET_API_KEY`

GitHub Packages publish uses the built-in `GITHUB_TOKEN` from the workflow run.

Important GitHub Packages nuance:

- the built-in `GITHUB_TOKEN` works for publish only when the GitHub Packages package is connected to this repository
- if the package already exists and is linked to another repository, GitHub can return `403 Forbidden` even when the workflow job has `packages: write`
- package metadata should also point to this repository so future publishes stay aligned

## GitHub Settings To Verify

1. GitHub Actions is enabled.
2. Manual workflow dispatch is allowed.
3. Workflow token permissions allow package write access for GitHub Packages.
4. Repository owner and package namespace match expected feed URL.

GitHub Packages feed URL used:

- `https://nuget.pkg.github.com/<repository-owner>/index.json`

## How To Run

1. Open GitHub Actions.
2. Select Publish NuGet Packages workflow.
3. Click Run workflow.
4. Choose publish toggles.
5. Start run.

Recommended first validation run:

1. publish_nuget = false
2. publish_github = false

Then validate publish paths in two follow-up runs.

## Failure Triage

### Pack Fails

Check:

- SDK resolution from `global.json`
- MAUI workload install output
- project path in workflow
- package artifact collection pattern

### NuGet.org Publish Fails

Check:

- `NUGET_API_KEY` exists and is valid
- package version collisions (duplicates are skipped)
- NuGet.org transient issues

### GitHub Packages Publish Fails

Check:

- workflow has `packages: write` on publish-github job
- feed URL owner is correct
- package metadata repository URL and visibility settings
- if the package already exists under GitHub Packages, verify it is linked to this repository
- if package metadata previously pointed at another repository, republish after correcting `RepositoryUrl` and `PackageProjectUrl`
- if the push reaches the GitHub Packages endpoint and fails with `403 Forbidden`, treat repository linkage mismatch as a primary cause, not just missing token scope

## Maintenance Notes

If package projects are added later, extend the `$projects` list in the workflow.

If versioning strategy changes, keep dynamic package collection and avoid hardcoded version masks.
