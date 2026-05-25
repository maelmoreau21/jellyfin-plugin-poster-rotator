# Instructions

## Objectif de la branche 1.7

Preparer Poster Rotator `1.7.0.0` pour Jellyfin `12.0.0.0`.

- Ne pas ajouter d'acces SQL brut.
- Ne pas utiliser `SQLiteConnection`, `DbConnection`, `FromSql`, `ExecuteSql`, ni requetes textuelles.
- Utiliser les services Jellyfin injectes (`ILibraryManager`, `IProviderManager`, etc.).
- Garder `CS0618` en erreur pour bloquer les API `[Obsolete]`.
- Conserver `1.6.0.0` comme ligne compatible Jellyfin `10.11.x`.

## Stockage des pools

La ligne 1.7 utilise un stockage fichier structure sous `PluginData/pools`:

- `pools/index.json`: index leger pour diagnostics, recherche, pagination et actions globales.
- `pools/{itemId}/pool.json`: metadonnees versionnees du pool, images, hashes, langues, sources, dates et erreurs recentes.
- Les anciens fichiers `rotation_state.json`, `pool_urls.json`, `pool_languages.json` et `pool_hashes.json` sont migres puis supprimes apres ecriture reussie de `pool.json`.

La recherche UI ne doit pas scanner tous les dossiers a chaque requete. Utiliser l'index existant et l'action explicite `POST /PosterRotator/Pools/RebuildIndex` pour reconstruire l'index.

Ne pas ajouter de `DbContext` custom ni de SQL brut pour ce stockage.

## Rotation grande bibliotheque

La tache planifiee doit rester adaptee aux bibliotheques de plus de 20 000 medias:

- recuperer les IDs avec `ILibraryManager.GetItemIds`;
- melanger les IDs avant traitement pour repartir les changements sur de grosses bibliotheques;
- traiter par `ProcessingBatchSize`;
- resoudre les items au moment du traitement;
- respecter `MinHoursBetweenSwitches` avant toute rotation;
- plafonner chaque execution avec `MaxRotationsPerRun`, `MaxProviderLookupsPerRun` et `MaxDownloadsPerRun`;
- interpreter `MaxRotationsPerRun = 0` comme "pas de limite de rotations", sans ignorer le cooldown;
- garder `PluginData` comme stockage recommande.

Valeurs par defaut:

- `PoolSize`: `4`
- `MinHoursBetweenSwitches`: `72`
- `MaxRotationsPerRun`: `500`
- `MaxProviderLookupsPerRun`: `250`
- `MaxDownloadsPerRun`: `250`
- `ProcessingBatchSize`: `250`
- `CadenceProfile`: `Balanced`

## API admin

Toutes les routes `PosterRotator/*` doivent rester protegees par `RequiresElevation`.

- `GET /PosterRotator/Diagnostics`
- `GET /PosterRotator/Pools?library=&query=&type=&hasErrors=&isEmpty=&start=&limit=`
- `POST /PosterRotator/Pools/RebuildIndex`
- `GET /PosterRotator/Pools/{itemId}`
- `GET /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `POST /PosterRotator/Pools/{itemId}/RotateNow`
- `POST /PosterRotator/Libraries/{libraryName}/RotateNow`
- `POST /PosterRotator/Pools/{itemId}/Images`
- `DELETE /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `POST /PosterRotator/Purge`

## Taches planifiees

Jellyfin doit afficher une categorie `Poster Rotator` dans le planificateur:

- `Rotate posters`: tache quotidienne par defaut, cle stable `PosterRotator.RotatePostersTask`;
- `Nettoyage pools orphelins`: tache hebdomadaire par defaut, purge `Scope = "orphans"`.

Les anciennes options de nettoyage automatique restent dans le modele de configuration pour compatibilite, mais ne doivent plus etre exposees dans l'interface.

## Interface

L'interface utilise deux onglets: `Pools` et `Parametres`.

- recherche et filtres dans l'onglet `Pools`;
- filtre bibliotheque sous forme de menu deroulant charge depuis `/Library/VirtualFolders`;
- statistiques compactes;
- table paginee des pools;
- panneau de detail avec miniatures;
- suppression/import d'images;
- action de maintenance `Reparer la liste des pools` qui appelle `POST /PosterRotator/Pools/RebuildIndex`;
- l'onglet `Parametres` expose seulement les reglages utiles au quotidien;
- le champ `Nombre maximum d'affiches a changer par passage` accepte `0` pour aucune limite de nombre;
- ne pas afficher `CadenceProfile`, `PoolSize`, `MinHoursBetweenSwitches`, `MaxProviderLookupsPerRun`, `MaxDownloadsPerRun`, `ProcessingBatchSize`, `AutoCleanupOrphanedPools` ou `CleanupIntervalDays`;
- ne pas afficher `ManualLibraryRoots`; vider cette liste lors de la sauvegarde depuis l'interface.

## Build local

La cible par defaut du projet est:

```text
12.0.0-20260523021143
```

Cette version est publiee sur GitHub Packages Jellyfin et necessite une configuration NuGet authentifiee.

Commande attendue pour Jellyfin 12:

```powershell
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=12.0.0-20260523021143 -warnaserror:CS0618 --source https://api.nuget.org/v3/index.json --source https://nuget.pkg.github.com/jellyfin/index.json
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=12.0.0-20260523021143 -warnaserror:CS0618 --source https://api.nuget.org/v3/index.json --source https://nuget.pkg.github.com/jellyfin/index.json
```

Fallback public pour verifier le code sans acces GitHub Packages:

```powershell
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.10 -warnaserror:CS0618
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.10 -warnaserror:CS0618
```

## Release

1. Verifier que `Version`, `AssemblyVersion` et `FileVersion` valent `1.7.0.0`.
2. Compiler en `Release` contre le package Jellyfin 12 authentifie.
3. Lancer les tests.
4. Creer `Jellyfin.Plugin.PosterRotator-1.7.0.0.zip` avec:
   - `Jellyfin.Plugin.PosterRotator.dll`
   - `Jellyfin.Plugin.PosterRotator.deps.json`
   - `Jellyfin.Plugin.PosterRotator.pdb`
   - `jellyfin-plugin-posterrotator.png`
   - `meta.json`
5. Calculer le MD5 du zip et le reporter dans `manifest.json`.
6. Garder l'entree `1.6.0.0` du manifest pour Jellyfin `10.11.x`.

## Nettoyage

Fichiers a ne pas commiter:

- `bin/`
- `obj/`
- `artifacts/`
- fichiers temporaires de packaging

Le zip de release a la racine peut etre conserve seulement quand il correspond a une version declaree dans `manifest.json`.

## Verifications utiles

```powershell
rg -n "SQLite|Sqlite|SQLiteConnection|DbConnection|DbContext|FromSql|ExecuteSql|SELECT |INSERT |UPDATE |DELETE |System\.Data|Microsoft\.Data|RawSql" src tests
rg -n "Obsolete|GetImageProviders|IRemoteImageProvider|SetLastWriteTimeUtc" src tests
Get-Content .\manifest.json | ConvertFrom-Json | Out-Null
```

## Comportement a preserver

- Le stockage `PluginData` doit rester le mode recommande.
- Le mode `MediaFolders` doit rester disponible uniquement pour compatibilite.
- Les anciens dossiers `.poster_pool` doivent etre migres ou purgeables sans suivre les reparse points.
- Les telechargements distants doivent rester bornes en taille et bloquer les URLs privees/locales par defaut.
- L'interface ne doit editer que les pools `PluginData`; les pools `MediaFolders` restent un mode de compatibilite.
- Les actions upload, suppression et rotation immediate doivent utiliser un verrou par pool.
