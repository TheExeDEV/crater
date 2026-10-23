# Contributing

## Rules

- Publish only into the folder named after your GitHub login in lowercase.
- A published version never changes. To fix something, publish a new version.
- Published versions are not deleted. Ask a maintainer if a version must be yanked.
- Packages may depend only on registry packages, not on local folders or git repositories.
- Packages must not contain executables or code that loads other code at runtime (`require(<asset id>)`, `loadstring`).
- Do not edit `packages.json`, `registry.json` or `.github/`. The catalog is generated after merge.

## What the check verifies

- `index.json` is valid and its versions are valid semantic versions
- every version has its archive and the sha256 matches
- the archive contains `crater.json` with the same name and version
- existing versions and archives were not modified or removed
- every changed file is inside the author's folder
- new versions contain no executables

The check also scans the code of every new version for backdoors and obfuscation and lists what it found in the **Summary** of the check run. You can run the same scan before publishing:

```bash
crater registry scan path/to/your/package
```

Maintainers listed in `registry.json` may change any file.

## Review

Nothing is merged automatically. A maintainer reads the code of every pull request, looks at the scan report and decides. Explain anything the scanner flags (for example why your package needs `HttpService`) in the pull request description.

## Pull requests

`crater publish --pr` creates one branch and one commit per version. Keep one package version per pull request when possible.
