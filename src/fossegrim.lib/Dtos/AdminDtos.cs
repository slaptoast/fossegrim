namespace Fossegrim.Lib.Dtos;

public record MediaFolderDto(Guid Id, string Name, string Location, DateTime? DateAdded, DateTime? DateLastScanned);

public record AddMediaFolderRequest(string Name, string Location);

public record ScanRequest(string FolderPath, int? MaxDegreeOfParallelism = 4);

public record ApiErrorResponse(string Error);
