# Jellyfin Poster Rotator

Poster Rotator garde l'interface Jellyfin vivante en constituant un pool d'affiches par media, puis en remplacant regulierement l'image principale. La ligne `1.7.0.0` est optimisee pour les grosses bibliotheques et vise Jellyfin 12 beta.

## Compatibilite

- Version du plugin: `1.7.0.0`
- ABI cible: Jellyfin `12.0.0.0`
- Packages Jellyfin: `12.0.0-20260523021143`
- Runtime: `.NET 9`
- Ligne precedente: `1.6.0.0` reste la version pour Jellyfin `10.11.x`

La version `1.7.0.0` ne fait pas d'acces SQL brut. Elle passe par les services Jellyfin (`ILibraryManager`, `IProviderManager`) et stocke son etat dans le dossier data du plugin.
Les correctifs UI et les regenerations d'archive de cette ligne doivent conserver la version `1.7.0.0`.

## Fonctionnement

1. La tache planifiee **Download missing pools** recupere les IDs des films, series, collections, et optionnellement saisons/episodes.
2. Les IDs sont melanges puis traites par lots pour eviter de charger toute une bibliotheque en memoire.
3. Les bibliotheques activees sont resolues par nom ou par racines manuelles.
4. Chaque media utilise un pool local sous `Jellyfin.Plugin.PosterRotator.pools/{itemId}`.
5. Les affiches distantes viennent de `IProviderManager.GetAvailableRemoteImages(...)`.
6. Les images sont validees: URL, taille, format, dimensions, langue et doublons.
7. La tache **Rotate pools** lit `index.json` et applique uniquement des images deja presentes avec `IProviderManager.SaveImage(...)`, seulement si le cooldown du media est expire.
8. L'etat est maintenu dans `Jellyfin.Plugin.PosterRotator.pools/index.json` et `Jellyfin.Plugin.PosterRotator.pools/{itemId}/pool.json`.

Le planificateur Jellyfin affiche une section **Poster Rotator** avec:

- **Download missing pools**: remplit les pools manquantes ou incompletes sans changer les affiches, en ecrivant strictement sous `Jellyfin.Plugin.PosterRotator.pools`;
- **Rotate pools**: change les affiches eligibles depuis les pools existantes, sans telecharger;
- **Nettoyage pools orphelins**: supprime les pools dont le media n'existe plus.

Le seul reglage de volume expose dans l'interface est **Nombre maximum d'affiches a changer par passage**. `0` signifie aucune limite de nombre, tout en respectant le delai interne entre deux changements du meme media.

## Installation

Ajoutez ce depot dans Jellyfin:

```text
https://raw.githubusercontent.com/maelmoreau21/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
```

Puis installez **Poster Rotator** depuis le catalogue des plugins et redemarrez Jellyfin.

Pour une installation manuelle par zip, l'archive `Jellyfin.Plugin.PosterRotator-1.7.0.0.zip` contient aussi `meta.json` et `jellyfin-plugin-posterrotator.png`, afin que la page plugins de Jellyfin puisse afficher l'image du plugin.

## Interface

L'interface admin est organisee en deux vrais onglets separes: `Pools` s'ouvre par defaut et contient uniquement les outils de pools, puis `Parametres` contient uniquement les reglages.

- statistiques et etat dans l'onglet pools;
- recherche paginee des pools `PluginData`;
- filtres par bibliotheque Jellyfin, type, erreur et pool vide/non vide;
- detail du pool selectionne avec miniatures reduites cote serveur, normalisees au format affiche, taille bornee, et message lisible seulement si une image ne peut pas etre chargee;
- import et suppression d'affiches;
- rotation immediate par media ou bibliotheque;
- purge des orphelins, d'une bibliotheque ou d'un media;
- suppression de tous les pools en une action admin confirmee;
- action de maintenance **Reparer la liste des pools** pour reconstruire `Jellyfin.Plugin.PosterRotator.pools/index.json`;
- reglage simple du nombre maximum d'affiches changees par passage, avec l'aide `0 = aucune limite...` directement sous le libelle du champ;
- bibliotheques, langues et securite dans l'onglet `Parametres`;
- fallback de langue configurable: langue preferee, langue originale detectee, langue fallback, images sans langue et dernier recours toutes langues.

Les anciens dossiers `.poster_pool` du mode `MediaFolders` restent compatibles seulement si ce mode est choisi. Les anciens pools sous `Jellyfin.Plugin.PosterRotator/pools` ne sont pas migres et sont ignores.

## Stockage PluginData

- `Jellyfin.Plugin.PosterRotator.pools/index.json`: index leger pour recherche, pagination et actions globales.
- `Jellyfin.Plugin.PosterRotator.pools/{itemId}/pool.json`: metadonnees du pool, images, hashes, langues, sources, dates et erreurs recentes.
- Les anciens fichiers `rotation_state.json`, `pool_urls.json`, `pool_languages.json` et `pool_hashes.json` ne sont plus migres automatiquement.

La recherche des pools lit l'index existant sans scanner tous les dossiers. Si des pools ont ete ajoutes manuellement ou si l'index est absent, utilisez l'action **Reparer la liste des pools**. Si des medias n'ont pas encore de pool, utilisez la tache planifiee **Download missing pools**. Les apercus de la page admin utilisent `preview=true` pour charger une image reduite; si Jellyfin ne peut pas generer cette miniature, l'interface retente l'image originale tout en la gardant bornee a une petite carte.

Le telechargement distant suit une chaine de redirection bornee et revalide chaque cible pour eviter les redirections vers localhost, reseaux prives ou link-local quand le blocage des URLs privees est actif. Les uploads sont rejetes des l'entree API si leur taille depasse `MaxDownloadMegabytes`.

## Developpement

Les instructions de build, test et release sont dans [instructions.md](./instructions.md).

## Licence

Distribue sous licence MIT.
