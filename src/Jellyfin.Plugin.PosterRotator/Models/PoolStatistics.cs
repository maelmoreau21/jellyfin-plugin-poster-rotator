namespace Jellyfin.Plugin.PosterRotator.Models;

using System;

/// <summary>
/// Statistiques globales sur les pools d'images.
/// </summary>
public class PoolStatistics
{
    /// <summary>
    /// Nombre total de pools existants.
    /// </summary>
    public int TotalPools { get; set; }

    /// <summary>
    /// Nombre total d'images dans tous les pools.
    /// </summary>
    public int TotalImages { get; set; }

    /// <summary>
    /// Taille totale en octets.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Taille totale formatée (ex: "2.5 GB").
    /// </summary>
    public string TotalSizeFormatted { get; set; } = string.Empty;

    /// <summary>
    /// Nombre de pools orphelins (médias supprimés).
    /// </summary>
    public int OrphanedPools { get; set; }

    /// <summary>
    /// Nombre de pools verrouillés.
    /// </summary>
    public int LockedPools { get; set; }

    /// <summary>
    /// Nombre de rotations dans les dernières 24 heures.
    /// </summary>
    public int RotationsLast24h { get; set; }

    /// <summary>
    /// Nombre de rotations dans les 7 derniers jours.
    /// </summary>
    public int RotationsLast7d { get; set; }

    /// <summary>
    /// Date de la dernière rotation effectuée.
    /// </summary>
    public DateTimeOffset? LastRotationTime { get; set; }

    /// <summary>
    /// Nombre moyen d'images par pool.
    /// </summary>
    public double AverageImagesPerPool { get; set; }

    /// <summary>
    /// Répartition par type d'item.
    /// </summary>
    public PoolTypeBreakdown TypeBreakdown { get; set; } = new();
}

/// <summary>
/// Répartition des pools par type d'item.
/// </summary>
public class PoolTypeBreakdown
{
    public int Movies { get; set; }
    public int Series { get; set; }
    public int Seasons { get; set; }
    public int Episodes { get; set; }
    public int BoxSets { get; set; }
}
