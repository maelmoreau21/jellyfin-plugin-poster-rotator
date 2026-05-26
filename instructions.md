# Instructions

## Objectif de la branche 1.7

Preparer Poster Rotator `1.7.0.0` pour Jellyfin `12.0.0.0`.

- Ne pas ajouter d'acces SQL brut.
- Ne pas utiliser `SQLiteConnection`, `DbConnection`, `FromSql`, `ExecuteSql`, ni requetes textuelles.
- Utiliser les services Jellyfin injectes (`ILibraryManager`, `IProviderManager`, etc.).
- Garder `CS0618` en erreur pour bloquer les API `[Obsolete]`.
- Conserver `1.6.0.0` comme ligne compatible Jellyfin `10.11.x`.

## Stockage des pools

La ligne 1.7 utilise un stockage fichier structure dans un dossier dedie, frere du dossier config du plugin:

- `Jellyfin.Plugin.PosterRotator.pools/index.json`: index leger pour diagnostics, recherche, pagination et actions globales.
- `Jellyfin.Plugin.PosterRotator.pools/{itemId}/pool.json`: metadonnees versionnees du pool, images, hashes, langues, sources, dates et erreurs recentes.
- Les anciens pools sous `Jellyfin.Plugin.PosterRotator/pools` sont ignores et ne doivent pas etre migres.
- Les anciens fichiers `rotation_state.json`, `pool_urls.json`, `pool_languages.json` et `pool_hashes.json` ne doivent plus etre migres automatiquement.

La recherche UI ne doit pas scanner tous les dossiers a chaque requete. Utiliser l'index existant et reconstruire automatiquement l'index seulement s'il est absent ou illisible alors que des dossiers de pools existent. Apres une purge complete, invalider le cache et accepter une liste vide propre. L'endpoint `POST /PosterRotator/Pools/RebuildIndex` reste disponible en interne/admin, mais aucun bouton visible ne doit etre expose pour cette action.

Ne pas ajouter de `DbContext` custom ni de SQL brut pour ce stockage.

## Rotation et telechargement grande bibliotheque

Les taches planifiees doivent rester adaptees aux bibliotheques de plus de 200 000 medias:

- `Download missing pools` recupere les IDs avec `ILibraryManager.GetItemIds`;
- `Rotate pools` lit `Jellyfin.Plugin.PosterRotator.pools/index.json` et ne scanne pas toute la bibliotheque;
- melanger les IDs ou les entrees d'index avant traitement pour repartir les changements sur de grosses bibliotheques;
- traiter par `ProcessingBatchSize`;
- resoudre les items au moment du traitement;
- respecter `MinHoursBetweenSwitches` avant toute rotation;
- plafonner chaque execution avec `MaxRotationsPerRun`, `MaxProviderLookupsPerRun` et `MaxDownloadsPerRun`;
- interpreter `MaxRotationsPerRun = 0` comme "pas de limite de rotations", sans ignorer le cooldown;
- ne pas telecharger ni creer de pool vide depuis `Rotate pools`;
- ne creer `Jellyfin.Plugin.PosterRotator.pools/{itemId}` pendant `Download missing pools` que via `PoolStore`, lorsqu'un fichier image valide va etre ecrit, puis supprimer le dossier s'il reste vide;
- ne jamais retomber vers `.poster_pool` ou le dossier du media quand `Download missing pools` force le stockage `PluginData`;
- ignorer et vider les anciens `ManualLibraryRoots` caches pendant les runs admin ou planifies, car l'UI ne les expose plus;
- garder `PluginData` comme stockage recommande.

Valeurs par defaut:

- `PoolSize`: `4`
- `MinHoursBetweenSwitches`: `72`
- `MaxRotationsPerRun`: `500`
- `MaxProviderLookupsPerRun`: `250`
- `MaxDownloadsPerRun`: `250`
- `ProcessingBatchSize`: `250`
- `CadenceProfile`: `Balanced`

La detection de doublons doit utiliser un hash calcule apres normalisation par `IImageProcessor` quand ce service Jellyfin est disponible, avec fallback vers le hash fichier leger. Les anciens hashes restent acceptes, mais les nouveaux telechargements et imports doivent privilegier le hash normalise.

## API admin

Toutes les routes `PosterRotator/*` doivent rester protegees par `RequiresElevation`.

- `GET /PosterRotator/Diagnostics`
- `GET /PosterRotator/Pools?library=&query=&type=&hasErrors=&isEmpty=&start=&limit=`
- `POST /PosterRotator/Pools/RebuildIndex` (interne/admin, pas de bouton visible)
- `POST /PosterRotator/Pools/DownloadMissing`
- `GET /PosterRotator/Pools/{itemId}`
- `GET /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `GET /PosterRotator/Pools/{itemId}/Images/{fileName}?preview=true&maxWidth=320&maxHeight=480&quality=80`
- `POST /PosterRotator/Pools/{itemId}/RotateNow`
- `POST /PosterRotator/Libraries/{libraryName}/RotateNow`
- `POST /PosterRotator/Pools/{itemId}/Images`
- `DELETE /PosterRotator/Pools/{itemId}/Images/{fileName}`
- `POST /PosterRotator/Purge`

La reponse `GET /PosterRotator/Pools/{itemId}` expose aussi l'affiche courante:

- `CurrentPoster.PrimaryImageFound`
- `CurrentPoster.Matched`
- `CurrentPoster.FileName`
- `CurrentPoster.MatchMethod`
- `Images[].IsCurrent`

La detection tente d'abord le hash de l'image primaire Jellyfin actuelle, puis retombe sur la derniere image appliquee (`LastAppliedUtc`) si aucun hash ne correspond.

## Taches planifiees

Jellyfin doit afficher une categorie `Poster Rotator` dans le planificateur:

- `Download missing pools`: tache quotidienne par defaut a 02:00, cle stable `PosterRotator.DownloadMissingPoolsTask`;
- `Rotate pools`: tache quotidienne par defaut a 03:00, cle stable `PosterRotator.RotatePostersTask`, rotation-only sans telechargement;
- `Nettoyage pools orphelins`: tache hebdomadaire par defaut, purge `Scope = "orphans"`.

Les anciennes options de nettoyage automatique restent dans le modele de configuration pour compatibilite, mais ne doivent plus etre exposees dans l'interface.

## Interface

L'interface utilise deux vrais onglets ARIA: `Pools` et `Parametres`.

- `Pools` est actif par defaut, avec `SettingsPanel` masque par `hidden`;
- recherche et filtres uniquement dans l'onglet `Pools`;
- filtre bibliotheque sous forme de menu deroulant charge depuis `/Library/VirtualFolders`;
- statistiques compactes;
- table paginee des pools avec taille `25 / 50 / 100 / 200`;
- table de resultats dense, avec nom, chemin ou ID, et badges type/bibliotheque lisibles;
- token de requete JS pour eviter qu'une ancienne recherche remplace une recherche plus recente;
- panneau de detail avec miniatures chargees via `ApiKey` et parametres `preview`;
- ouvrir un pool ne doit pas recharger toute la liste; recharger la liste seulement apres rotation, import, suppression ou purge;
- miniatures de pools reduites cote serveur via `IImageProcessor.ProcessImage`, normalisees en taille bornee, format affiche, avec fallback `Apercu indisponible` masque par defaut et visible seulement sur erreur de chargement;
- si la preview serveur echoue, l'image retente une fois la route originale, toujours bornee par CSS et attributs `width`/`height`, avant d'afficher `Apercu indisponible`;
- les cartes d'images doivent rester petites, environ `104x156`, pour voir plusieurs affiches a l'ecran;
- le nom de fichier des affiches doit pouvoir passer sur 2 ou 3 lignes avec `overflow-wrap:anywhere`;
- l'affiche courante doit afficher un badge `Actuelle`;
- suppression/import d'images;
- action principale `Telecharger les pools manquants` qui appelle `POST /PosterRotator/Pools/DownloadMissing`;
- ne pas afficher de bouton `Reparer la liste des pools`; la reparation d'index est automatique ou reservee a l'endpoint admin;
- action `Supprimer tous les pools` qui appelle `POST /PosterRotator/PurgeAllPools` apres confirmation;
- les boutons `Precedent`, `Suivant`, `Rotation bibliotheque`, `Purger bibliotheque` et `Purger media` doivent etre desactives quand leur action n'est pas disponible;
- l'onglet `Parametres` expose uniquement les reglages utiles au quotidien;
- le champ `Nombre maximum d'affiches a changer par passage` accepte `0` pour aucune limite de nombre, avec le texte d'aide dans le meme `inputContainer` juste sous le libelle;
- les langues exposent un ordre de fallback configurable: langue originale puis fallback, fallback puis langue originale, originale uniquement, ou fallback uniquement;
- le helper JS `fallbackModeValue` doit accepter `0..3` et les noms `OriginalThenConfigured`, `ConfiguredThenOriginal`, `OriginalOnly`, `ConfiguredOnly`;
- `Rotation sequentielle` doit etre libelle `Parcourir les affiches dans l'ordre`, avec l'aide: `Active: prend l'image suivante du pool a chaque rotation. Desactive: choisit une affiche au hasard. Ne change pas le delai entre deux rotations.`;
- le dernier recours toutes langues peut etre active separement;
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
dotnet restore .\jellyfin-plugin-poster-rotator.sln -p:JellyfinPackageVersion=12.0.0-20260523021143 --source https://api.nuget.org/v3/index.json --source https://nuget.pkg.github.com/jellyfin/index.json
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release --no-restore -p:JellyfinPackageVersion=12.0.0-20260523021143 -warnaserror:CS0618
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release --no-restore -p:JellyfinPackageVersion=12.0.0-20260523021143 -warnaserror:CS0618
```

Fallback public pour verifier le code sans acces GitHub Packages:

```powershell
dotnet build .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.10 -warnaserror:CS0618
dotnet test .\jellyfin-plugin-poster-rotator.sln -c Release -p:JellyfinPackageVersion=10.11.10 -warnaserror:CS0618
```

## Release

1. Verifier que `Version`, `AssemblyVersion` et `FileVersion` valent `1.7.0.0`.
   - Ne pas changer `meta.json.version`, `manifest.json.version` ni `targetAbi` pour un correctif UI de cette ligne.
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
- Les anciens dossiers `.poster_pool` restent disponibles seulement pour le mode `MediaFolders`; ne pas les migrer automatiquement vers PluginData.
- Les telechargements distants doivent rester bornes en taille et bloquer les URLs privees/locales par defaut.
- L'interface ne doit editer que les pools `Jellyfin.Plugin.PosterRotator.pools`; les pools `MediaFolders` restent un mode de compatibilite.
- Les actions upload, suppression et rotation immediate doivent utiliser un verrou par pool.
- Les telechargements distants doivent desactiver les redirections automatiques et revalider chaque cible de redirection avant de lire la reponse.
- Les uploads doivent rejeter `IFormFile.Length` au-dessus de `MaxDownloadMegabytes` avant `OpenReadStream`.
