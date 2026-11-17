using System;
using System.Collections.Generic;

namespace NexusBGI.RoutePackages;

public sealed class RoutePackageFile
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class RoutePackageDescriptor
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Description { get; set; } = "";
    public string RequiredClientVersion { get; set; } = "";
    public int FileCount { get; set; }
    public int RouteCount { get; set; }
    public List<RoutePackageFile> Files { get; set; } = new();
    public string DownloadUrl { get; set; } = "";
    public string State { get; set; } = "draft";
}

public sealed class RoutePackagePublication
{
    public long Revision { get; set; }
    public RoutePackageDescriptor Package { get; set; } = new();
}
