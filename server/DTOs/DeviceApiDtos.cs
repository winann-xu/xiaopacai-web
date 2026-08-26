using System.ComponentModel.DataAnnotations;

namespace XiaopacaiWeb.DTOs;

public class DeviceRegisterRequest
{
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? ExistingToken { get; set; }

    [MaxLength(16)]
    public string Platform { get; set; } = "harmonyos";
}

public class DeviceUsageReportRequest
{
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(10)]
    public string Date { get; set; } = string.Empty;

    public List<DeviceUsageRecord> Records { get; set; } = new();
}

public class DeviceUsageRecord
{
    [MaxLength(256)]
    public string AppPackage { get; set; } = string.Empty;

    [MaxLength(128)]
    public string AppName { get; set; } = string.Empty;

    [MaxLength(16)]
    public string Category { get; set; } = "other";

    public int DurationSeconds { get; set; }

    public bool IsBlocked { get; set; }
}

public class DeviceHeartbeatRequest
{
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    public long Timestamp { get; set; }

    public bool EmergencyActive { get; set; }
}

public class DeviceGuardEventRequest
{
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string EventType { get; set; } = string.Empty;

    public long? StartedAt { get; set; }

    public long? EndedAt { get; set; }

    public long? DurationSeconds { get; set; }

    [MaxLength(128)]
    public string? Reason { get; set; }

    [MaxLength(128)]
    public string? RestoredReason { get; set; }

    public bool WasEnforcing { get; set; }

    public string? HealthJson { get; set; }
}

public class DeviceAnnouncementAckRequest
{
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    public int AnnouncementId { get; set; }

    public long AcknowledgedAt { get; set; }
}

public class DeviceDiagnosticsReportRequest
{
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? AppVersion { get; set; }

    [MaxLength(16)]
    public string? OsVersion { get; set; }

    [MaxLength(64)]
    public string? DeviceModel { get; set; }

    [MaxLength(64)]
    public string? Manufacturer { get; set; }

    public string? PermissionStatus { get; set; }

    public string? ServiceStatus { get; set; }

    public string? RecentCrashes { get; set; }

    public long? DbSizeBytes { get; set; }

    [MaxLength(16)]
    public string? NetworkType { get; set; }
}

public class DeviceEmergencyReleaseRequest
{
    [Required]
    [MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? Reason { get; set; }

    public int? DurationMinutes { get; set; }
}
