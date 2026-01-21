namespace Jellyfin.Plugin.PosterRotator.Models;

using System;
using System.Collections.Generic;

/// <summary>
/// Représente les informations d'un pool d'images pour un item média.
/// </summary>
public class PoolInfo
{
    /// <summary>
    /// Identifiant unique de l'item Jellyfin.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Nom de l'item (titre du film/série).
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Type d'item (Movie, Series, Season, Episode).
    /// </summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>
    /// Chemin absolu du dossier .poster_pool.
    /// </summary>
    public string PoolPath { get; set; } = string.Empty;

    /// <summary>
    /// Liste des images dans le pool.
    /// </summary>
    public List<PoolImage> Images { get; set; } = new();

    /// <summary>
    /// Taille totale du pool en octets.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Taille formatée (ex: "25.3 MB").
    /// </summary>
    public string TotalSizeFormatted { get; set; } = string.Empty;

    /// <summary>
    /// Indique si le pool est verrouillé (pool.lock existe).
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Date de la dernière rotation.
    /// </summary>
    public DateTimeOffset? LastRotation { get; set; }
}

/// <summary>
/// Représente une image individuelle dans un pool.
/// </summary>
public class PoolImage
{
    /// <summary>
    /// Nom du fichier (ex: pool_1705123456789.jpg).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// URL relative pour afficher l'image via l'API.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Taille du fichier en octets.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Taille formatée (ex: "1.2 MB").
    /// </summary>
    public string SizeFormatted { get; set; } = string.Empty;

    /// <summary>
    /// Date de création du fichier.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Indique si c'est l'image actuellement utilisée comme poster.
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// Ordre dans la rotation séquentielle.
    /// </summary>
    public int Order { get; set; }
}
