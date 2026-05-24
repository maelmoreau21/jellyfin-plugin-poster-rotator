# Jellyfin Poster Rotator

Poster Rotator garde l'interface Jellyfin plus vivante en constituant un petit pool d'affiches par média, puis en remplaçant périodiquement l'image principale par une autre affiche validée.

## Compatibilité

- Version courante du plugin: `1.7.0.0`
- ABI cible: Jellyfin `10.12.0.0` prerelease
- Runtime: `.NET 9`
- Ligne précédente: `1.6.0.0` reste la version à utiliser pour Jellyfin `10.11.x`

La version `1.7.0.0` vise Jellyfin `10.12`, où l'accès SQL brut et les API obsolètes ne doivent plus être utilisés. Le plugin ne fait pas d'accès direct à la base de données: il passe par les services Jellyfin (`ILibraryManager`, `IProviderManager`) et stocke son état dans le dossier de données du plugin.

## Fonctionnement

1. La tâche planifiée parcourt les films, séries, collections, et éventuellement saisons/épisodes.
2. Le plugin résout les bibliothèques activées dans la configuration.
3. Pour chaque média, il crée ou complète un pool local d'affiches dans `PluginData/pools`.
4. Les images distantes sont récupérées via `IProviderManager.GetAvailableRemoteImages(...)`.
5. Chaque image est validée: URL, taille de téléchargement, format, dimensions, langue et doublons visuels.
6. L'affiche suivante est choisie, puis appliquée avec `IProviderManager.SaveImage(...)`.
7. L'etat du pool est mis a jour dans un stockage JSON versionne: `pools/index.json` et `pools/{itemId}/pool.json`.

Par défaut, les pools sont stockés dans le dossier data du plugin. C'est préférable au mode historique `MediaFolders`, car cela évite de créer des dossiers `.poster_pool` dans les bibliothèques et réduit le travail des watchers Jellyfin: les images du pool ne sont plus créées/modifiées dans les chemins surveillés de la médiathèque.

Nuance: cette phrase est vraie seulement avec le mode `PluginData`. Les anciens dossiers `.poster_pool` déjà présents peuvent encore exister jusqu'à migration ou purge, et le mode `MediaFolders` continue volontairement à écrire dans les dossiers média.

## Installation

Ajoutez ce dépôt dans Jellyfin:

```text
https://raw.githubusercontent.com/maelmoreau21/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
```

Puis installez **Poster Rotator** depuis le catalogue des plugins et redémarrez Jellyfin.

## Configuration

Réglages principaux:

- `Pool Size`: nombre d'affiches candidates conservées par média.
- `Pool Storage`: utilisez `Plugin data folder` sauf besoin précis de compatibilité historique.
- `Min Hours Between Switches`: délai minimal avant une nouvelle rotation.
- `Sequential Rotation`: rotation stable au lieu d'un choix aléatoire.
- `Language Filter`: priorise les affiches dans la langue choisie.
- `Lock Images After Fill`: bloque un pool une fois rempli.

## Gestion des pools

L'interface admin contient maintenant deux ecrans dedies:

- `Diagnostic`: nombre de pools, taille disque, estimation des orphelins, derniers medias traites et erreurs recentes.
- `Pools`: recherche paginee, detail d'un pool, miniatures, suppression d'image, import par upload, rotation immediate par media ou bibliotheque, purge selective.

Seuls les pools modernes du dossier `PluginData` sont editables depuis l'interface. Les anciens dossiers `.poster_pool` restent migrables ou purgeables, mais ils ne sont pas exposes comme pools editables pour eviter de reveiller inutilement les watchers Jellyfin.

## Mise à jour depuis 1.6

- Jellyfin `10.11.x`: restez sur Poster Rotator `1.6.0.0`.
- Jellyfin `10.12.x`: utilisez Poster Rotator `1.7.0.0`.
- Les pools en dossier média peuvent être migrés vers le dossier data du plugin quand `Pool Storage` est réglé sur `PluginData`.
- Les anciens fichiers `rotation_state.json`, `pool_urls.json`, `pool_languages.json` et `pool_hashes.json` sont migres vers `pool.json`, puis supprimes seulement apres ecriture reussie du nouveau stockage.

## Idées d'amélioration

- Ajouter une limite globale de taille disque pour les pools, avec nettoyage LRU.
- Ajouter un mode "pré-remplissage seulement" pour télécharger les affiches sans changer immédiatement le poster.
- Ajouter des métriques simples dans les logs: temps moyen par média, images rejetées par cause, providers les plus utiles.
- Ajouter des tests d'intégration autour de la migration `.poster_pool` vers `PluginData`.
- Ajouter une action de previsualisation avant rotation pour comparer l'affiche actuelle et la candidate.
- Ajouter un export/import de pool complet pour deplacer une selection d'affiches entre serveurs.

## Développement

Les instructions de build, test et release sont dans [instructions.md](./instructions.md).

## Licence

Distribué sous licence MIT.
