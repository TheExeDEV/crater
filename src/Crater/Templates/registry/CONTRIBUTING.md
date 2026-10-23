# Contributing

## Rules

- Publish only into the folder named after your GitHub login in lowercase.
- A published version never changes. To fix something, publish a new version.
- Published versions are not deleted. Ask a maintainer if a version must be yanked.
- Packages may depend only on registry packages, not on local folders or git repositories.
- Do not edit `packages.json`, `registry.json` or `.github/`. The catalog is generated after merge.

## What the check verifies

- `index.json` is valid and its versions are valid semantic versions
- every version has its archive and the sha256 matches
- the archive contains `crater.json` with the same name and version
- existing versions and archives were not modified or removed
- every changed file is inside the author's folder

Maintainers listed in `registry.json` may change any file.

## Pull requests

`crater publish --pr` creates one branch and one commit per version. Keep one package version per pull request when possible.
