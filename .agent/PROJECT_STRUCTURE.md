# Jellyfin Poster Rotator - Project Structure

> **Purpose**: Ce document sert de mémoire pour l'IA afin d'éviter toute hallucination et de maintenir une compréhension cohérente du projet.

---

## 📁 Structure des Fichiers

```
jellyfin-plugin-poster-rotator-1/
├── .agent/
│   └── PROJECT_STRUCTURE.md         # Ce fichier (mémoire IA)
├── manifest.json                    # Manifest du plugin pour le repository Jellyfin
├── README.md                        # Documentation principale
├── jellyfin-plugin-poster-rotator.sln
└── src/
    └── Jellyfin.Plugin.PosterRotator/
        ├── Api/
        │   └── PoolController.cs    # API REST pour la gestion des pools
        ├── Models/
        │   ├── PoolInfo.cs          # Modèle d'un pool et ses images
        │   └── PoolStatistics.cs    # Modèle de statistiques
        ├── Services/
        │   └── PoolService.cs       # Service métier pour les pools
        ├── Web/
        │   ├── config.html          # Interface de configuration
        │   └── pool_manager.html    # Interface Pool Manager (split-view)
        ├── Plugin.cs                # Enregistrement du plugin
        ├── Configuration.cs         # Classe de configuration
        ├── PosterRotatorService.cs  # Service principal de rotation
        ├── PosterRotationTask.cs    # Tâche planifiée Jellyfin
        ├── ServiceRegistrator.cs    # Injection de dépendances
        └── Jellyfin.Plugin.PosterRotator.csproj
```

---

## 🔧 Fichiers Clés

### `Plugin.cs`
- **Classe**: `Plugin : BasePlugin<Configuration>, IHasWebPages`
- **GUID**: `7f6eea8b-0e9c-4cbd-9d2a-31f9a37ce2b7`
- **Pages**: `config.html`, `pool_manager.html`

### `Configuration.cs`
Propriétés principales:
- `PoolSize` (défaut: 5)
- `SequentialRotation`
- `LockImagesAfterFill`
- `MinHoursBetweenSwitches` (défaut: 23)
- `EnableSeasonPosters`, `EnableEpisodePosters`
- `AutoCleanupOrphanedPools`, `CleanupIntervalDays`
- `EnableLanguageFilter`, `PreferredLanguage`, `MaxPreferredLanguageImages`
- `UseOriginalLanguageAsFallback`, `FallbackLanguage`, `IncludeUnknownLanguage`

### `PoolController.cs` (API REST)
| Endpoint | Méthode | Description |
|----------|---------|-------------|
| `/PosterRotator/Stats` | GET | Statistiques globales |
| `/PosterRotator/Items` | GET | Liste des items avec pools |
| `/PosterRotator/Pool/{id}` | GET | Détails d'un pool |
| `/PosterRotator/Pool/{id}` | POST | Upload image |
| `/PosterRotator/Pool/{id}/{file}` | DELETE | Supprimer image |
| `/PosterRotator/Search/{id}` | GET | Rechercher images providers |
| `/PosterRotator/Pool/{id}/AddFromUrl` | POST | Ajouter depuis URL |
| `/PosterRotator/Cleanup` | POST | Nettoyer orphelins |

### `PoolService.cs`
Méthodes principales:
- `GetStatisticsAsync()` - Stats globales
- `GetAllPoolsAsync()` - Liste tous les pools
- `GetPoolForItemAsync()` - Pool d'un item
- `AddImageToPoolAsync()` - Upload image
- `DeleteImageFromPoolAsync()` - Supprimer image
- `SearchRemoteImagesAsync()` - Recherche providers
- `AddImageFromUrlAsync()` - Télécharger depuis URL
- `CleanupOrphanedPoolsAsync()` - Nettoyage orphelins

### `PosterRotatorService.cs`
- `RunAsync()` - Point d'entrée de la rotation
- `ProcessItemAsync()` - Traite un item
- `Harvest()` - Filtre et télécharge images avec préférences de langue
- `GetOriginalLanguage()` - Détecte la langue originale du média
- `DetectLanguageFromTitle()` - Détection heuristique de langue

---

## 📂 Structure des Pools

```
/path/to/movie/
├── movie.mkv
├── poster.jpg
└── .poster_pool/
    ├── pool_currentprimary.jpg      # Backup affiche initiale
    ├── pool_1705123456789.jpg       # Affiches téléchargées
    ├── rotation_state.json          # État rotation
    ├── pool_languages.json          # Métadonnées langue
    ├── pool_order.json              # Ordre personnalisé
    └── pool.lock                    # Verrouillage
```

---

## 🌍 Détection Langue Originale

La fonction `GetOriginalLanguage()` utilise plusieurs heuristiques:
1. Comparaison `OriginalTitle` vs `Name`
2. Détection caractères Unicode (japonais, coréen, chinois, russe, arabe)
3. Provider IDs (AniDB → japonais)
4. Patterns dans le chemin (/anime/, /korean/)
5. Fallback configurable

---

## 🔌 APIs Jellyfin Utilisées

| Service | Utilisation |
|---------|-------------|
| `ILibraryManager` | Récupérer les items média |
| `IProviderManager` | Accéder aux providers d'images |
| `IRemoteImageProvider` | Télécharger images distantes |
| `BaseItem` | Représente un item média |
| `ImageType` | Types d'images (Primary, etc.) |

---

## ⚡ Points d'Attention

1. **Compatibilité Jellyfin 10.10/10.11**: Utilise la réflexion
2. **API Frontend**: Utilise `ApiClient.ajax()` et `ApiClient.getUrl()`
3. **Cooldown**: Respecte `MinHoursBetweenSwitches`
4. **Language Detection**: Heuristiques basées sur Unicode et métadonnées

---

## ✅ Fonctionnalités Implémentées (v1.3.0)

- [x] Pool Manager avec interface split-view
- [x] Statistiques (pools, images, taille, orphelins)
- [x] Recherche et filtrage des pools
- [x] Visualisation des images du pool
- [x] Recherche d'images via providers Jellyfin
- [x] Ajout d'images depuis URL
- [x] Import manuel (drag & drop)
- [x] Suppression d'images
- [x] Nettoyage des pools orphelins
- [x] Préférences de langue
- [x] Détection automatique langue originale (VO)
