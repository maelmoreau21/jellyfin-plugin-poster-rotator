# Instructions

## Objectif de la branche 1.7

Préparer Poster Rotator `1.7.0.0` pour Jellyfin `10.12.0.0`.

- Ne pas ajouter d'accès SQL brut.
- Ne pas utiliser `SQLiteConnection`, `DbConnection`, `FromSql`, `ExecuteSql`, ni requêtes textuelles.
- Utiliser les services Jellyfin injectés (`ILibraryManager`, `IProviderManager`, etc.).
- Garder `CS0618` en erreur pour bloquer les API `[Obsolete]`.
- Conserver `1.6.0.0` comme ligne compatible Jellyfin `10.11.x`.

## Stockage des pools

La ligne 1.7 utilise un stockage fichier structure sous `PluginData/pools`:

- `pools/index.json`: index leger pour diagnostics, pagination et actions globales.
- `pools/{itemId}/pool.json`: metadonnees versionnees du pool, images, hashes, langues, sources, dates et erreurs recentes.
- Les anciens fichiers `rotation_state.json`, `pool_urls.json`, `pool_languages.json` et `pool_hashes.json` sont migres puis supprimes apres ecriture reussie de `pool.json`.

Ne pas ajouter de `DbContext` custom ni de SQL brut pour ce stockage.

## API admin

Toutes les routes `PosterRotator/*` doivent rester protegees par `RequiresElevation`.

- `GET /PosterRotator/Diagnostics`
- `GET /PosterRotator/Pools?library=&query=&type=&start=&limit=`
- `GET /PosterRotator/Pools/{itemId}`
- `GET /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `POST /PosterRotator/Pools/{itemId}/RotateNow`
- `POST /PosterRotator/Libraries/{libraryName}/RotateNow`
- `POST /PosterRotator/Pools/{itemId}/Images`
- `DELETE /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `POST /PosterRotator/Purge`

## Build local

La cible par défaut du projet est:

```text
10.12.0-20260330054505
```

Cette version est publiée sur GitHub Packages Jellyfin et nécessite une configuration NuGet authentifiée.

Commande attendue pour Jellyfin 10.12:

```powershell
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.12.0-20260330054505 -warnaserror:CS0618 --source https://api.nuget.org/v3/index.json --source https://nuget.pkg.github.com/jellyfin/index.json
```

Fallback public pour vérifier le code sans accès GitHub Packages:

```powershell
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.9 -warnaserror:CS0618
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.9 -warnaserror:CS0618
```

## Release

1. Vérifier que `Version`, `AssemblyVersion` et `FileVersion` valent `1.7.0.0`.
2. Compiler en `Release` contre le package Jellyfin 10.12 authentifié.
3. Lancer les tests.
4. Créer `Jellyfin.Plugin.PosterRotator-1.7.0.0.zip` avec:
   - `Jellyfin.Plugin.PosterRotator.dll`
   - `Jellyfin.Plugin.PosterRotator.deps.json`
   - `Jellyfin.Plugin.PosterRotator.pdb`
   - `jellyfin-plugin-posterrotator.png`
5. Calculer le MD5 du zip et le reporter dans `manifest.json`.
6. Garder l'entrée `1.6.0.0` du manifest pour Jellyfin `10.11.x`.

## Nettoyage

Fichiers à ne pas commiter:

- `bin/`
- `obj/`
- `artifacts/`
- fichiers temporaires de packaging

Le zip de release à la racine peut être conservé seulement quand il correspond à une version déclarée dans `manifest.json`.

## Vérifications utiles

```powershell
rg -n "SQLite|Sqlite|SQLiteConnection|DbConnection|DbContext|FromSql|ExecuteSql|SELECT |INSERT |UPDATE |DELETE |System\.Data|Microsoft\.Data|RawSql" src tests
rg -n "Obsolete|GetImageProviders|IRemoteImageProvider|SetLastWriteTimeUtc" src tests
Get-Content .\manifest.json | ConvertFrom-Json | Out-Null
```

## Comportement à préserver

- Le stockage `PluginData` doit rester le mode recommandé.
- Le mode `MediaFolders` doit rester disponible uniquement pour compatibilité.
- Les anciens dossiers `.poster_pool` doivent être migrés ou purgeables sans suivre les reparse points.
- Les téléchargements distants doivent rester bornés en taille et bloquer les URLs privées/locales par défaut.
- L'interface ne doit editer que les pools `PluginData`; les pools `MediaFolders` restent un mode de compatibilite.
- Les actions upload, suppression et rotation immediate doivent utiliser un verrou par pool.
