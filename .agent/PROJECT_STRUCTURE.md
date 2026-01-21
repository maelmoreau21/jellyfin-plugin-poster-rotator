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
        │   └── pool_manager.html    # Interface de gestion des pools
        ├── Plugin.cs                # Enregistrement du plugin, implémente IHasWebPages
        ├── Configuration.cs         # Classe de configuration (settings persistants)
        ├── PosterRotatorService.cs  # Service principal de rotation (1628 lignes)
        ├── PosterRotationTask.cs    # Tâche planifiée Jellyfin
        ├── ServiceRegistrator.cs    # Injection de dépendances
        └── Jellyfin.Plugin.PosterRotator.csproj
```

---

## 🔧 Fichiers Clés

### `Plugin.cs`
- **Classe**: `Plugin : BasePlugin<Configuration>, IHasWebPages`
- **GUID**: `7f6eea8b-0e9c-4cbd-9d2a-31f9a37ce2b7`
- **Rôle**: Point d'entrée du plugin, expose les pages web
- **Pages**: Renvoie `config.html` comme ressource embarquée

### `Configuration.cs`
- **Classe**: `Configuration : BasePluginConfiguration`
- **Propriétés actuelles**:
  - `List<LibraryRule> LibraryRules` - Règles par bibliothèque (nom + enabled)
  - `int PoolSize` (défaut: 5) - Nombre d'affiches par item
  - `bool SequentialRotation` - Rotation séquentielle vs aléatoire
  - `bool LockImagesAfterFill` - Verrouiller le pool une fois rempli
  - `int MinHoursBetweenSwitches` (défaut: 23) - Cooldown entre rotations
  - `bool EnableSeasonPosters` - Inclure les saisons
  - `bool EnableEpisodePosters` - Inclure les épisodes
  - `bool TriggerLibraryScanAfterRotation` - Déclencher scan après rotation
  - `List<string> ExtraPosterPatterns` - Patterns de fichiers additionnels
  - `List<string> ManualLibraryRoots` - Chemins manuels

### `PosterRotatorService.cs`
- **Classe**: `PosterRotatorService`
- **Dépendances injectées**:
  - `ILibraryManager _library`
  - `IProviderManager _providers`
  - `IServiceProvider _services`
  - `ILogger<PosterRotatorService> _log`
- **Méthodes principales**:
  - `RunAsync()` - Point d'entrée de la rotation
  - `ProcessItemAsync()` - Traite un item (film/série)
  - `TryTopUpFromProvidersDIAsync()` - Télécharge depuis providers
  - `PickNextFor()` - Choisit la prochaine image
  - `GetLibraryRootPaths()` - Récupère les chemins des bibliothèques

### `Web/config.html`
- Interface de configuration embarquée
- Utilise `ApiClient.getPluginConfiguration()` / `updatePluginConfiguration()`
- Composants Emby: `emby-input`, `emby-button`, `emby-checkbox`

---

## 📂 Structure des Pools (par item média)

```
/chemin/vers/media/
├── film.mkv
├── poster.jpg                       # Affiche actuelle
└── .poster_pool/
    ├── pool_currentprimary.jpg      # Snapshot de l'affiche initiale
    ├── pool_1705123456789.jpg       # Affiches téléchargées (timestamp)
    ├── pool_1705123456790.jpg
    ├── rotation_state.json          # État de rotation (dernière rotation, index)
    └── pool.lock                    # Présent si pool verrouillé
```

### `rotation_state.json`
```json
{
  "LastRotatedUtcByItem": {
    "<item-guid>": 1705123456
  }
}
```

---

## 🔌 APIs Jellyfin Utilisées

| Service | Utilisation |
|---------|-------------|
| `ILibraryManager` | Récupérer les items (films, séries, etc.) |
| `IProviderManager` | Accéder aux providers d'images |
| `IRemoteImageProvider` | Télécharger les images distantes |
| `BaseItem` | Représente un item média |
| `ImageType` | Types d'images (Primary, Backdrop, etc.) |

---

## ⚡ Points d'Attention

1. **Compatibilité Jellyfin 10.10/10.11**: Utilise la réflexion pour les APIs qui ont changé
2. **Mixed Folders**: Gestion spéciale quand plusieurs films dans le même dossier
3. **Cooldown**: Respecte `MinHoursBetweenSwitches` avant de rotater
4. **Locking**: Option pour verrouiller le pool une fois rempli

---

## 🚀 Fonctionnalités Planifiées (v1.3.0)

1. **Interface web de gestion du pool** 
   - Visualiser les images du pool par item
   - Supprimer des images individuelles
   - Réordonner les images manuellement

2. **Dashboard de statistiques**
   - Nombre d'items avec pools
   - Taille totale des pools
   - Dernières rotations effectuées

3. **Import manuel d'images**
   - Glisser-déposer des images dans l'interface
   - Upload vers le pool d'un item spécifique

4. **Nettoyage automatique**
   - Détecter les pools orphelins (médias supprimés)
   - Option pour supprimer automatiquement

---

## 📝 Conventions de Code

- **Namespace**: `Jellyfin.Plugin.PosterRotator`
- **Logging**: Via `ILogger<T>` avec préfixe "PosterRotator:"
- **Async**: Toutes les opérations I/O sont async
- **Réflexion**: Utilisée pour la compatibilité multi-versions
