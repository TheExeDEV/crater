# {{NAME}}

A package registry for [crater](https://github.com/{{CRATER_REPO}}), the package manager for Luau and Roblox.

Browse packages: {{SITE_URL}}

## Use this registry

Set it in `crater.json` of your project (new projects from `crater init` already have it):

```json
{
  "registry": "{{LOCATION}}"
}
```

Then install packages as usual:

```bash
crater add scope/name^
```

## Publish a package

Two commands, run in the folder of your package:

```bash
crater login
```

```bash
crater publish
```

`crater login` signs you in with GitHub (once per computer, nothing to install). `crater publish` sends the package here for review and prints a link to the request. A maintainer checks the code and accepts or declines it; GitHub notifies you either way. After it is accepted, the website and `crater search` show it within a few minutes.

Your packages are named after your GitHub login in lowercase: if your login is `TheExeDEV`, the name is `theexedev/<package>`. `crater init` fills this in for you. Members of a GitHub organization can also publish as the organization (`myorg/<package>`) once their membership is public. The version is three numbers, like `1.0.0`, and every publish needs a new one.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the rules.

## License

The registry itself (indexes, catalog, workflows, website) is under the [MIT License](LICENSE). Each package is distributed under the license in its own `crater.json`.
