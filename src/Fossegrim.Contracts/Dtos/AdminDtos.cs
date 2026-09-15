namespace Fossegrim.Contracts.Dtos;

public record MediaFolderDto(Guid Id, string Name, string Location, DateTime? DateAdded, DateTime? DateLastScanned);

public record AddMediaFolderRequest(string Name, string Location);

public record ScanRequest(string FolderPath, int? MaxDegreeOfParallelism = 4);

public record ApiErrorResponse(string Error);

public class ScanResult
{
    public bool Success { get; set; }
    public int ScannedCount { get; set; }
    public int ItemsAdded { get; set; }
    public int ItemsUpdated { get; set; }
    public string FolderPath { get; set; } = string.Empty;
}
