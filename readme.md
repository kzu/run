# :runner: dnx runcs kzu/run:file.cs

Quickly and easily run any file in this repo using .NET 10 CLI: 

```pwsh
dnx runcs kzu/run:file.cs
```

The following are the available scripts.

## Azure KeyVault to dotnet-secrets sync

```pwsh
dnx runcs kzu/run:vault2secrets.cs
```

Uses the `az` CLI to fetch secrets from an Azure KeyVault and sync them to 
`dotnet user-secrets` store (either current directory project's secret or 
a specific id specified by `--id` argument or interactively).

The summary of the run shows what action was taken for each secret:

:white_check_mark: local secret value matches KeyVault value, no change needed

:heavy_plus_sign: new KeyVault added to local secrets

:pencil: existing local secret value updated to match KeyVault value

![vault2secrets](https://raw.githubusercontent.com/kzu/run/main/img/vault2secrets.png)

The updated secrets JSON is formatted with nested sections as appropriate 
for easier reading/editing.

## Clean bin/obj recursively

```pwsh
dnx runcs kzu/run:clean.cs
```

What you would expect `dotnet clean` to do, but it doesn't :)

![clean](https://raw.githubusercontent.com/kzu/run/main/img/clean.png)
