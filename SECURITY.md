# Security policy

## Supported versions

Only the latest release of crater gets security fixes. Update with:

```bash
crater self-update
```

| Version | Supported |
|---|---|
| latest release | yes |
| anything older | no |

## Reporting a vulnerability

Do not open a public issue for a vulnerability.

Report it privately through GitHub: open the [Security tab](https://github.com/TheExeDEV/crater/security) of this repository and press **Report a vulnerability**, or go straight to [a new advisory](https://github.com/TheExeDEV/crater/security/advisories/new). Only the maintainers see the report.

Please include:

- the crater version (`crater --version`) and your system
- what an attacker can do and what they need for it (a published package, a crafted `crater.json`, a pull request to a registry...)
- steps or a small project that shows the problem

You get an answer within 7 days. Once the fix is released, the advisory is published with credit to you, unless you ask not to be named.

## What counts

Problems in crater itself, for example:

- a package archive that writes files outside its folder on install
- a way to install code that does not match the sha256 in `crater.lock` or in the registry index
- a way to publish into someone else's scope in a crater registry, or to change a version that was already published
- a way to get a program, or a script that was not allowed, past `crater pack`, `crater publish` or `crater registry check`
- a backdoor pattern the package scanner should catch but does not
- `crater self-update` installing a binary from anywhere but the release of the configured repository
- crater sending tokens or other secrets anywhere they should not go

## Malicious packages

A package that contains a backdoor or malware is not a vulnerability in crater. Report it in the registry it was published to:

- the default crater registry: an issue in [TheExeDEV/crater-registry](https://github.com/TheExeDEV/crater-registry/issues) with the package name and version
- Wally, pesde, Forest, Ember or LuaRocks: the maintainers of that registry

A bad version of a crater registry package is marked with `crater deprecate`, so projects stop picking it.

## What crater already does

- Every downloaded archive is checked against the sha256 in the registry index and in `crater.lock`. A version that was republished with other content stops the install.
- Archive paths that leave the package folder are refused.
- Programs are never packed, and scripts are packed only when `package_files.allow` lists them.
- `crater publish` and the registry pull request check scan every new version for executables, `require(<asset id>)`, `loadstring`, `getfenv` and obfuscation. Nothing is merged into the registry automatically.
- Packages that run scripts in your game are named on install, and `package_scripts.block` refuses them.
